# 后端节点镜像：SDK 阶段发布，运行阶段只带 ASP.NET 运行时。
# 同一镜像既当公司节点也当总部节点——由环境变量（Node__IsHeadquarters 等）区分。
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HT.Agent.Api -c Release -o /app/publish

# 备份任务要在容器内调 pg_dump，且大版本必须 ≥ 服务器（16）——Debian 12 源里只有 15。
# 不走 PGDG 软件源（国内各镜像站路径不一、直连又常断）：pg_dump 与配套 libpq
# 直接从数据库同款镜像里复制（部署本来就要拉它，本地必有缓存），版本天然一致。
FROM pgvector/pgvector:pg16 AS pgtools

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# libpq/pg_dump 的运行库从 Debian 源装。源默认走清华镜像（国内直连官方源经常断）；
# 海外构建可 --build-arg APT_MIRROR=deb.debian.org 换回官方。
ARG APT_MIRROR=mirrors.tuna.tsinghua.edu.cn
RUN sed -i "s|deb.debian.org|${APT_MIRROR}|g" /etc/apt/sources.list.d/debian.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 libldap-2.5-0 liblz4-1 libzstd1 zlib1g \
 && rm -rf /var/lib/apt/lists/*
COPY --from=pgtools /usr/lib/postgresql/16/bin/pg_dump /usr/local/bin/pg_dump
COPY --from=pgtools /usr/lib/x86_64-linux-gnu/libpq.so.5* /usr/lib/x86_64-linux-gnu/
WORKDIR /app
COPY --from=build /app/publish .
# 文件存储与备份都在 data/ 下（Storage:Root=data/files、backup 默认 data/backup），
# compose 里把 /app/data 挂成卷即可持久化。
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HT.Agent.Api.dll"]
