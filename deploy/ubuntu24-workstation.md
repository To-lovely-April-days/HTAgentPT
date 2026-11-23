# HTAgentPT：Ubuntu 24.04 工作站部署

适用分支：claude/fervent-tesla-pf2u1d。本指南按单机部署公司节点、总部节点、两个后台、客户门户和本地模型服务编写。

前置条件：Ubuntu 24.04、NVIDIA 驱动和 Docker 已安装，nvidia-smi 与 docker compose version 正常。以下命令在目标工作站执行。GPU 参数是首次部署的起点；完成生成、检索、业务请求和重启检查后，才能确认目标机器可用。

模型采用 Qwen 官方的 FP8 权重 Qwen/Qwen3.8-27B-FP8（官方说明效果和原版几乎一样，权重约为原版一半），示例推理版本为 vLLM 0.30.0（官方要求 ≥ 0.17.0）。省下的显存用来把上下文开到 32K，助手的「深度」思考档要这么长的上下文。具体 GPU/驱动兼容性以 PyTorch 自检和启动结果为准。已经按旧版指南装了原版权重的，看第 8 节升级。

## 1. 确认硬件和目录

```bash
cat /etc/os-release
nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv
docker compose version
df -hT / /srv
lsblk -o NAME,SIZE,FSTYPE,MOUNTPOINTS
```

核对实际显卡名称和显存，不以整机销售配置代替 GPU 查询结果。推荐模型、数据库和在线文档放 SSD；8TB 机械盘放归档和备份。下面 /srv/htagent 应位于有足够空间的 SSD 分区，先确认挂载位置。Docker 的数据目录也应位于 SSD；仅把代码放到 SSD 不会迁移现有 Docker 数据卷。

```bash
sudo apt-get update
sudo apt-get install -y git curl python3-venv nano
sudo install -d -o "$(id -u)" -g "$(id -g)" /srv/htagent
mkdir -p /srv/htagent/config /srv/htagent/models
```

## 2. 部署全部业务服务

首次克隆：

```bash
git clone --branch claude/fervent-tesla-pf2u1d --single-branch \
  https://github.com/To-lovely-April-days/HTAgentPT.git /srv/htagent/app
cd /srv/htagent/app
git rev-parse HEAD
```

如该目录已经有代码，先检查 git status，保留本地修改，再更新相同分支，不要重复克隆覆盖。

仓库中已跟踪 deploy/.env。用独立配置文件，避免部署密码进入 Git。Compose 项目名固定为 `htagent`；先查看已有项目与数据卷：

```bash
docker compose ls --all
docker volume ls --filter label=com.docker.compose.project=htagent
```

**下面的随机配置生成仅用于全新安装。** 如果已经部署过，保留原数据库密码、JWT 密钥、同步令牌及其他配置，将原部署使用的配置安全迁移到 `/srv/htagent/config/compose.env`，权限设为 `600`；不要重新生成。现有数据库不会因更改环境变量自动改密。

下面会拒绝覆盖配置文件，并检查已有同名容器和数据卷；密码不会打印到终端：

```bash
python3 - <<'PY'
from pathlib import Path
import os, re, secrets, subprocess
dest = Path("/srv/htagent/config/compose.env")
if dest.exists():
    raise SystemExit("配置已存在，请保留并检查，未覆盖。")
for command in (
    ["docker", "ps", "--all", "--filter", "label=com.docker.compose.project=htagent", "--format", "{{.ID}}"],
    ["docker", "volume", "ls", "--filter", "label=com.docker.compose.project=htagent", "--format", "{{.Name}}"],
):
    if subprocess.run(command, check=True, capture_output=True, text=True).stdout.strip():
        raise SystemExit("检测到既有 htagent 部署；请沿用原配置，不生成新数据库密码。")
text = Path("/srv/htagent/app/deploy/.env.example").read_text()
for name in ("POSTGRES_PASSWORD", "JWT_SIGNING_KEY", "SYNC_TOKEN", "ADMIN_PASSWORD"):
    text = re.sub(r"^" + name + r"=.*$", name + "=" + secrets.token_hex(32), text, flags=re.M)
for name, value in {"COMPANY_API_PORT": "127.0.0.1:8090", "HQ_API_PORT": "127.0.0.1:8091"}.items():
    text = re.sub(r"^" + name + r"=.*$", name + "=" + value, text, flags=re.M)
fd = os.open(dest, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, "w") as f:
    f.write(text)
print("已生成独立部署配置；请在本机编辑器查看 ADMIN_PASSWORD。")
PY
nano /srv/htagent/config/compose.env
```

ADMIN_PASSWORD 是首次初始化用的 admin 密码。已有数据库时修改这里不会更改现有账户密码。公司和总部各有独立账号、独立设置。

配置默认镜像源按仓库的 DaoCloud 设置。若服务器可直连官方仓库，可在此配置文件中设置：

```dotenv
DOTNET_REGISTRY=mcr.microsoft.com
NODE_IMAGE=node:22-alpine
NGINX_IMAGE=nginx:1.27-alpine
PG_IMAGE=pgvector/pgvector:pg16
```

启动（以后更新、查看日志也带同一个 --env-file 和 -f）：

