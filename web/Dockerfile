# 内网前端镜像：构建静态产物 + nginx 承担同源反代。
# 同一镜像既当公司内网界面也当总部审核台——由环境变量 API_TARGET 指向不同后端节点。
FROM node:22-alpine AS build
WORKDIR /app
# npm 源默认走国内镜像（npmmirror）；海外构建可 --build-arg NPM_REGISTRY=https://registry.npmjs.org
ARG NPM_REGISTRY=https://registry.npmmirror.com
RUN npm config -g set registry ${NPM_REGISTRY} && npm i -g pnpm@10
COPY package.json pnpm-lock.yaml ./
RUN pnpm config set registry ${NPM_REGISTRY} && pnpm install --frozen-lockfile
COPY . .
RUN pnpm build

FROM nginx:1.27-alpine
COPY --from=build /app/dist /usr/share/nginx/html
# nginx 官方镜像启动时会对 templates/*.template 做 envsubst（只替换已定义的环境变量），
# 所以 ${API_TARGET} 会被替换，而 $host、$uri 这类 nginx 变量原样保留。
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
ENV API_TARGET=http://server-company:8080
EXPOSE 80
