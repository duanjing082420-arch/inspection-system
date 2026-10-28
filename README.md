# 桌面虚拟巡检标注系统

Unity（团结引擎）三维场景 + React 管理台 + Go 后端的最小闭环：
在三维场景里点设备拍照式上报问题 → 后端落库 → 管理台和三维场景双向联动查看/处理。

## 三端在哪

| 端 | 位置 | 说明 |
|---|---|---|
| Unity | `unity/` | 团结引擎 2022.3.62t17，场景 `unity/Assets/Scenes/SampleScene.scene` |
| Go 后端 | `backend/` | Go 1.22 + net/http（标准库，零外部依赖），JSON 文件存储，默认 `:8080` |
| React 前端 | `frontend/` | React 19 + TypeScript + Vite 8，dev `:5173`，通过代理访问 `/api` |

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

## 已完成功能

**Unity（三维现场端）**
- `Physics.Raycast` 射线检测，标记放在 `hit.point`（命中点），不是物体中心
- 点在 UI 上不放标记（射线前先判 `EventSystem.IsPointerOverGameObject`）
- 上报模式：一次最多一个待提交标记，取消或提交后清理
- 表单校验（标题必填、优先级三选一）+ 实时显示命中坐标
- **列表 ↔ 三维双向联动**：点记录 → 相机飞过去并高亮该标记；点设备 → 按 `targetId` 列出该设备全部记录
- 筛选与搜索：关键词、设备、优先级、状态、区域、重置（只改显示集合，不删原始数据）
- 标记按**状态着色**（open 红 / in_progress 橙 / resolved 绿）、按**优先级改大小**
- 标记记住自己对应的记录 `id`，点标记可看详情（屏幕空间就近拾取，不加 Collider 以免挡住设备点击）
- 进场景时按后端坐标全量恢复标记
- 删除记录（二次确认）、网络失败进本地待发队列、下次启动自动重发

**React（管理台）**
- 列表 / 详情 / 新增 / 改状态 / 删除，状态下拉直接改
- 筛选：关键词、优先级、区域、状态
- 四个工作区：问题总览（统计卡）、巡检问题、处理记录（只列已处理）、系统设置（含服务健康检测）
- 后端不可用时不白屏：红色错误横幅 + 刷新重试

**Go（后端）**
- 5 个接口：列表、新增、改状态、删除、健康检查（另保留一条早期兼容通道）
- 参数校验：标题必填、枚举合法性、`DisallowUnknownFields`（多字段即 400）、坐标有限性
- JSON 文件持久化，重启后数据仍在
- CORS、请求体上限 1MB、**panic 恢复**、**优雅退出**
- 12 个自动化测试（`go test ./...`），CI 里跑 `go vet` + 两个 `docker build`

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

## 后端地址

默认 `http://localhost:8080`，三端各自的改法：

| 端 | 地址写在哪 |
|---|---|
| Go 后端 | `backend/main.go` 里 `addr := ":8080"`，改端口要改代码重新编译 |
| React | 不写死地址。代码里只写 `/api/...`，由 `frontend/vite.config.ts` 的 proxy 转发到 8080 |
| Unity | 场景里 `InspectionApiClient` 组件的 `apiUrl` 字段，填 `/api/issues` |

数据文件路径可用环境变量 `INSPECTION_DATA_FILE` 覆盖（容器里用它指向挂载的数据卷），
不设就是 `backend/issues.json`。

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
4. **演示数据被删到只剩几条**。为了验证删除功能，17 条记录被逐条删掉了大半
   （这是主动测试，不是 bug，`Editor.log` 里每条 `DELETE` 都返回成功）。
   仓库里始终留着完整的 17 条可随时恢复：
   ```bash
   git show 5700d2d:backend/issues.json > backend/issues.json
   ```
   改完**必须重启后端**——服务启动时才把文件读进内存，直接改文件不会生效，
   而且后端下一次保存会把内存里的旧数据写回去覆盖掉你刚恢复的内容。
   恢复后记得重启：先 `Ctrl+C` 停掉，再 `.\backend.exe`。
5. 三维标记按后端数据全量生成，坐标全 0 的记录（早期没有 position 字段）不会生成标记。
6. React 端新增表单不传 `targetId`（网页端登记时不知道设备编号），所以网页新增的记录
   在三维场景里没有标记。

## 题面第 5 节：三个 AR 基础问题