```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml config --quiet

docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml up -d --build

docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml ps

curl -fsS http://127.0.0.1:8090/healthz
curl -fsS http://127.0.0.1:8091/healthz
```

同一局域网电脑通过服务器的局域网 IP 加端口访问：

| 入口 | 端口 |
|---|---:|
| 公司后台/业务界面 | 8080 |
| 总部审核台 | 8081 |
| 客户门户 | 8082 |

管理员用户名 admin，密码见独立配置文件。先登录验证，暂时使用演示模型。访问端口应限制在计划使用的内网；若客户门户需要公网访问，另行配置域名、HTTPS 和入口策略。

## 3. 安装推理环境并下载模型

不用为业务容器安装宿主机 .NET/Node。模型服务在宿主机的独立 Python 环境运行。

```bash
python3 -m venv /srv/htagent/vllm
/srv/htagent/vllm/bin/python -m pip install --upgrade pip
/srv/htagent/vllm/bin/python -m pip install 'vllm==0.30.0' huggingface_hub

/srv/htagent/vllm/bin/python - <<'PY'
import torch
print("torch:", torch.__version__, "CUDA runtime:", torch.version.cuda)
assert torch.cuda.is_available(), "PyTorch 无法使用 GPU；先处理驱动/运行时兼容性"
print("GPU:", torch.cuda.get_device_name(0))
print("capability:", torch.cuda.get_device_capability(0))
x = torch.ones((64,64), device="cuda")
print("CUDA tensor check:", (x @ x)[0,0].item())
PY

/srv/htagent/vllm/bin/hf download Qwen/Qwen3.8-27B-FP8 \
  --local-dir /srv/htagent/models/Qwen3.8-27B-FP8
/srv/htagent/vllm/bin/hf download BAAI/bge-m3 \
  --local-dir /srv/htagent/models/bge-m3
/srv/htagent/vllm/bin/hf download BAAI/bge-reranker-v2-m3 \
  --local-dir /srv/htagent/models/bge-reranker-v2-m3
```

nvidia-smi 中的 CUDA Version 是驱动支持上限，不等于已安装的 Python CUDA runtime。以以上自检为准，不额外混装另一套 torch/CUDA 包。若 Hugging Face 下载失败，保留错误信息后按服务器实际网络处理；不要关闭 TLS 校验。下载完成前不启动相应服务。

## 4. 先启动 Qwen，再启动两个检索模型

先取 Docker 默认网桥地址。模型绑定这个宿主机地址，让业务容器通过 host.docker.internal 访问，避免把无鉴权的模型接口绑定到所有网卡：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
printf '%s\n' "$MODEL_BIND"
```

首次在前台运行 Qwen，观察完整日志：

```bash
/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/Qwen3.8-27B-FP8 \
  --served-model-name Qwen/Qwen3.8-27B \
  --host "$MODEL_BIND" --port 8000 \
  --gpu-memory-utilization 0.80 \
  --max-model-len 32768 --max-num-seqs 4 \
  --max-num-batched-tokens 8192 \
  --language-model-only --enable-prefix-caching \
  --reasoning-parser qwen3 \
  --enable-auto-tool-choice --tool-call-parser qwen3_coder \
  --default-chat-template-kwargs '{"enable_thinking":false}'
```

这组参数是首次试运行起点，不是已测出的最大并发。每个参数为什么这样设：

- `--served-model-name Qwen/Qwen3.8-27B`：权重换成 FP8 也沿用这个名字，后台的模型名不用改。
- `--enable-auto-tool-choice --tool-call-parser qwen3_coder`：**必须带**。没有这两个参数，模型不能调用工具，助手会悄悄退回老的问答流程，查技能、计算、查项目、查价格都用不上。回答上方「查了几步」的思考那一行会写「模型服务没开工具调用」。
- `--reasoning-parser qwen3`：把思考过程和正文分开，不带的话思考内容会混进回答。
- `--max-model-len 32768`：FP8 权重约 28GB。这个模型 64 层里只有 16 层是全注意力，上下文占的显存很小（按配置估算每千个 token 约 64MB），32K、4 路并发约 8GB。显存不够时先把 `--max-num-seqs` 降到 2，或加 `--kv-cache-dtype fp8`。
- `--default-chat-template-kwargs '{"enable_thinking":false}'`：只是没带参数的请求的默认值。助手每次提问都会按思考档位带上思考开关和强度，以请求里的为准。

启动后到后台「系统设置 · 思考档位」把「模型服务的上下文」改成和 `--max-model-len` 一样的数（32768），深度档才会开放；助手也按这个数算每轮回答还能写多长。若启动报告模型架构或模板参数不支持，核对下载模型的 config.json 和 vLLM 支持情况；若显存不足，查看实际占用与 KV cache 日志，再调整，不能只提高所有服务的利用率。

另开终端（重新设置 MODEL_BIND）验证真实生成：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
curl -fsS "http://$MODEL_BIND:8000/v1/models"
curl -fsS "http://$MODEL_BIND:8000/v1/chat/completions" \
  -H 'Content-Type: application/json' \
  -d '{"model":"Qwen/Qwen3.8-27B","messages":[{"role":"user","content":"请用一句话介绍你能提供什么帮助。"}],"max_tokens":128,"stream":false}'
```

