# 企业内部智能体系统 · 后端（.NET 8）

对应《企业内部智能体系统产品需求文档》的服务端实现，按表 12-1 的开发依赖顺序推进。
原型见 `design/`（45 页 + 14 弹层，11 张画布）；本目录是同一份需求的代码侧。

## 当前进度（后端第 1 批 = 开发步骤 1–3）

| 步骤 | 内容 | 状态 |
|---|---|---|
| 1 | 权限骨架：用户/角色/密级/审计/互斥登录 | ✅ 已实现并冒烟验证 |
| 2 | 语料入库：上传/元数据/解析队列/切分/向量 | ✅ 已实现并冒烟验证 |
| 3 | 检索问答：混合检索/RRF/重排/阈值/来源/流式 | ✅ 已实现并冒烟验证 |
| 4–10 | 分块检视界面深化、台账与意图路由、故障案例、翻译、三级库同步、文档生成、回流审核与运维 | 实体与数据模型已就位，服务与接口按批次跟进 |

步骤 4 的后端能力（分块查看/编辑/删除/重解析）已随步骤 2 一并落地；其余批次沿用同一骨架。

## 结构

```
server/
  src/HT.Agent.Domain          实体（表 7-1 全量）、枚举、权限键、配置键
  src/HT.Agent.Application     接口抽象、DTO、纯逻辑（切分/分词/RRF——可单测）
  src/HT.Agent.Infrastructure  EF Core + pgvector、服务实现、模型客户端、解析工作器、迁移
  src/HT.Agent.Api             控制器、JWT、会话守卫、权限过滤器、SSE
  tests/HT.Agent.Tests         单元测试（19 个）
```

依赖：PostgreSQL 16 + pgvector + pg_trgm。业务数据与向量同库（7 章开篇：权限过滤、
元数据筛选与相似度检索在一次查询内完成，这是不用独立向量库的原因）。

## 启动

```bash
# 数据库（一次性）
sudo -u postgres psql -c "CREATE ROLE htagent LOGIN PASSWORD 'htagent-dev'"
sudo -u postgres psql -c "CREATE DATABASE htagent OWNER htagent"
sudo -u postgres psql -d htagent -c "CREATE EXTENSION vector; CREATE EXTENSION pg_trgm"

cd server && dotnet run --project src/HT.Agent.Api
# 启动时自动执行迁移与种子（Database:MigrateOnStartup=false 可关）
# 种子账号 admin / Admin@12345（Seed:AdminPassword 可改；上线前必须改）
```

`Models:UseStubs=true`（默认）时四个外部组件（对话/向量化/重排/解析引擎）用确定性桩，
无模型服务的环境整条链路可端到端跑通。生产在系统设置里配真实地址即可，代码不改（10.4）。

## 落进代码的关键需求语义

**权限过滤在候选选取之前（FR-4.4、7.2）。** `RetrievalService` 的向量路与关键词路
是同一条 SQL：密级 `= ANY(@cls)`、库分层、公司归属、元数据筛选全部在 `WHERE` 里，
然后才 `ORDER BY embedding <=> @qvec LIMIT k`。chunk 表冗余 kb_id 与 classification
并建组合索引，就是为了这一步。应用层不存在「先取回再剔除」的代码路径——售后查
机密文档时，机密块根本不进候选集（已验证）。

**管理员默认没有问答（表 3-1，歧义 2 的既定处理）。** admin 角色种子里没有 qa.* 权限键；
/api/retrieve 对 admin 返回 403 并写 authz.denied 审计。确需开通 = 给角色加键，
动作本身入审计（FR-7.2 角色映射可配）。

**互斥登录给明确原因（决策 4）。** JWT 带 sid 声明，SessionGuard 每请求与
app_user.active_session_id 比对；被顶下线的一端收到 401 `SESSION_SUPERSEDED` 与
一句能看懂的话，不是笼统的「登录已过期」。停用账号（FR-7.1）与重置口令走同一机制立即失权。

**没有自助改密（决策 2）。** 系统中改口令的唯一路径是 `POST /api/users/{id}/reset-password`
（user.manage 权限），重置记录经手人与时间。个人中心导出操作记录与管理员离职导出
（FR-7.5）共用同一查询与同一 CSV 编码——两个入口一份数据。

**上传后不自动解析（FR-1.2）。** 解析是 DB 队列 + 后台工作器（并行度可配，默认串行）。
引擎或向量化服务不可用 → 任务置 **Waiting** 按退避重试，不丢请求不报失败（10.3）；
内容性失败 → Failed 且 parse_error 记具体原因（FR-1.7）。两种状态在数据上就分开，
界面照 E3 原型呈现即可。

**切分策略按表 4-2 全量实现**（章节/条款/行/语义窗口/通用），全部保留相邻重叠。
表格行在所有策略下都不脱离表头——这条在冒烟中暴露为真实缺陷（按章节切分时
「最大工作压力」在表头里、块里只有数据行，检索命不中），已修复并加回归测试。

