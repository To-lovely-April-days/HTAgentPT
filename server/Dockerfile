# 后端节点镜像：SDK 阶段发布，运行阶段只带 ASP.NET 运行时。
# 同一镜像既当公司节点也当总部节点——由环境变量（Node__IsHeadquarters 等）区分。
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HT.Agent.Api -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# 备份任务在容器内直接调 pg_dump / tar。pg_dump 大版本必须 ≥ 服务器（16），
# Debian 12 源里只有 15 且会因版本不匹配拒绝，故从 PGDG 源装 16。
# 源默认走清华镜像（国内直连官方源经常断）；海外构建可用 --build-arg 换回官方：
#   APT_MIRROR=deb.debian.org  PGDG_MIRROR=apt.postgresql.org/pub/repos/apt
# PGDG 签名密钥用 Debian 自带的 postgresql-common 包里那份，不再联网取密钥。
ARG APT_MIRROR=mirrors.tuna.tsinghua.edu.cn
ARG PGDG_MIRROR=mirrors.tuna.tsinghua.edu.cn/postgresql/repos/apt
RUN sed -i "s|deb.debian.org|${APT_MIRROR}|g" /etc/apt/sources.list.d/debian.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates postgresql-common \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://${PGDG_MIRROR} bookworm-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update && apt-get install -y --no-install-recommends postgresql-client-16 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# 文件存储与备份都在 data/ 下（Storage:Root=data/files、backup 默认 data/backup），
# compose 里把 /app/data 挂成卷即可持久化。
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HT.Agent.Api.dll"]