必须返回非空 message.content；不能只检查端口。

再验证工具调用和思考强度。第一条必须返回 `tool_calls`（不是报错，也不是把调用写成文字）；第二条把思考强度设成 low，必须正常返回，`reasoning_content` 或 `reasoning` 里有思考过程：

```bash
curl -fsS "http://$MODEL_BIND:8000/v1/chat/completions" \
  -H 'Content-Type: application/json' \
  -d '{"model":"Qwen/Qwen3.8-27B","messages":[{"role":"user","content":"把 3 bar 换算成 kPa"}],"max_tokens":512,"tool_choice":"auto","chat_template_kwargs":{"enable_thinking":false},"tools":[{"type":"function","function":{"name":"convert_unit","description":"单位换算","parameters":{"type":"object","properties":{"value":{"type":"number"},"from":{"type":"string"},"to":{"type":"string"}},"required":["value","from","to"]}}}]}' \
  | python3 -c 'import json,sys; m=json.load(sys.stdin)["choices"][0]["message"]; assert m.get("tool_calls"), m; print("tool call OK:", m["tool_calls"][0]["function"])'

curl -fsS "http://$MODEL_BIND:8000/v1/chat/completions" \
  -H 'Content-Type: application/json' \
  -d '{"model":"Qwen/Qwen3.8-27B","messages":[{"role":"user","content":"316L 能不能用在 80 度的 10% 盐酸里？一句话回答。"}],"max_tokens":2048,"chat_template_kwargs":{"enable_thinking":true,"reasoning_effort":"low"}}' \
  | python3 -c 'import json,sys; m=json.load(sys.stdin)["choices"][0]["message"]; r=m.get("reasoning_content") or m.get("reasoning") or ""; print("reasoning chars:", len(r)); print("answer:", m["content"])'
```

把第二条的 `low` 换成 `xhigh` 再跑一次，思考的字数应该明显更多；如果两次差不多，说明这个 vLLM 版本没有把思考强度传进模板，三档就只靠「最多查几轮」和「回答最长」区分，功能照样能用。

Qwen 能正常生成后，再分别在两个终端启动：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/bge-m3 \
  --served-model-name BAAI/bge-m3 \
  --host "$MODEL_BIND" --port 8001 \
  --runner pooling --dtype float16 \
  --max-model-len 8192 --max-num-batched-tokens 8192 \
  --gpu-memory-utilization 0.05
```

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/bge-reranker-v2-m3 \
  --served-model-name BAAI/bge-reranker-v2-m3 \
  --host "$MODEL_BIND" --port 8002 \
  --runner pooling --dtype float16 \
  --max-model-len 8192 --max-num-batched-tokens 8192 \
  --gpu-memory-utilization 0.05
```

80%/5%/5% 是总显存预算起点，三个进程还有额外开销；以三者同时存活并完成请求为准。不能据标称 72GB 保证全部服务一定共卡运行；放不下时先降 Qwen 的 `--max-num-seqs` 或上下文，再考虑调整部署方案。

验证向量维度和重排：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
set -o pipefail
curl -fsS "http://$MODEL_BIND:8001/v1/embeddings" \
  -H 'Content-Type: application/json' \
  -d '{"model":"BAAI/bge-m3","input":["反应釜维护"]}' \
  | python3 -c 'import json,sys; v=json.load(sys.stdin)["data"][0]["embedding"]; assert len(v)==1024; print("embedding: 1024 dimensions, OK")'

curl -fsS "http://$MODEL_BIND:8002/v1/rerank" \
  -H 'Content-Type: application/json' \
  -d '{"model":"BAAI/bge-reranker-v2-m3","query":"反应釜维护","documents":["反应釜的密封件需要定期检查。","明天的天气预报。"]}'
