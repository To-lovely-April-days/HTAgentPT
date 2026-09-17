# 在「现场内网机器」上跑（PowerShell）：导入镜像并起服务，全程不联网。
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$Tar = if ($args[0]) { $args[0] } else { "htagent-images.tar" }
if (-not (Test-Path $Tar)) { throw "找不到 $Tar——先把它和 deploy\ 一起拷过来" }

Write-Host "▶ 导入镜像"
docker load -i $Tar

if (-not (Test-Path ".env")) {
  Copy-Item ".env.example" ".env"
  Write-Host "▶ 已从 .env.example 生成 .env——先改里面的口令再继续"
}

# 镜像已经在本地时 compose 不会去构建，也就不会联网核对基础镜像
Write-Host "▶ 启动（用导入的镜像，不构建、不联网）"
docker compose up -d

Write-Host "✅ 起来了。docker compose ps 看状态；端口见 .env"
