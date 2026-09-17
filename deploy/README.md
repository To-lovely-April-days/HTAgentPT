# 部署包（Docker Compose）

一条命令把整套系统起在本机：数据库、公司节点、总部节点、公司内网界面、总部审核台、客户门户。

## 前置条件

- Docker（含 Compose v2，`docker compose version` 能出版本号即可）
  - Windows / macOS：装 Docker Desktop
  - Linux：装 docker-ce + docker-compose-plugin
- Git
- 首次构建需要联网拉取基础镜像与依赖包

## 一、获取代码（切到部署分支）

```bash
# 首次
git clone https://github.com/To-lovely-April-days/HTAgentPT.git
cd HTAgentPT
git switch claude/fervent-tesla-pf2u1d

# 已有本地仓库则
cd HTAgentPT
git fetch origin claude/fervent-tesla-pf2u1d
git switch claude/fervent-tesla-pf2u1d
git pull origin claude/fervent-tesla-pf2u1d
```

## 二、配置（可选）

```bash
cd deploy
cp .env.example .env
```

本机试用可以跳过这一步——compose 内置了同一套默认值。对外部署前务必在 `.env` 里更换
`POSTGRES_PASSWORD`、`JWT_SIGNING_KEY`（≥32 字节）、`SYNC_TOKEN`（≥16 位）。

## 三、启动

```bash
cd deploy        # 如果还没进来
docker compose up -d --build
```

首次构建约 5～10 分钟（编译后端 + 两个前端）。之后再启动只要几秒。

看状态与日志：

```bash
docker compose ps
docker compose logs -f server-company    # 后端日志（首启会自动建表+初始化数据）
```

`server-company` 与 `server-hq` 日志里出现监听 8080 的字样、`docker compose ps` 全部 Up，即启动完成。

## 四、访问入口

| 入口 | 地址 | 初始账号 |
|---|---|---|
| 公司内网界面 | http://localhost:8080 | admin / Admin@12345 |
| 总部审核台 | http://localhost:8081 | admin / Admin@12345（总部节点自己的账号） |
| 客户门户 | http://localhost:8082 | 匿名可用；客户账号由管理员创建 |
| 公司节点 API（调试） | http://localhost:8090/healthz | — |
| 总部节点 API（调试） | http://localhost:8091/healthz | — |

两个节点是两套独立数据库：公司节点与总部节点各有一个 admin，互不相通。

## 五、运行测试走查

默认配置下嵌入/重排/文档解析走内置演示实现，对话模型默认「内置演示应答」——
不接任何外部模型服务也能把全流程走通。

1. **建账号**：公司内网（8080）用 admin 登录 → 管理后台 → 用户与权限，
   建一个售前工程师、一个售后工程师账号。总部审核台（8081）用它自己的 admin 登录，
   建一个总部审核人账号。
2. **语料入库**：管理后台 → 语料管理 → 上传文档（选文档类别、密级等），
   提交后在解析队列里看它完成。方案、合同这类要关联项目编号的类别，
   编号不在台账里时可在上传弹窗里勾「同时登记到台账」，一次填完，不用切到台账页。
   Word（.docx）走本地解析，不需要外部解析服务；PDF、扫描件才需要接 MinerU。
3. **问答**：退出 admin，用售前账号登录 → 智能问答提问，能看到流式回答与来源标注。
4. **对话式方案生成**：售前账号 → 方案生成 → 选模板即进入对话式填写；或者直接在
   智能问答里说「帮我出一份××」，识别到生成意图后对话里会给出模板推荐卡，点选开聊。
   对话由「总指挥」派工：整句描述会抽值入表（「材质 316L，法兰结构」）、
   「有没有历史做过的」会查台账列历史项目卡（「用第二个做基准」即继承预填）、
   「推荐一下参数」会到基准文档与知识库检索给带依据的建议（「都采纳」落表）、
   提问（「设计压力和使用压力有什么区别」）会检索作答不动表；一句话可以混着说。
   报出客户后会主动翻台账递基准卡；进新章节前先给知识库里有依据的建议。
   交代工况（「我要做硝化反应」「介质有强腐蚀」「要过夜无人值守」）时，会按工艺常识
   判断影响哪些选型，逐项给建议值、理由与风险——这一档明确标注**没有文档依据**、
   界面上与资料建议分色，须工程师确认后才采纳；工况会记进会话，后面选型一直带着。
   接入 DeepSeek 后为完全体，演示档退化为规则派工 + 逐项问答。下拉项直接点选项按钮，
   随时说「跳过」「汇总」「生成文档」，「逐项核对」可切到工作台看每项来源。
   生成的 Word 页眉带「待复核」标注。
5. **切换对话模型**：admin → 管理后台 → 系统设置 → 对话模型：
   - 选 **DeepSeek**（在线）：填 API 密钥后应用，问答立即改走线上服务；
   - 选 **本地 Qwen**：服务跑在宿主机时，地址填 `http://host.docker.internal:8000/v1`
     （容器里 `127.0.0.1` 指容器自己，到不了宿主机）；
   - 随时可切回「内置演示应答」。改动即时生效，不用重启。