```

数据库使用 vector(1024)。重排请求应返回两个文档的分数和索引，并核对维护文档相关性更高。

## 5. 后台切到本地服务

公司管理员登录 → 系统设置，分别选择本地提供方：

| 功能 | 地址 | 服务名称（启动时必须完全一致） |
|---|---|---|
| 本地 Qwen | http://host.docker.internal:8000/v1 | Qwen/Qwen3.8-27B |
| 本地向量化 | http://host.docker.internal:8001/v1/embeddings | BAAI/bge-m3 |
| 本地重排 | http://host.docker.internal:8002/v1/rerank | BAAI/bge-reranker-v2-m3 |

这些名称与当前 UI 实际发送值一致。不要替换为 chat-27b、bge-m3 等短别名，否则请求的 model 字段会与服务不匹配。

不要在后台填写 127.0.0.1，它在后端容器里指向容器自身。compose 已配置 host.docker.internal:host-gateway。可在项目目录执行以下命令，确认容器解析到的地址与 MODEL_BIND 一致：

```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml exec -T server-company getent ahostsv4 host.docker.internal
```

若不同，先核对 Docker 的 host-gateway 自定义配置和实际宿主机地址，再确定模型监听地址。本地全新无认证服务不需要外部 API key；如果之前用过在线 API，本地卡不会自动清空旧 key，应检查对应配置。

各卡选择本地后约 5 秒生效，不需要先设 MODELS_USESTUBS=false。公司和总部设置彼此独立；总部也需要模型时，在总部后台重复配置。

若已用演示模型入库，点击“重建不一致向量”，等待数量归零。

## 6. 常驻运行与验收

前台验证通过后，把三条已验证的启动命令分别放入 systemd 服务。Qwen 示例（先在前台终端按 Ctrl-C 停止你启动的 Qwen，避免占用相同端口和显存）：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
HT_RUN_USER="$(id -un)"
if sudo test -e /etc/systemd/system/htagent-chat.service || sudo test -L /etc/systemd/system/htagent-chat.service; then
  printf '%s\n' 'htagent-chat.service 已存在，请保留并核对，未覆盖。' >&2
  exit 1
fi
sudo sh -c 'set -Ce; cat > "$1"' sh /etc/systemd/system/htagent-chat.service <<EOF
[Unit]
Description=HTAgent local Qwen service
After=network-online.target docker.service
Wants=network-online.target
Requires=docker.service

[Service]
User=$HT_RUN_USER
WorkingDirectory=/srv/htagent
ExecStart=/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/Qwen3.8-27B-FP8 --served-model-name Qwen/Qwen3.8-27B --host $MODEL_BIND --port 8000 --gpu-memory-utilization 0.80 --max-model-len 32768 --max-num-seqs 4 --max-num-batched-tokens 8192 --language-model-only --enable-prefix-caching --reasoning-parser qwen3 --enable-auto-tool-choice --tool-call-parser qwen3_coder --default-chat-template-kwargs '{"enable_thinking":false}'
Restart=on-failure
RestartSec=15
TimeoutStopSec=120

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now htagent-chat
sudo journalctl -u htagent-chat -n 80 --no-pager
```

先停止前台的向量化和重排进程，再创建它们的服务。如果前台验证时调整过启动参数，下面也使用调整后的值。

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
HT_RUN_USER="$(id -un)"
for service in embedding rerank; do
  unit="/etc/systemd/system/htagent-$service.service"
  if sudo test -e "$unit" || sudo test -L "$unit"; then
    printf '%s\n' "$unit 已存在，请保留并核对，未覆盖。" >&2
    exit 1
  fi
done
for spec in \
  'embedding bge-m3 BAAI/bge-m3 8001' \
  'rerank bge-reranker-v2-m3 BAAI/bge-reranker-v2-m3 8002'; do
  read -r service model_dir model_alias port <<< "$spec"
  sudo sh -c 'set -Ce; cat > "$1"' sh "/etc/systemd/system/htagent-$service.service" <<EOF
[Unit]
Description=HTAgent local $service service
After=network-online.target docker.service
Wants=network-online.target
Requires=docker.service

[Service]
User=$HT_RUN_USER
WorkingDirectory=/srv/htagent
ExecStart=/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/$model_dir --served-model-name $model_alias --host $MODEL_BIND --port $port --runner pooling --dtype float16 --max-model-len 8192 --max-num-batched-tokens 8192 --gpu-memory-utilization 0.05
Restart=on-failure
RestartSec=15
TimeoutStopSec=120

[Install]
WantedBy=multi-user.target
EOF
done
sudo systemctl daemon-reload
sudo systemctl enable --now htagent-embedding htagent-rerank
sudo systemctl status htagent-chat htagent-embedding htagent-rerank --no-pager
```

三者都托管后，关闭 SSH 不会丢失模型进程。业务容器已有 restart: unless-stopped。再次执行第 4 节的三类功能请求；服务显示 active 仍不足以证明模型已加载完毕。

验收业务流程：

1. 公司和总部 admin 均能登录；创建售前用户，admin 默认没有问答权限。
2. 上传一个真实 DOCX，登记信息后主动提交解析，等解析完成。
3. 用售前用户提问，检查回答正文、来源引用和命中文档。
4. 测试文档生成、翻译，再测试总部审核/共享库同步和客户门户。
5. 重启服务后再次验证数据库数据、已上传文档和模型服务仍可用。

## 7. PDF、扫描件和备份

DOCX/XLSX/PPTX 有本地解析，先用 DOCX 完成知识库验收。PDF/扫描件再接 MinerU；Qwen 本身不能替代当前系统的解析接口。

仓库 deploy/docker-compose.mineru.yml 的实际映射为宿主机 8093 → 容器 8000，MinerU 4 V1 基地址为 http://mineru:8000。它与三个模型的 8000/8001/8002 宿主机端口不冲突。该叠加文件没有给 MinerU 设置显存硬预算，应在确认三模型占用后再确定解析档位、并发和显存；不能直接保证四个 GPU 服务同时满载。

使用 NVIDIA GPU 容器需要 nvidia-container-toolkit；nvidia-smi 和普通 Docker 正常还不足以证明 GPU 容器可用。按 NVIDIA 官方安装文档配置后，验证 GPU 容器，再按 MinerU 官方当前安装文档构建镜像，最后叠加 compose。

数据库和业务文件位于 Docker 持久卷。备份到 8TB 盘时，要把实际已挂载的备份目录挂入后端容器后再在系统设置选择该容器路径，不能直接填宿主机不可见路径。先做一次备份和恢复验证。日常停止使用 docker compose down，不使用 down -v（后者会删除业务数据卷）。

## 8. 升级：换 FP8 权重、开工具调用、上下文 32K

已经按旧版指南装好、正在跑原版权重（8K 上下文、没开工具调用）的机器，按下面换。业务数据和数据库不受影响，只是模型服务重启一次（约几分钟，期间助手答不了）。

先下载 FP8 权重（约 30GB，原版权重先留着，换回去只要改回启动命令）：

```bash
/srv/htagent/vllm/bin/hf download Qwen/Qwen3.8-27B-FP8 \
  --local-dir /srv/htagent/models/Qwen3.8-27B-FP8
