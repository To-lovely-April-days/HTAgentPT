#!/usr/bin/env bash
# 在「能上网的机器」上跑：把现场要用的镜像全部做成一个 tar，拷到内网即可。
#
# 客户现场多半没有外网，而 docker 构建期要拉 .NET SDK、node、nginx 这些基础镜像——
# 拉不到就寸步难行（报 "failed to resolve source metadata ... EOF" 就是这个）。
# 这里把应用镜像直接构建好一起打包，现场不再构建、不再联网。
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="${1:-htagent-images.tar}"
PG_IMAGE="${PG_IMAGE:-pgvector/pgvector:pg16}"

echo "▶ 构建应用镜像（三个：后端、内网界面、客户门户）"
docker compose build

echo "▶ 拉数据库镜像 ${PG_IMAGE}"
docker pull "${PG_IMAGE}"

echo "▶ 打包到 ${OUT}（约 1.5~2 GB，视基础镜像而定）"
docker save -o "${OUT}" \
  htagent-server htagent-web htagent-portal "${PG_IMAGE}"

echo "✅ 好了：${OUT}"
echo "   连同整个 deploy/ 目录一起拷到现场，在那边跑 offline/load-images.sh"
