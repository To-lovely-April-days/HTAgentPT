# 后端节点镜像：SDK 阶段发布，运行阶段只带 ASP.NET 运行时。
# 同一镜像既当公司节点也当总部节点——由环境变量（Node__IsHeadquarters 等）区分。
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HT.Agent.Api -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
# 文件存储与备份都在 data/ 下（Storage:Root=data/files、backup 默认 data/backup），
# compose 里把 /app/data 挂成卷即可持久化。
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HT.Agent.Api.dll"]
