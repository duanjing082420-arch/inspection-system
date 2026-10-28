# 桌面虚拟巡检标注系统

Unity（团结引擎）三维场景 + React 管理台 + Go 后端的最小闭环：
在三维场景里点设备拍照式上报问题 → 后端落库 → 管理台和三维场景双向联动查看/处理。

## 三端在哪

| 端 | 位置 | 说明 |
|---|---|---|
| Unity | `unity/` | 团结引擎 2022.3.62t17，场景 `unity/Assets/Scenes/SampleScene.scene` |
| Go 后端 | `backend/` | net/http + JSON 文件存储，默认 `:8080`；带 CORS、请求体上限、panic 恢复、优雅退出 |
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

## 实机验证记录

后端有自动化测试、镜像有 CI，**但 Unity 端只能靠真跑一遍**。以下是 2026-10-28 19:59
编译通过后在编辑器里 Play、从 `Editor.log` 取证的结果：

| 功能 | 日志证据 |
|---|---|
| 脚本编译 | `Assembly-CSharp.dll` 19:59 重新生成，全文无 `error CS` |
| 射线检测命中设备 | `查看模式：命中 Pump_Housing → 设备「水泵机组 A」（编号 PUMP-A）` |
| 点设备 → 列该设备记录 | `设备「水泵机组 A」（PUMP-A）共 N 条记录` |
| 射线命中位置用于放标记 | `表单已打开，命中坐标 = (1.01, 0.69, -1.32)` |
| 上报模式与取消 | `已进入上报模式` → `已销毁待提交标记：PendingIssueMarker` |
| 删除记录 | 多条 `DELETE /api/issues/{id}` 成功并重新加载列表 |

**没验证到的（如实列出）**：演示数据已被逐条删除，所以下面这几项在空数据下跑不出来——
三维标记的生成、**按优先级改大小**、**点击标记看详情**，以及 `zone` 字段真正写进回包。
这四项都依赖「至少有一条带 `position` 的记录」，补一条数据即可验证。

## Docker

```bash
docker compose up --build
# 后端 http://localhost:8080 ，前端 http://localhost:5173
```

- `backend/Dockerfile`：Go 多阶段构建，最终只有静态二进制 + alpine，带 HEALTHCHECK
- `frontend/Dockerfile`：Node 构建 → nginx 托管，`nginx.conf` 把 `/api` 反代到 backend 容器
- 巡检记录落在 `inspection-data` 卷里，容器重建不丢

> **本机没有安装 Docker**，所以镜像不是在本机构建的，而是交给 CI 验证：
> `.github/workflows/ci.yml` 每次 push 都在 GitHub Runner 上跑 `docker build`，
> 同时跑 `go vet`/`go test` 和 `tsc -b && vite build`。
> **四个 job 已全部通过**（见仓库 Actions 页），也就是说仓库里这两份 Dockerfile 是确认能构建出镜像的。
> 没验证的是**运行时**：`docker compose up` 之后前后端能否正常联动，那需要有 Docker 的环境实测。
>
> 构建前离线核对过这几处（`docker build` 最容易栽的地方）：
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
2. **Docker 只验证了「能构建」，没验证「能跑起来」**。本机无 Docker 客户端，
   `docker build` 由 CI 在 GitHub Runner 上跑通（四个 job 全绿），
   但 `docker compose up` 之后前后端联动、数据卷挂载是否真的正常，没有实测过。
3. 后端 `saveLocked` 是整文件覆盖写，进程被强杀或并发写时理论上可能把文件写坏；
   改成「临时文件 + 原子 rename」是下一步该做的加固。
4. **演示数据现在是空的**。`backend/issues.json` 内容为 `[]`——
   在 Unity 里逐条测试删除功能时把 17 条记录全删了（这个是主动测试，不是 bug，
   日志里每条 `DELETE` 都返回成功）。
   需要恢复的话，首次提交里还留着完整的 17 条：
   `git show 5700d2d:backend/issues.json > backend/issues.json`，然后重启后端。
   （顺带一提，原先那 3 条 `targetId` 为空的历史记录也一并删掉了，
   所以「未知设备」分组现在也是空的。）
5. 三维标记按后端数据全量生成，坐标全 0 的记录（早期没有 position 字段）不会生成标记。
6. React 端新增表单不传 `targetId`（网页端登记时不知道设备编号），所以网页新增的记录
   在三维场景里没有标记。

## 文档

- `AI_LOG.md`：所有主动指令的原文、采纳结果与验证过程
- `AR_MIGRATION.md`：AR 版迁移方案 + 真实 AR 与虚拟场景的差异对照