```

用 systemd 的覆盖文件换启动命令，不改原来的服务文件（要换回去删掉这个覆盖文件即可）：

```bash
MODEL_BIND="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')" || exit 1
: "${MODEL_BIND:?未取得 Docker 网桥地址，请先检查 Docker 网络}"
sudo mkdir -p /etc/systemd/system/htagent-chat.service.d
sudo tee /etc/systemd/system/htagent-chat.service.d/fp8.conf >/dev/null <<EOF
[Service]
ExecStart=
ExecStart=/srv/htagent/vllm/bin/vllm serve /srv/htagent/models/Qwen3.8-27B-FP8 --served-model-name Qwen/Qwen3.8-27B --host $MODEL_BIND --port 8000 --gpu-memory-utilization 0.80 --max-model-len 32768 --max-num-seqs 4 --max-num-batched-tokens 8192 --language-model-only --enable-prefix-caching --reasoning-parser qwen3 --enable-auto-tool-choice --tool-call-parser qwen3_coder --default-chat-template-kwargs '{"enable_thinking":false}'
EOF
sudo systemctl daemon-reload
sudo systemctl restart htagent-chat
sudo journalctl -u htagent-chat -n 80 --no-pager
```

日志里出现服务就绪后，按第 4 节最后的三条命令验证（普通生成、工具调用、思考强度）。然后到后台「系统设置 · 思考档位」把「模型服务的上下文」改成 32768，点保存。

显存不够起不来时，看日志里的 KV cache 报错：先把命令里的 `--max-num-seqs 4` 改成 2 再试；还不行就把 `--max-model-len` 改回 16384，后台也填 16384（这时深度档不开放）。

要换回原版：

```bash
sudo rm /etc/systemd/system/htagent-chat.service.d/fp8.conf
sudo systemctl daemon-reload
sudo systemctl restart htagent-chat
```

## 9. 升级：助手做文件（Excel、Word、PPT、PDF）

助手在代码解释器里写代码做文件，交出前由 office 容器检查（Excel 用 LibreOffice 重算公式查错）。要重建这两个容器：代码解释器装上做文件的库，office 容器加上表格和演示组件。业务数据不受影响。

```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml build sandbox office
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d sandbox office
```

（如果平时还带 `-f deploy/docker-compose.mineru.yml`，两条命令里照样带上。）

验证两个容器都装好了：

```bash
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml exec -T sandbox \
  python -c "import openpyxl, docx, pptx, pypdf, reportlab, htfiles; print('file libs ok')"
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml exec -T office \
  python -c "import openpyxl, pypdf, subprocess; print(subprocess.run(['soffice','--version'],capture_output=True,text=True).stdout)"