本项目跑的是**固定虚拟场景**，下面是如果要迁移到真实 AR 时，我对这三个问题的理解。
（更完整的改造点逐条对照在 `AR_MIGRATION.md`。）

**Q1：改成真实 AR 后，平面检测和点击放置通常需要哪些 AR Foundation 能力？**

需要 `AR Session` + `XR Origin` 打底；`ARPlaneManager` 做平面检测（本项目是机房地面，
配 `Detection Mode = Horizontal`，再挂 `AR Default Plane` 把识别到的平面画出来让用户知道哪里能点）；
`ARRaycastManager` 把屏幕点击转成射线，用 `TrackableType.PlaneWithinPolygon`
去命中**已识别的平面**而不是任意网格；拿到 `ARRaycastHit.pose` 后在那里实例化标记，
再用 `ARAnchorManager` 挂一个 `ARAnchor`，减少手机移动时的漂移。
另外还需要 `ARCameraManager`（把相机画面当背景）、相机权限申请，以及运行时检查 `ARSession.state`。

对照本项目的代码：`InspectionRaycaster.cs` 现在用的是 `Physics.Raycast`，
打的是场景里真实存在的 Mesh，命中就取 `hit.point`。这一条在 AR 里要整个换掉——
真实环境里你点到的墙并没有一个 BoxCollider，能命中的只有平面检测结果。

**Q2：为什么只保存 Unity 中的 x/y/z 坐标，通常不能保证下次启动对应同一个真实位置？**

因为 AR 的世界坐标系原点是**会话开始那一刻设备的位姿**，每次启动都不一样——
同一个墙角，两次运行会给出完全不同的三元组。加上视觉里程计和 IMU 会持续漂移，
系统在修正 tracking 的时候还会悄悄微调整个坐标系，不同设备、不同尺度之间也不通用。
只存三个数字，等于存了"某个随机坐标系里的一个点"，没有任何与之绑定的地图信息。

要跨启动还原，得靠 ARCore Cloud Anchor / ARKit ARWorldMap 这类持久化锚点，
或者在现场放二维码、图像标记做一次标定。

**这一点恰恰是本项目和真实 AR 最本质的差别**：虚拟场景的世界原点写在场景文件里、恒定不变，
所以我现在存 x/y/z 就能精确还原标记位置——`IssueMarkerLayer.Sync()` 每次拉完列表，
直接按坐标把球摆回去，位置分毫不差。也正因为如此，"从后端恢复历史标记"这个加分项在虚拟场景里
是确定可行的，换到真机上就必须引入锚点才能谈还原。

**Q3：AR 初始化失败、设备不支持或 Tracking 异常时，应该如何提示用户？**

启动前先查 `ARSession.state` 和 ARCore 可用性：设备不在支持列表、或没装
Google Play Services for AR，就明确提示"当前设备不支持 AR"并禁用放置功能，
**绝不能让相机黑屏让用户自己猜**；相机权限被拒要提示并引导去系统设置开启；
Tracking 掉到 `Limited` 时提示"请缓慢移动手机、保证光线充足、对准有纹理的表面"；
初始化超时或 Error 时给一个"重试 / 重置会话"的入口。
原则是所有异常都要有**可读的中文提示 + 恢复入口**，不崩溃、不静默失效。

这个思路我在网络失败上已经做了一遍：提交失败时红色 toast + 行内错误，
且**保留已填内容和待提交标记**方便重试；拉列表失败保留上一次结果，不清空面板。
AR 状态提示要照同样的标准做，只是对象从"网络"换成"会话与 tracking"。

## 演示视频

两段，共 8~10 分钟（录制脚本见 `VIDEO_SCRIPT.md`）：

| 视频 | 内容 |
|---|---|
| A · Unity 端 | 场景操作 → 射线命中放标记 → 填表提交 → 列表与三维双向联动 → 筛选 → 删除 |
| B · 网页与后端 | 启动后端 → React 列表/筛选/改状态/删除 → 停掉后端看错误提示 → 重启恢复 |

录制完成后放在 `docs/videos/` 下。如果单个文件超过 100 MB（GitHub 单文件上限），
上传到仓库的 Release 附件里，并在本节给出链接——不要硬塞进 git 历史。

## 文档

- `AI_LOG.md`：所有主动指令的原文、采纳结果与验证过程
- `AR_MIGRATION.md`：AR 版迁移方案 + 真实 AR 与虚拟场景的差异对照
- `VIDEO_SCRIPT.md`：演示视频的分镜与口播要点
