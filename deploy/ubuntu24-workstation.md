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
