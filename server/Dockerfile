# 后端节点镜像：SDK 阶段发布，运行阶段只带 ASP.NET 运行时。
# 同一镜像既当公司节点也当总部节点——由环境变量（Node__IsHeadquarters 等）区分。
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HT.Agent.Api -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# 备份任务在容器内直接调 pg_dump / tar。pg_dump 大版本必须 ≥ 服务器（16），
# Debian 12 源里只有 15 且会因版本不匹配拒绝，故从 PGDG 官方源装 16。
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
 && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg \
 && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] http://apt.postgresql.org/pub/repos/apt bookworm-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update && apt-get install -y --no-install-recommends postgresql-client-16 \
 && apt-get purge -y gnupg && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# 文件存储与备份都在 data/ 下（Storage:Root=data/files、backup 默认 data/backup），
# compose 里把 /app/data 挂成卷即可持久化。
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HT.Agent.Api.dll"]
