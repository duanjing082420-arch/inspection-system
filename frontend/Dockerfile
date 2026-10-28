# 多阶段构建：Node 镜像只负责把 React 打成静态文件，运行时用 nginx 托管。
FROM node:22-alpine AS build

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27-alpine

COPY --from=build /app/dist /usr/share/nginx/html

# 前端在浏览器里发的是同源 /api/* 请求，生产环境由 nginx 反代到后端容器，
# 这样线上就不需要 CORS，前端代码也不用区分开发和生产的接口地址。
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