**运行参数改后即时生效（FR-9.6）。** 全部参数在 sys_config 表，`PUT /api/config` 写入
即失效缓存，下一次读取拿新值，不重启。冒烟里就是这样把阈值从 0.62 调到 0.30 的。

**阈值拦截如实说没有（FR-4.7）。** 重排最高分低于阈值 → `no_result` 事件 +
可能相关的文档名清单，不硬答。阈值默认 0.62 只是测试集初值，试运行期须按
真实问题集标定（E15 原型的标定记录区就是给这件事的）。

**审计按月分区、只写不改（FR-9.1、7.2）。** audit_log 是分区表（48 个月预建 + DEFAULT
兜底分区），问答留痕含提问、改写、命中分块与回答长度（FR-4.12）。

**中文全文检索。** 未引入分词扩展，应用侧二元切分 + `simple` 解析器 + GIN 表达式索引；
型号/报警代码类字符串（CJF-5L、E-17、GB/T）整串保留不切（7.2），索引侧与查询侧
同一实现（有测试锁定）。以后要换 zhparser，只动 `ChineseTokenizer` 与迁移。

**向量列维度是部署配置**（Persistence:VectorDimension，默认 1024）。换向量化模型的
全量重建流程（E15 原型的二次确认 + 哨兵）在运维批次实现；chunk.embedding_model
字段现在就记录每块用哪个模型算的，为「与当前模型不一致的分块数」哨兵备好数据。

## 接口（已实现部分，全表见 PRD 表 8-1）

```
POST /api/auth/login|logout   GET /api/auth/me
GET|POST /api/users           POST /api/users/{id}/role|deactivate|reactivate|reset-password
GET  /api/users/{id}/audit-export（CSV）   POST /api/users/{id}/terminals
GET|POST|PUT /api/roles /api/kbs           GET|POST /api/vocab/{key}
POST /api/documents（multipart）           GET /api/documents[?kbId|status|search]
POST /api/documents/parse|{id}/parse|{id}/reparse   GET /api/documents/{id}/chunks|queue
PUT|DELETE /api/chunks/{id}                GET /api/files/{docId}（按密级校验）
POST /api/retrieve                         POST /api/chat/completions（SSE）
GET  /api/qa-sessions[/{id}]               POST /api/qa-sessions/messages/{id}/feedback
GET|PUT /api/config                        GET /api/audit/logs[/export]
GET  /api/profile /api/profile/my-audit[/export]
```

## 对抗评审（2026-09-15）

第 1–2 批合入后跑了一轮五视角对抗评审（权限泄漏 / PRD 条款符合性 / 并发事务 /
接口契约 / 检索正确性）：54 条原始发现，每条经 3 个独立反驳者投票，**33 条确认、
21 条否决**，确认项已全部修复并配回归测试。修复中份量最重的几条：

- **forcedIntent 不是权限提升通道**：权限门移到意图确定之后，对自动判定与手动
  纠正一视同仁；无台账权限者强指 ledger 会记一条越权审计再按知识问答走
- **下载口与检索 SQL 同一套过滤**：密级之外补齐库层级、公司归属、撤回状态与
  角色数据范围——检索挡住的东西，换个接口不能就拿得到
- **客户数据范围落地**：CustomerNo 经客户设备台账关联项目行级过滤，即便把
  project.search 配给客户角色，可见面也只剩本人项目
- **共享库只读贯穿全部写路径**：分块编辑/删除/合并/拆分/重解析/元数据批改
  与上传同一约束（FR-2.2）
- **HNSW ef_search 按候选倍数生效**（7.2）：SET LOCAL 与向量查询同事务；
  ann_candidate_factor 从死配置变成真旋钮
- **合并/拆分行锁化**：FOR UPDATE 内校验与改写，合并后整篇 seq 重排不留空洞
- **SSE 错误事件**：响应流开始后出错不再硬断连接；断连时残答用无取消令牌落库，
  且问答留痕在检索内容出手之前先写（FR-4.12）
- **CSV 公式注入中和**、**词表/库清单接口对客户角色关闭**、**JWT 开发密钥
  须显式确认**、**全角型号 NFKC 归一化**、**所有切分策略补齐相邻重叠**
  （按行策略刻意例外，理由见代码注释）

否决的 21 条里有代表性的：ParseWorker 多实例竞态（单实例部署 + 已加崩溃回收）、
管理员种子含机密密级（管理员本就要做机密语料的检视）。全部裁决细节在评审
工作流日志中。

## 验证方式

```bash
dotnet test                 # 45 个单元测试：切分/分词/RRF/口令散列/意图路由/评审回归锁
```

冒烟已跑通的端到端序列（真库 + 桩模型）：登录 → 上传手册（按章节切分）→ 队列解析 →
分块含表头 → 售前检索命中/售后同问拿不到机密块 → SSE 问答带来源 → 互斥登录顶下线 →
管理员问答 403 → 越权与下载拒绝均入审计。