```

然后到后台「系统设置 · 助手工具」，「助手做文件」那一组：Excel、Word 默认开着，状态应该是「运行环境和检查服务正常」；PPT、PDF 默认关着，用测试题跑过、成功率够了再开。在对话里试一句「出一份 30L 加氢釜的报价单 Excel」，回答里应该出现文件卡，能预览、能下载。

## 10. 升级：内置浏览器（助手能打开网页、读、点、填）

新加一个 browser 容器：Chromium 用 Debian 软件源装，Playwright 用 pip 装，不用从国外下载浏览器。业务数据不受影响。

```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml build browser
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d browser
```

（平时还带 `-f deploy/docker-compose.mineru.yml` 的，照样带上。）

验证浏览器能打开公网网页、拦得住内网：

```bash
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml exec -T browser python - <<'PY'
import json, urllib.request
def call(path, body):
    req = urllib.request.Request("http://127.0.0.1:8100" + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
    try:
        return json.load(urllib.request.urlopen(req, timeout=60))
    except urllib.error.HTTPError as e:
        return json.load(e)
print(call("/s/selftest-0001/open", {"url": "https://www.htlab.cn/"}))
print(call("/s/selftest-0001/open", {"url": "http://db:5432/"}))
urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:8100/s/selftest-0001", method="DELETE"))
PY
```

第一行应该返回网页标题，第二行应该是「这是公司内网或本机地址，默认不让打开」。

然后到后台「系统设置 · 助手工具」，「外部数据」里的「内置浏览器」状态应是「浏览器服务正常」。员工要用的内部系统（如 ERP），在它的「设置」里加进「允许打开的内网地址」。同时开几个浏览器、每个对话最多几个网页、闲置多久关，在 compose.env 里用 BROWSER_MAX_SESSIONS、BROWSER_MAX_TABS、BROWSER_IDLE_MINUTES 改，默认 4、8、15。

## 11. 升级：内置浏览器第二步（实时画面、下载进附件、看截图）+ 代码解释器修复

这次要重建 browser 和 sandbox 两个容器：
- browser：画面实时推过来（有人在看才推）；网页下载的文件先留在浏览器里，员工在面板上点「放进对话附件」才收进对话。
- sandbox：修了一个问题——后端发给代码解释器的请求以前不带长度（分块发送），代码解释器读到的是空的，「运行计算」「做文件」会没有结果。后端和代码解释器两边都改了，重建后才生效。

```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml build browser sandbox
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d browser sandbox
```

验证：在对话里让助手「用 Python 算一下 3.14*0.25^2/4」，「查了几步」里「运行计算」应该有输出（0.049…）。

「看网页画面」要模型能看图：后台「系统设置 · 模型与运行参数」的「图片理解」里打开，并且模型服务启动命令去掉 `--language-model-only`（会多占一些显存），或者另配一个看图模型的地址。不开也不影响其他功能，助手只是看不了截图。

## 12. 升级：国家目录（重点监管危险化工工艺、危化品）和四个新技能

这次只更新后端和前端，有数据库迁移（新加三张表），后端启动时自动执行，并自动导入系统自带的目录数据（不用另外下载）：
- 重点监管危险化工工艺目录（2013 年完整版，18 类）
- 危险化学品目录（2015 版）+ 危险化学品分类信息表（2828 种，含危险性类别、剧毒标记；柴油条目按 2022 年调整）
- 易制爆危险化学品名录（2017 年版）、易制毒化学品分类和品种目录（含到 2021 年的增补）

原来「化学品安全数据」要手动放的 `data/hazchem.csv` 不用了，改用这份数据。

```bash
cd /srv/htagent/app
git pull
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d --build
```

验证：
- 后台「能力域 · 国家目录」能看到 18 类工艺、2828 种危险化学品
- 后台「系统设置 · 工具管理」里「查危险工艺和危化品」显示「目录已导入」
- 对话里问「客户做催化加氢，介质氢气、甲醇，釜要配哪些安全控制？」，「查了几步」里有「查危险工艺和危化品」，回答里有加氢工艺的原文和介质的危险性类别

目录以后更新了，在「国家目录」里改对应条目；改过的条目系统更新不会覆盖，工艺可以「恢复原文」。

## 13. 升级：流程模拟（DWSIM）

这次多一个工具容器 dwsim：DWSIM 官方的无界面版（dwsim-mcp 10.2.16，开源 GPL-3），只给后端用，不对外开端口。约 350 MB 镜像、1–2 GB 内存。

1. 在 `/srv/htagent/config/compose.env` 里加一个口令（随便一串字母数字，后端和 dwsim 用同一个）：

```bash
echo "DWSIM_TOKEN=$(openssl rand -hex 16)" >> /srv/htagent/config/compose.env
```

镜像在 ghcr.io 上，国内拉不动时在 compose.env 里加 `DWSIM_IMAGE=` 换成可用的镜像地址（同一个版本 10.2.16）。

2. 更新并启动：

```bash
cd /srv/htagent/app
git pull
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d --build
```

3. 验证：
- 后台「系统设置 · 工具管理」里「流程模拟（DWSIM）」显示「DWSIM 服务正常」
- 对话里问「苯加氢中试：苯 10 kg/h、氢气 0.448 kmol/h，3 MPa、180 ℃ 反应，转化率 99%，出口冷到 40 ℃ 气液分离，算各物流和撤热」：回答里有流程图、物流表；「DWSIM 原图」能看到 DWSIM 画的图；能下载 .dwxmz 文件，用 DWSIM 桌面版打开

说明：
- 模拟文件经 dwsim-files 卷交给后端（两个容器都挂在 /dwsim-files）；后端取走后会删，删不掉的（权限不同）每份约 50 KB，可以不管
- 第一版能算：混合器、分流器、加热器、冷却器、泵、压缩机、阀、闪蒸罐、转化反应器。精馏塔、循环流程以后再开

## 14. 升级：外形图和三维模型、反应平衡、温度控制

这次要重建代码解释器（sandbox）：多装了 CadQuery（三维建模，Apache-2.0）、ezdxf（DXF 图纸，MIT）、Cantera（化学平衡，BSD），镜像大约多 400 MB；还多了一个画外形图的小工具 htcad。另有三个新技能：外形图和三维模型、反应平衡、温度控制与动态模拟。

```bash
cd /srv/htagent/app
git pull
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml build sandbox
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d --build
```

验证：
- 对话里问「30L 加氢釜，内径 300、直边 420、夹套，顶部 5 个接管、底部放料，出外形图和三维模型」：回答里有两个文件卡（.dxf、.step），下载后 CAD 能打开
- 问「苯加氢 3 MPa、氢苯比 3.5，180 到 350 ℃ 的平衡转化率」：350 ℃ 约 91.7%

## 15. 升级：PPT 模板库（按模板做 PPT）

这次要重建两个工具容器：代码解释器（sandbox）多了套模板的小工具 htppt；office 容器多了 poppler（PPT 每页转图）和几款开源英文字体。
数据库会自动加一张表（PptTemplates）。30 套开源模板（SlidesCarnival，CC BY 4.0，约 100 MB）**不在 git 里**——模板许可不许把模板文件原样转发，
仓库里只有清单（deploy/ppt-templates/templates.json）。先把开发机上的模板文件拷到服务器同一个目录：

```bash
# 在开发机上（项目目录里）执行；用户名、服务器地址换成自己的
scp deploy/ppt-templates/*.pptx 用户名@服务器地址:/srv/htagent/app/deploy/ppt-templates/
```

后端启动约 1.5 分钟后自动导入（每套分析每页、转缩略图，30 套要几分钟）。

```bash
cd /srv/htagent/app
git pull
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml build sandbox office
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml -f deploy/docker-compose.tools.yml up -d --build
```

然后：
- 后台「工具」里打开「PPT 方案汇报」（默认关着），按需设可用角色
- 后台「PPT 模板」：等「系统自带 30 / 30 已导入」；没导入完、或「用不了」里写着沙箱 / office 连不上，等容器起来后点「现在导入」或「重新分析」
- 看着缩略图不好看的模板直接关掉，员工就选不到了

验证：
- 对话里点「＋ → PPT 模板」能看到模板网格，选一套
- 问「按 30L 加氢釜方案做一份给客户讲的 PPT，10 分钟」：回答里有 PPT 文件卡，一排每页的预览图，下面写着检查结果；下载后 PowerPoint、WPS 能打开，最后一页右下角有一行模板署名
- 文件卡上点「换个风格」选另一套：内容不变，换成新模板

## 16. 升级：工具按需加载、听懂人话

这次只改后端和网页，不加容器、不改数据库，照常 `git pull` 后 `up -d --build`。

- 助手的工具分两类：查资料、查项目、计算、联网搜索这些常用的每次都带；流程模拟、做 PPT、浏览器这类专用的平时只列一行用途，用得上才把完整说明交给模型（系统按问题先挑，模型也能自己加载）。每轮交给模型的工具说明从约 2.2 万字降到约 1 万字。
- 后台「系统设置」里多一张「工具按需加载」卡片：开关（关掉就和以前一样全部都带）、按问题先挑的门槛（默认 0.6）、常驻的专用工具。
- 回答里的图表、流程图这类组件如果明显变少，在卡片里把「回答组件」设成常驻。
- 验证：跑 `python3 deploy/eval/run_eval.py deploy/eval/questions.intent.jsonl`，和上次比「查对地方」「没推脱」；对话里问「苯加氢中试算一下物料衡算」，思考过程里应能看到「流程模拟」（系统先挑上）或「加载工具」一步。

## 17. 导入：CSB 事故调查报告（反应失控、聚合失控）

美国化学品安全委员会（CSB）的公开报告，导进知识库「安全事故案例（公开资料）」，助手讲反应风险、讲为什么要配联锁和泄放时引用。报告是公有领域，可以放心用。PDF 不在 git 里，脚本从 csb.gov 下载。

1. 在服务器上（用有「资料管理」「知识库管理」权限的管理员账号；登录会把这个账号在浏览器上的登录挤掉，跑完重新登录）：

```bash
cd /srv/htagent/app && git pull
```

```bash
export HT_URL=http://localhost:8080 HT_USER=管理员账号 HT_PASS='密码'
```

```bash
python3 deploy/safety-corpus/import_csb.py
```

2. 后台「资料管理」里看解析进度（英文报告，每份几十页，几分钟到十几分钟）。
3. 服务器上不了 csb.gov 时：在能上的电脑上按 `deploy/safety-corpus/csb_reports.json` 里的地址下载（文件名照 `file`），拷到服务器某个目录，再运行 `python3 deploy/safety-corpus/import_csb.py --dir 那个目录`。重跑不会重复导入。
4. 验证：对话里问「丙烯酸聚合放大时冷凝器能力不够会怎样，有没有真实事故」，回答里应引用 CSB 报告。

## 18. 导入：一个文件夹的资料批量进知识库（标准、手册、公司资料）

把资料按子文件夹整理好（`标准规范/`、`已作废标准/`、`设计手册/`、`公司资料/`；别的子文件夹名按「库名 = 文件夹名、类别 = 资料」导），拷到服务器，例如 `~/kb-import`。脚本会：建库、在词表里补「通用」设备类型、规整标准标题（「GBT 150.1-2024」→「GB/T 150.1-2024」，从文件名取年份）、已作废的标题前加【已作废】、跳过库里已有的同名文件、按文件夹顺序提交解析。密级默认「内部」。

```bash
cd /srv/htagent/app
export HT_URL=http://localhost:8080 HT_USER=admin HT_PASS='密码'
python3 deploy/kb-import/import_folder.py ~/kb-import --dry-run
python3 deploy/kb-import/import_folder.py ~/kb-import
```

`--dry-run` 先列出每个文件会进哪个库、标题是什么；老格式（.doc / .xls）和读不了的格式会列在「跳过」里。扫描版的标准和手册要逐页识别，上千兆的资料全部解析完可能要一天，进度在后台「资料管理」里看。

## 19. 外网访问：公司没有公网 IP 时，用一台云服务器中转组网（WireGuard）

不要把 8080 映射到公网：服务器上有机密资料，模型接口也没有鉴权。做法是组一个加密的虚拟局域网：租一台有公网 IP 的小云服务器当「中转站」，公司这台服务器和要远程用的电脑、手机都连上它，彼此用 10.8.0.x 的地址互访。公网上看不到公司服务器，也不用备案、域名和证书。

```
远程电脑/手机 ──WireGuard──▶ 云服务器（中转，wg-easy） ◀──WireGuard── 公司服务器（主动连出去，不用公网 IP）
```

流量都经云服务器转一手，速度取决于它的带宽：聊天很轻，几 Mbps 就够；远程上传大文件会慢（5 Mbps 传 100 MB 约 3 分钟）。

### 19.1 云服务器（一次性）

1. 买一台国内云的轻量服务器，Ubuntu 24.04，1 核 1G 就够，带宽按远程上传文件的需要选。安全组/防火墙放行 **UDP 51820**（只放这一个；管理页面不对公网开）。
2. 装 Docker 后，建 `/srv/wg-easy/docker-compose.yml`：

```yaml
services:
  wg-easy:
    image: ghcr.io/wg-easy/wg-easy:15
    container_name: wg-easy
    environment:
      INSECURE: "true"          # 管理页面只绑本机，经 SSH 隧道访问，所以用 http
    volumes:
      - ./data:/etc/wireguard
      - /lib/modules:/lib/modules:ro
    ports:
      - "51820:51820/udp"
      - "127.0.0.1:51821:51821/tcp"
    cap_add: [NET_ADMIN, SYS_MODULE]
    sysctls:
      - net.ipv4.ip_forward=1
      - net.ipv4.conf.all.src_valid_mark=1
    restart: unless-stopped
```

```bash
cd /srv/wg-easy && sudo docker compose up -d
```

ghcr.io 拉不动时换国内镜像加速前缀。

3. 在自己电脑上开 SSH 隧道进管理页面：

```bash
ssh -L 51821:127.0.0.1:51821 用户@云服务器IP
```

浏览器打开 http://127.0.0.1:51821，按向导建管理员账号，「主机」填云服务器公网 IP、端口 51820。
4. **先改设置再加客户端**：设置里把默认 Allowed IPs 改成 `10.8.0.0/24`（只有访问组网内地址的流量走隧道，上网照旧走本地），Persistent Keepalive 设 25。

### 19.2 公司服务器接入（一次性）

1. 在 wg-easy 里新建客户端「htagent-server」，下载它的配置文件。
2. 打开看一眼：`AllowedIPs` 必须是 `10.8.0.0/24`（若是 `0.0.0.0/0` 就改掉，否则服务器所有上网流量都会绕到云服务器）；有 `PersistentKeepalive = 25`（没有就在 `[Peer]` 下加一行——公司服务器在 NAT 后面，靠它保持通道）；删掉 `DNS =` 那一行。
3. 拷到服务器上启用：

```bash
sudo apt install -y wireguard
sudo install -m 600 htagent-server.conf /etc/wireguard/wg0.conf
sudo systemctl enable --now wg-quick@wg0
ip addr show wg0
```

记下 `inet 10.8.0.x`：这就是服务器的组网地址。`sudo wg` 里能看到 `latest handshake` 就是通了，重启后自动连上。

### 19.3 远程电脑、手机接入（每人一份）

1. 在 wg-easy 里每人建一个客户端（用人名起名，离职时删掉即失效）。
2. 装 WireGuard 官方客户端（Windows/macOS/iOS/Android 都有），电脑导入配置文件，手机扫二维码。同样确认 AllowedIPs 是 `10.8.0.0/24`。
3. 打开隧道，浏览器访问 `http://服务器组网地址:8080`；SSH 也可以 `ssh dell@服务器组网地址`。

配置文件等于钥匙，只发给本人，不要发群里。远程账号还可以在后台「系统设置 → 备份与远程接入」里和终端绑定。

### 19.4 排错

- 远程打不开：先看两端 `sudo wg`（电脑上看客户端的「最近握手」）。云服务器上没有某一端的握手 → 那一端没连上（安全组 UDP 51820、配置里的 Endpoint）。
- 都有握手但打不开：在公司服务器上 `curl -I http://127.0.0.1:8080` 看网页本身是否正常。
- 运营商偶尔会对 UDP 限速，表现为时快时慢；这时把 wg-easy 的端口改成别的数字（如 443/udp）再重新下发配置。

## 排错所需信息

出现问题时，提供 GPU 型号/驱动版本、失败命令、错误日志尾部即可。不要发送 compose.env、管理员密码或 API key。

业务日志：
```bash
cd /srv/htagent/app
docker compose --env-file /srv/htagent/config/compose.env \
  -f deploy/docker-compose.yml logs --tail=120 server-company server-hq
```

参考：
- 本分支 deploy/README.md、docker-compose.yml 及 SettingsPage.tsx（模型别名已按实际代码纠正）。
- https://huggingface.co/Qwen/Qwen3.8-27B
- https://huggingface.co/Qwen/Qwen3.8-27B-FP8
- https://recipes.vllm.ai/Qwen/Qwen3.8-27B
- https://pypi.org/project/vllm/0.30.0/
- https://docs.vllm.ai/en/latest/getting_started/installation/gpu/
- https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html
