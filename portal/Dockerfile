# 客户门户镜像：独立构建产物 + nginx。
# 门户属隔离区：nginx 只放行门户自己用得到的接口面，其余 /api 一律 404。
FROM node:22-alpine AS build
WORKDIR /app
RUN npm i -g pnpm@10
COPY package.json pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY . .
# build 里包含 check-words.mjs：产物出现内部概念用词会直接构建失败
RUN pnpm build

FROM nginx:1.27-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
ENV API_TARGET=http://server-company:8080
EXPOSE 80
