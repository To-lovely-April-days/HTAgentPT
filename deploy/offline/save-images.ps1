# 在「能上网的机器」上跑（PowerShell）：把现场要用的镜像全部做成一个 tar。
# 客户现场多半没有外网，而构建期要拉 .NET SDK、node、nginx 这些基础镜像——
# 拉不到就寸步难行（报 "failed to resolve source metadata ... EOF" 就是这个）。
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$Out = if ($args[0]) { $args[0] } else { "htagent-images.tar" }
$PgImage = if ($env:PG_IMAGE) { $env:PG_IMAGE } else { "pgvector/pgvector:pg16" }

Write-Host "▶ 构建应用镜像（三个：后端、内网界面、客户门户）"
docker compose build

Write-Host "▶ 拉数据库镜像 $PgImage"
docker pull $PgImage

Write-Host "▶ 打包到 $Out（约 1.5~2 GB）"
docker save -o $Out htagent-server htagent-web htagent-portal $PgImage

Write-Host "✅ 好了：$Out"
Write-Host "   连同整个 deploy\ 目录一起拷到现场，在那边跑 offline\load-images.ps1"