6. **案例回流（跨节点）**：售后账号在公司内网录一条故障案例并提交总部 →
   总部审核台用审核人账号在待审列表里通过/驳回 → 公司侧看到状态回传。
   两节点的同步令牌已在首启时按 `.env` 的 `SYNC_TOKEN` 预置好。
7. **共享库同步**：管理后台 → 知识库管理 → 集团共享库「从总部同步」。
8. **客户门户**（8082）：匿名搜索公开资料、匿名提交报修并凭单号+联系方式查进度；
   管理员建了客户账号后，客户登录可看自己的设备与工单。

## 常用命令

```bash
docker compose logs -f <服务名>     # 看日志：pg / server-company / server-hq / web / hq-web / portal
docker compose restart <服务名>     # 重启某个服务
docker compose down                 # 停止（保留数据）
docker compose down -v              # 停止并清空所有数据（数据库+文件），下次启动重新初始化
docker compose up -d --build        # 改了代码后重建并启动
```

## 接入 MinerU（真实文档解析，需要 NVIDIA 显卡）

默认的内置演示解析器只认纯文本文件。接上 [MinerU](https://github.com/opendatalab/MinerU)
之后，PDF / DOCX / PPTX / 扫描件都能做真实的版面解析（章节层级、表格结构、页码全保留）。

**第一步：构建 MinerU 镜像**（官方 Dockerfile，国内网络用 china 目录的）

```bash
curl -L -o Dockerfile.mineru https://github.com/opendatalab/MinerU/raw/master/docker/china/Dockerfile
docker build -t mineru:latest -f Dockerfile.mineru .
```

**第二步：验证 GPU 可用**（Docker Desktop 的 WSL2 后端自带 GPU 支持，装好 NVIDIA 驱动即可）

```bash
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
```

**第三步：叠加启动**

```bash
cd deploy
docker compose -f docker-compose.yml -f docker-compose.mineru.yml up -d --build
```

首次解析会自动下载模型（几 GB，只下一次，存在 `mineru-models` 卷里），
所以第一份文档会等得久一些，之后就快了。

注意事项：

- **数据库已经初始化过的环境**：解析地址的首启种子不再生效，登录 admin 到
  「系统设置 → 切分与解析」把解析服务地址改成 `http://mineru:8000/file_parse`（改后即时生效）。
- **MinerU 跑在另一台机器上**（比如专门的 GPU 工作站）：不用叠加文件，在那台机器上
  单独起 mineru-api，然后 `.env` 里设 `MODELS_PARSER=mineru` 重建后端容器，
  再到「系统设置」把解析服务地址改成 `http://<那台机器IP>:8000/file_parse`。
- **解析后端**：默认 `pipeline`（通用、显存要求低）。显存充足想要更高精度，可在
  「系统设置 → 切分与解析 → 解析后端」按所装 MinerU 版本支持的取值切换（如 vlm 系列）。
- **对照预览**：PDF 逐页渲染并按坐标叠亮框；Word 在浏览器里还原版面（表格、图片、样式都在，
  不出网也不需要转换服务），点右侧分块按文字定位并高亮，右上角可切「文本版」；
  Excel、演示稿等仍是文本版对照。
- `.txt` / `.md` 与 Office 文件（Word `.docx/.docm/.dotx`、Excel `.xlsx/.xlsm`、
  PowerPoint `.pptx/.pptm`）始终本地解析，不经过 MinerU：这些文件的标题层级、表格单元格、
  工作表、幻灯片在文件里是现成的，本地读更准也不出网（Excel 多数解析引擎还不收）。
  文中图片一并抽出（新式与老式两种写法都认），同一张图只入库一次，项目符号之类的小图标
  与浏览器显示不了的 EMF/WMF 会跳过。想让 Office 文件改走引擎（比如要版面切图），
  把「系统设置 → 切分与解析 → Word 本地解析」设为 `false`。
- 老版 `.doc` / `.xls` / `.ppt` 与 WPS 私有格式本地读不了（不是 XML 包）：用 Word/WPS
  另存为 `.docx` / `.xlsx` / `.pptx` 再传，系统会直接这样提示。

## 显卡未到？先用在线接口把全链路测起来

对话、向量化、重排、文档解析四项 GPU 能力，都在「系统设置」里做成了
**在线接口 / 本地部署 / 内置演示** 三选一的卡片，改后即时生效。显卡没到货时全部选在线：

| 能力 | 在线服务 | 准备什么 |
|---|---|---|
| 对话 | DeepSeek 官方 API | platform.deepseek.com 的 API key |
| 向量化 | 硅基流动 · BAAI/bge-m3（免费档可用） | siliconflow.cn 注册领 API key |
| 重排 | 硅基流动 · BAAI/bge-reranker-v2-m3（免费档可用） | 同上（同一个 key） |
| 文档解析 | MinerU 官方在线（每天 1000 页高优先级额度） | mineru.net 用户中心领令牌 |

步骤：admin 登录 → 系统设置，四张卡各选在线档、贴上密钥/令牌、点应用即可，
不用改任何部署文件。切了向量化后记得点页底的「重建不一致向量」。
在线档的向量化/重排与本地部署是**同一个模型**（bge 系列），显卡到货切回本地时
向量库不用重建。注意：选在线即意味着相应内容会发往外部服务，机密语料是否
允许出网请先按公司规定确认。

## 按选型表部署模型服务（GPU 机器到货后）

选型：对话与视觉模型 Qwen3.8-27B、向量化 bge-m3、重排 bge-reranker、推理框架 vLLM。
三个服务共卡部署，按显存占比分配（27B 模型是大头，向量化与重排都很小）：

```bash
pip install vllm   # 或用 vllm/vllm-openai 官方容器镜像

# 对话模型（端口 8000，OpenAI 兼容 /v1）
vllm serve Qwen/Qwen3.8-27B --port 8000 --gpu-memory-utilization 0.75

# 向量化 bge-m3（端口 8001，/v1/embeddings，1024 维——与系统向量列一致）
vllm serve BAAI/bge-m3 --task embed --port 8001 --gpu-memory-utilization 0.08

# 重排 bge-reranker（端口 8002，/rerank）
vllm serve BAAI/bge-reranker-v2-m3 --task score --port 8002 --gpu-memory-utilization 0.08
```

然后 admin 登录 → 系统设置，三张卡各选「本地部署」并把地址指到 GPU 机器
（设其地址为 `<GPU_IP>`，与主系统同机部署时用 `host.docker.internal`）：

- **对话模型**：选 Qwen/Qwen3.8-27B，地址 `http://<GPU_IP>:8000/v1`；
- **向量化模型**：选本地 bge-m3，地址 `http://<GPU_IP>:8001/v1/embeddings`；
- **重排模型**：选本地 bge-reranker，地址 `http://<GPU_IP>:8002/rerank`。

若之前一直用硅基流动在线档（同为 bge-m3），切回本地不触发重建；若从演示档切来，
页底哨兵会亮，点**重建不一致向量**逐篇补齐（期间这些内容按关键词检索，数字回落到 0 即完成）。

bge-m3 输出 1024 维，与部署包的向量列维度一致，不需要动数据库。总部节点如需同样能力，
在总部审核台的系统设置里做同样的配置（两个节点各自独立）。

## 说明

- **数据都在卷里**：数据库、上传文件、备份分别在 `pgdata`、`company-files`、`hq-files`
  三个 Docker 卷中，`down` 不丢数据，`down -v` 才清空。
- **首启初始化**：数据库第一次为空时自动建表并写入初始数据（公司、角色、admin、
  三级库、词表、同步令牌）。`.env` 里的 `ADMIN_PASSWORD`、`SYNC_TOKEN` 只在这一次生效，
  之后改密码/改配置一律走界面。
- **接入真实的嵌入/重排/解析服务**：在 compose 的两个 server 服务里加环境变量
  `Models__UseStubs: "false"`，再到「系统设置」里把各服务地址指到实际部署
  （宿主机上的服务用 `http://host.docker.internal:端口`），然后 `docker compose up -d` 重建。
- **端口被占**：改 `.env` 里对应的 `*_PORT` 再 `docker compose up -d`。
- **构建源**：后端镜像构建期除基础镜像外零联网（备份用的 `pg_dump` 及其依赖库
  直接取自数据库同款镜像）；web/portal 的 npm 源默认指向国内镜像 npmmirror，
  海外环境可 `--build-arg NPM_REGISTRY=https://registry.npmjs.org` 换回官方。
- **拉基础镜像失败**（`load metadata … EOF / not found`，或
  `failed to resolve source metadata for mcr.microsoft.com/dotnet/sdk:8.0`）：
  构建期连不上镜像仓，跟本项目代码无关。三步排障，从轻到重：
  ① 先 `set COMPOSE_BAKE=false`（PowerShell 用 `$env:COMPOSE_BAKE="false"`）
  再 `docker compose up -d --build`——退回传统构建器，本地已有的基础镜像不再联网核对；
  ② 仍不行则打开 `.env`，把「基础镜像加速」一节四行取消注释（切到 DaoCloud
  加速源）后重来；
  ③ 机器本来就上不了外网（客户现场常态）→ 走下面的「离线部署」，现场不构建。
- **离线部署**（现场无外网，或镜像仓怎么都拉不通）：在一台能上网的机器上把镜像
  做成一个 tar 带过去，现场只导入不构建。
  ```
  # 能上网的机器（deploy/ 目录下）
  ./offline/save-images.sh              # Windows：.\offline\save-images.ps1
  # 产出 htagent-images.tar（约 1.5~2 GB）

  # 把 htagent-images.tar 与整个 deploy/ 目录一起拷到现场，然后
  ./offline/load-images.sh              # Windows：.\offline\load-images.ps1
  ```
  导入后 `docker compose up -d` 不会再构建（镜像已在本地），也就不会去联网核对
  基础镜像。升级时在能上网的机器上重跑一次 save，把新 tar 带过去 load 即可。
