module inspection-system/backend

// 声明 1.22 而不是本地的 1.27：Docker 构建用的是 golang:1.22-alpine，
// go.mod 里写更高的版本会让镜像里的编译器直接拒绝构建。本地 1.27 向下兼容，不受影响。
go 1.22
