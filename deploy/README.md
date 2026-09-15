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
2. **语料入库**：管理后台 → 语料管理 → 上传登记（选文档类别、密级等），
   提交后在解析队列里看它完成。
3. **问答**：退出 admin，用售前账号登录 → 智能问答提问，能看到流式回答与来源标注。
4. **切换对话模型**：admin → 管理后台 → 系统设置 → 对话模型：
   - 选 **DeepSeek**（在线）：填 API 密钥后应用，问答立即改走线上服务；
   - 选 **本地 Qwen**：服务跑在宿主机时，地址填 `http://host.docker.internal:8000/v1`
     （容器里 `127.0.0.1` 指容器自己，到不了宿主机）；
   - 随时可切回「内置演示应答」。改动即时生效，不用重启。
5. **案例回流（跨节点）**：售后账号在公司内网录一条故障案例并提交总部 →
   总部审核台用审核人账号在待审列表里通过/驳回 → 公司侧看到状态回传。
   两节点的同步令牌已在首启时按 `.env` 的 `SYNC_TOKEN` 预置好。
6. **共享库同步**：管理后台 → 知识库管理 → 集团共享库「从总部同步」。
7. **客户门户**（8082）：匿名搜索公开资料、匿名提交报修并凭单号+联系方式查进度；
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
- `.txt` / `.md` 纯文本文件不经过 MinerU，始终本地直接解析。

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
