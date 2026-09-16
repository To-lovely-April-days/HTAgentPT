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
# pg_dump 的依赖库也全部从数据库镜像复制（krb5/ldap 家族基础镜像里没有），
# 运行阶段完全不碰 apt——构建期除基础镜像外零联网，网络环境再差也能过。
# lz4/zstd/zlib 基础镜像自带（apt 自身的依赖），ssl 由 .NET 运行时带入。
COPY --from=pgtools /usr/lib/postgresql/16/bin/pg_dump /usr/local/bin/pg_dump
COPY --from=pgtools \
    /usr/lib/x86_64-linux-gnu/libpq.so.5* \
    /usr/lib/x86_64-linux-gnu/libgssapi_krb5.so.2* \
    /usr/lib/x86_64-linux-gnu/libkrb5.so.3* \
    /usr/lib/x86_64-linux-gnu/libk5crypto.so.3* \
    /usr/lib/x86_64-linux-gnu/libkrb5support.so.0* \
    /usr/lib/x86_64-linux-gnu/libcom_err.so.2* \
    /usr/lib/x86_64-linux-gnu/libkeyutils.so.1* \
    /usr/lib/x86_64-linux-gnu/libldap-2.5.so.0* \
    /usr/lib/x86_64-linux-gnu/liblber-2.5.so.0* \
    /usr/lib/x86_64-linux-gnu/libsasl2.so.2* \
    /usr/lib/x86_64-linux-gnu/
WORKDIR /app
COPY --from=build /app/publish .
# 文件存储与备份都在 data/ 下（Storage:Root=data/files、backup 默认 data/backup），
# compose 里把 /app/data 挂成卷即可持久化。
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HT.Agent.Api.dll"]
