# 桌面虚拟巡检标注系统

Unity（团结引擎）三维场景 + React 管理台 + Go 后端的最小闭环：
在三维场景里点设备拍照式上报问题 → 后端落库 → 管理台和三维场景双向联动查看/处理。

## 三端在哪

| 端 | 位置 | 说明 |
|---|---|---|
| Unity | `unity/` | 团结引擎 2022.3.62t17，场景 `unity/Assets/Scenes/SampleScene.scene` |
| Go 后端 | `backend/` | net/http + JSON 文件存储，默认 `:8080` |
| React 前端 | `frontend/` | Vite + React 19 + TS，dev `:5173`，通过代理访问 `/api` |

`unity/` 是从引擎工作目录 `D:/Tuanjie Cowork/HubFiles/InspectMark/` 复制过来的源码部分：
只含 `Assets/`、`Packages/`、`ProjectSettings/`，**不含 `Library/`、`Temp/`、`Logs/`（452MB 缓存）**。
用团结引擎 Hub 直接 Open `unity/` 目录即可，首次打开会自动重建缓存。

## 本地怎么跑

```bash
# 后端（二选一）
cd backend && go run .                 # 直接跑
cd backend && go build -o backend.exe . && ./backend.exe

# 前端
cd frontend && npm install && npm run dev     # http://localhost:5173

# Unity
团结引擎 Open → 选本仓库的 unity/ 目录 → 打开 Assets/Scenes/SampleScene.scene → Play。
场景内容由编辑器脚本生成，如需重建：Tools ▸ 巡检场景 ▸ 第1~13步 按顺序跑一遍。
```

后端启动后自检：`curl http://localhost:8080/api/health`

## 自动化测试

```bash
cd backend && go test ./...
```

11 个用例覆盖：health、提交（正常/空标题/非法优先级/未知字段）、区域字段与默认值、
重启后数据是否真的落盘、状态流转与持久化、中文枚举兜底、404、删除与重复删除、
历史数据归一化。测试跑在 `t.TempDir()` 里并临时切换工作目录，**不会碰真实数据文件**。

## Docker

```bash
docker compose up --build
# 后端 http://localhost:8080 ，前端 http://localhost:5173
```

- `backend/Dockerfile`：Go 多阶段构建，最终只有静态二进制 + alpine，带 HEALTHCHECK
- `frontend/Dockerfile`：Node 构建 → nginx 托管，`nginx.conf` 把 `/api` 反代到 backend 容器
- 巡检记录落在 `inspection-data` 卷里，容器重建不丢

> ⚠️ **本机没有安装 Docker，两个镜像都没在本机构建过。**
> 改为交给 CI 在云端验证：`.github/workflows/ci.yml` 每次 push 都会跑
> `docker build`，同时跑 `go vet`/`go test` 和 `tsc -b && vite build`。
>
> 离线能查的都查过了，这几处是最容易让 `docker build` 直接失败的：
> - `go.mod` 声明 `go 1.22`，与 `golang:1.22-alpine` 对齐（写更高版本镜像编译器会拒绝构建）
> - `package-lock.json` 存在，`npm ci` 可用
> - `nginx.conf` 上游写的是 compose 服务名 `backend:8080`，不是 localhost（容器内 localhost 指自己，会 502）
> - 两个构建目录都补了 `.dockerignore`，其中 `frontend/node_modules` 是关键：
>   Dockerfile 先 `npm ci` 再 `COPY . .`，不排除的话宿主机的 Windows 原生模块会覆盖
>   容器里刚装好的 Linux 版本，构建必炸 —— 这类问题本地看不出来，只有真 build 才暴露

## 源码清单

```
unity/Assets/Scripts/            运行时脚本（8 个）
  InspectionRaycaster.cs        射线检测：命中设备 / 拾取标记，区分「查看」与「上报」两种模式
  InspectionTarget.cs           设备标识组件：targetId、显示名、区域 zone
  InspectionApiClient.cs        HTTP 客户端：GET/POST/PATCH/DELETE + 离线发件箱重试
  InspectionRecordList.cs       记录列表与详情面板、筛选、删除、相机联动
  IssueMarkerLayer.cs           按后端数据全量生成标记球，状态着色 + 优先级尺寸
  IssueFormController.cs        上报表单：校验、提交、失败重试与提示
  InspectionCamera.cs           相机聚焦与轨道控制
  InspectionCameraController.cs 相机输入

unity/Assets/Editor/            编辑器脚本（5 个，只在编辑器里跑）
  SceneLayoutBuilder.cs         Tools ▸ 巡检场景 ▸ 第1~13步：程序化生成场景与 UI
  RecordPanelBuilder.cs         巡检记录面板 + 筛选区
  IssueFormPanelBuilder.cs      上报表单面板 + 动态中文字体生成
  InspectionWiring.cs           一键把脚本挂到场景对象上
  InspectionOutboxTools.cs      离线发件箱查看/清空

backend/  main.go（路由+存储+校验）  main_test.go（11 个用例）  Dockerfile
frontend/ src/App.tsx（列表/筛选/详情/新增/改状态/删除）  App.css  Dockerfile  nginx.conf
```

## 接口约定

```
GET    /api/issues                 列表 {items,total}
POST   /api/issues                 新增（DisallowUnknownFields，多字段即 400）
PATCH  /api/issues/{id}/status     改状态，请求体只能有 {"status": "open|in_progress|resolved"}
DELETE /api/issues/{id}            删除
GET    /api/health                 健康检查
POST   /api/inspections            早期扁平报文通道（保留兼容）
```

记录字段三端一致：`id / targetId / targetName / zone / title / description / priority /
position{x,y,z} / status / createdAt / updatedAt`。
`priority` 与 `status` 一律存英文枚举，界面显示中文。

## 已知问题与未完成内容（如实列出）

1. **没有真机 AR 演示**。迁移到 AR Foundation 的方案写在 `AR_MIGRATION.md`，是设计文档不是已实现功能。
2. **Docker 镜像未经实机构建**（本机无 Docker 客户端，Dockerfile / compose 只做了静态核对）。
   `.github/workflows/ci.yml` 已写好用于在 GitHub Runner 上验证 `docker build`，
   但项目尚未建 Git 仓库，**CI 一次都还没真正跑过**。
3. 后端 `saveLocked` 是整文件覆盖写，进程被强杀或并发写时理论上可能把文件写坏；
   改成「临时文件 + 原子 rename」是下一步该做的加固。
4. 历史数据里有 3 条记录 `targetId` 为空（早期版本留下的），列表里归到「未知设备」，
   修数据需要直接改 `backend/issues.json`，我没擅自改历史数据。
5. 三维标记按后端数据全量生成，坐标全 0 的记录（早期没有 position 字段）不会生成标记。
6. React 端新增表单不传 `targetId`（网页端登记时不知道设备编号），所以网页新增的记录
   在三维场景里没有标记。

## 文档

- `AI_LOG.md`：所有主动指令的原文、采纳结果与验证过程
- `AR_MIGRATION.md`：AR 版迁移方案 + 真实 AR 与虚拟场景的差异对照
