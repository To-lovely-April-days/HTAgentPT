ARG NODE_IMAGE=node:22-alpine
ARG NGINX_IMAGE=nginx:1.27-alpine
# 客户门户镜像：独立构建产物 + nginx。
# 门户属隔离区：nginx 只放行门户自己用得到的接口面，其余 /api 一律 404。
FROM ${NODE_IMAGE} AS build
WORKDIR /app
# npm 源默认走国内镜像（npmmirror）；海外构建可 --build-arg NPM_REGISTRY=https://registry.npmjs.org
ARG NPM_REGISTRY=https://registry.npmmirror.com
RUN npm config -g set registry ${NPM_REGISTRY} && npm i -g pnpm@10
COPY package.json pnpm-lock.yaml ./
RUN pnpm config set registry ${NPM_REGISTRY} && pnpm install --frozen-lockfile
COPY . .
# build 里包含 check-words.mjs：产物出现内部概念用词会直接构建失败
RUN pnpm build

FROM ${NGINX_IMAGE}
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
ENV API_TARGET=http://server-company:8080
EXPOSE 80
