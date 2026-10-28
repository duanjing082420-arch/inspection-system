# 多阶段构建：编译在 Go 镜像里做，最终镜像只带一个静态二进制，不带 Go 工具链。
FROM golang:1.22-alpine AS build

WORKDIR /src

# 先只拷依赖声明，利用镜像层缓存（改业务代码不会重新下载依赖）
COPY go.mod ./
COPY . .

# CGO_ENABLED=0：产出不依赖 glibc 的静态二进制，才能跑在 alpine 上
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -o /out/backend .

FROM alpine:3.20

# ca-certificates：容器里如果以后要调外部 HTTPS 接口（比如同步到云端）需要它
RUN apk add --no-cache ca-certificates wget

WORKDIR /app
COPY --from=build /out/backend /app/backend

# 数据必须落到卷里：写在镜像层的话，容器一删巡检记录就没了
VOLUME ["/data"]
ENV INSPECTION_DATA_FILE=/data/issues.json

EXPOSE 8080

# 健康检查直接用业务自己的 health 接口，比只探端口更能说明「服务真的能用」
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO- http://localhost:8080/api/health >/dev/null || exit 1

CMD ["/app/backend"]
