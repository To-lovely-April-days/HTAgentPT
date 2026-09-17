#!/usr/bin/env bash
# 在「现场内网机器」上跑：导入镜像并起服务，全程不联网。
set -euo pipefail
cd "$(dirname "$0")/.."

TAR="${1:-htagent-images.tar}"
[ -f "${TAR}" ] || { echo "找不到 ${TAR}——先把它和 deploy/ 一起拷过来"; exit 1; }

echo "▶ 导入镜像"
docker load -i "${TAR}"

[ -f .env ] || { cp .env.example .env; echo "▶ 已从 .env.example 生成 .env——先改里面的口令再继续"; }

# 镜像已经在本地时 compose 不会去构建，也就不会联网核对基础镜像
echo "▶ 启动（用导入的镜像，不构建、不联网）"
docker compose up -d

echo "✅ 起来了。docker compose ps 看状态；端口见 .env"
