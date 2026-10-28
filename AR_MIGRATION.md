# 从「桌面虚拟巡检场景」迁移到「AR 现场巡检」的设计说明

> 这份文档回答一个问题：**如果把这个项目搬到真实 AR 设备（手机 / 头显）上，哪些代码要改、为什么改、怎么验证。**
>
> 先说清楚边界：**当前交付的是虚拟固定场景版本，没有做真机 AR 演示**（选题说明里也写明不因缺少真机演示扣分）。
> 这份文档记录的是迁移路径和我对 AR 与虚拟 3D 差异的理解，不是已经跑通的 AR 功能。

---

## 1. 先看清现状：这套东西现在是怎么立起来的

| 环节 | 现在的实现 | 文件 |
|---|---|---|
| 相机 | 手动控制：WASD 移动、右键拖拽环绕、滚轮拉近，`FocusOn()` 飞行聚焦 | `InspectionCamera.cs` |
| 取屏幕射线 | `Camera.ScreenPointToRay(Input.mousePosition)` | `InspectionRaycaster.cs:163/200` |
| 求交 | `Physics.Raycast()` 打场景里的真实 Mesh（设备、地面、墙） | `InspectionRaycaster.cs:165/202` |
| 标记位置 | `hit.point`（命中表面点）+ `hit.normal` 抬起，直接作为**世界坐标**存库 | `PlaceMarker()` :245 |
| 设备识别 | 从命中物体往上找 `InspectionTarget`，取 `targetId`（PUMP-A 这类稳定编号） | `InspectionTarget.ResolveInfo()` |
| 标记恢复 | 拉后端列表 → 按 `position{x,y,z}` 在世界坐标原地重建球 | `IssueMarkerLayer.Sync()` |
| 场景本身 | 固定布局：地面 + 墙 + 8 台设备，坐标写死在场景里 | `SceneLayoutBuilder.cs` |

**这套流程成立的前提是「世界坐标系是稳定的」**——场景是我摆的，原点在哪我说了算，
所以把 `hit.point` 这个值存进数据库、下次打开原样放回去，一定还在水泵机组 A 上。

到了 AR 里，这个前提**不成立**。这就是下面所有改造的根因。

---

## 2. 真实 AR 与普通 Unity 3D 场景的七个关键差异

### 差异 1：相机位姿由谁决定（最根本的一条）

- 虚拟场景：相机是我创建的普通 GameObject，我想让它去哪就去哪。
  `InspectionCamera` 里直接改 `transform.position/rotation`，`FocusOn()` 还能做插值飞行。
- 真实 AR：相机的每一帧位姿由设备 Sensor（IMU + 视觉惯性里程计 VIO）算出来，
  由 `ARCameraManager` + `TrackedPoseDriver` 每帧写入 `Main Camera` 的 transform。
  **代码再改 transform 也会被下一帧覆盖**，所以「飞行聚焦」这类操作必须整个删掉或改成 UI 提示。

> 面试一句话版本：*虚拟场景里相机是「我控制的物体」，AR 里相机是「设备在现实中的眼睛」，
> 位置只能读不能写。*

### 差异 2：坐标系与原点每次都不一样

- 虚拟场景：原点 (0,0,0) 是场景文件里写死的，永远在同一个角落。
- 真实 AR：App 启动时设备所在位置就是 session 原点，**每次打开都不一样**，
  甚至同一次会话里 VIO 漂移后还会被系统微调。
- 后果：**世界坐标不能直接存库**。今天在水泵 A 上打的点存成 `(1.23, 0.59, -1.23)`，
  明天打开 App 原点换到三米外，这个坐标就落在墙里了。
- 正确做法：存 **锚点 + 相对偏移**
  （`ARAnchor` 的持久化 id，加上标记相对这个锚点的局部偏移），
  或者用云锚点 / 空间码（二维码、AprilTag）做重定位。

### 差异 3：用什么求交——`Physics.Raycast` 在 AR 里打不到东西

这是最容易踩的坑：AR 场景里**没有真实的 Mesh**，现实中的水泵在物理世界里是不存在的物体，
`Physics.Raycast` 只会打到空气或者我们自己摆的虚拟物体。

- 虚拟场景：`Physics.Raycast(ray, out hit)` → 打中 `Cabinet_Door` 这个 Mesh。
- 真实 AR：用 **ARRaycastManager** 对系统已经检测出来的**平面 / 特征点云**求交：

```csharp
// AR 版取交点（示意，未在本项目实现）
var raycastMgr = FindObjectOfType<ARRaycastManager>();
List<ARRaycastHit> hits = new List<ARRaycastHit>();
if (raycastMgr.Raycast(screenCenter, hits, TrackableType.PlaneWithinPolygon))
{
    Pose hitPose = hits[0].pose;               // 现实表面上的位姿
    TrackableId planeId = hits[0].trackableId; // 属于哪个检测到的平面
    // 用 hitPose.position 作为标记位置，用 planeId 建锚点
}
```

### 差异 4：平面要先「检测」出来，不是预先摆好

- 虚拟场景：地面、墙是我提前建好的，永远存在。
- 真实 AR：`ARPlaneManager` 在运行时逐步识别水平面/竖直面，边界还会随着扫描变化，
  光线差、纯色墙面、玻璃这类弱纹理表面经常检测不出来。
- 工程上的处理：检测不到平面时要给用户明确反馈（"请缓慢移动设备扫描地面"），
  而不是静默失败——这一点我在虚拟版本里已经做过：射线没命中会打日志 + 表单提示。

### 差异 5：光照与环境

- 虚拟场景：场景里有固定灯光，材质颜色所见即所得。
- 真实 AR：现实光照随时变化，`ARCameraManager` 的 light estimation 可以拿主光方向/亮度/色温，
  理想情况下要同步给场景光源（`ARLightEstimationData` → Directional Light），
  否则虚拟标记会明显"浮"在画面上。另外 AR 相机背景是摄像头实时画面
  （`ARCameraBackground`），场景里不能有天空盒和不透明地面挡住它。

### 差异 6：输入方式

- 虚拟场景：鼠标左键点击、`Input.mousePosition`、`Input.GetMouseButtonDown(0)`。
- 真实 AR：触屏 `Input.touches`（要处理多指，避免误触），
  没有右键，`InspectionCamera` 的"右键拖拽环绕/滚轮缩放"整套操作在 AR 里**没有意义**——
  想看别处，用户是**走过去**，不是转镜头。
- 不变的：UI 点击判定照样走 `EventSystem.current.IsPointerOverGameObject()`
  （我现在的代码 :143 就是靠它避免"点 UI 时误在场景里放标记"），这部分可以原样保留。

### 差异 7：性能与工程约束

移动端 SoC 上要控制：三角面数、Draw Call、贴图尺寸、每帧 GC。
现在标记球用的是 `CreatePrimitive(Sphere)` + URP Unlit 材质，已经很轻；
AR 版还要加：点云/平面可视化可开关、后台暂停时停掉相机追踪省电、
以及**权限与降级**（用户拒绝相机权限要给兜底 UI）。

---

## 3. 逐条映射：现在这行代码，AR 版换成什么

| 现在 | AR 版 | 改还是删 |
|---|---|---|
| `InspectionCamera` 手动移动 / 右键环绕 / 滚轮 | 删掉控制逻辑，改由 `TrackedPoseDriver` 驱动相机 | **删** |
| `InspectionCamera.FocusOn()` 飞行聚焦 | 改成「屏幕箭头指引 + 距离文字」，引导用户走过去 | **改** |
| `Input.mousePosition` + `ScreenPointToRay` | 保留取屏幕点的思路，但触屏取 `Touch.position`；部分场景用屏幕中心点 | **改** |
| `Physics.Raycast` 打 Mesh | `ARRaycastManager.Raycast` 打平面/点云 | **换** |
| `hit.point` 直接存世界坐标 | 存「锚点 id + 相对锚点的局部偏移」 | **换**（数据契约要改） |
| `InspectionTarget.ResolveInfo()` 靠 Mesh 上的组件认设备 | AR 里没有现成 Mesh：要靠**空间锚点/二维码/点云 registration** 把现实设备和虚拟设备模型对齐后才能认 | **换**（这是 AR 落地的最大难点） |
| `IssueMarkerLayer.Sync()` 按世界坐标重建球 | 按「锚点 + 偏移」重建；锚点没定位到就先不显示并提示 | **改** |
| `EventSystem.IsPointerOverGameObject()` 挡 UI | 原样保留 | **不动** |
| 后端字段 `position{x,y,z}` | 增加 `anchorId` + `offset{x,y,z}`（或保留 position 但注明它是相对锚点） | **后端扩展** |
| React / Go / 状态流转 / 离线重发 | 与渲染无关，**一行都不用改** | **不动** |

**这里有个我如实说明的点**：因为坐标语义要变，`position` 这个字段在 AR 版里必须重新定义。
现在虚拟版本存的是世界坐标，这在虚拟场景里是正确且简单的选择；
换成 AR 就要做数据迁移（把老记录挂到一个基准锚点上）。这是迁移的主要工作量之一。

---

## 4. 不用真机怎么验证：AR Foundation XR Simulation

AR Foundation 提供 **XR Simulation**（编辑器内模拟）：不需要连手机，
在 Editor 里加载一段预先录制的设备轨迹（.xrenv 环境素材），模拟摄像头画面和位姿，
就能在 Game 视图里跑通「平面检测 → 屏幕点选 → ARRaycast 放标记 → 锚点持久化」全流程。

真要落地这一步，我会在编辑器里验证的顺序是：

1. 装 `AR Foundation` + `ARKit XR Plugin` / `ARCore XR Plugin`（按目标平台选），
   并开启 `XR Simulation` 环境；
2. 场景里删掉固定地面/墙与灯光，换成 `AR Session` + `AR Session Origin`
   （含 `ARCameraManager` / `ARRaycastManager` / `ARPlaneManager`）；
3. 用模拟轨迹跑一遍：确认能检测到平面 → 屏幕点击能拿到 `ARRaycastHit` → 标记落在平面上；
4. 重点验证**二次启动**：退出再进入模拟，检查锚点恢复后标记是否还在原处——
   这一条才是 AR 版和虚拟版最大的区别，也是最容易翻车的地方。

---

## 5. AR 基础问答要点（给自己备忘）

| 问题 | 要点 |
|---|---|
| AR 和 VR 的区别？ | VR 是全虚拟、遮蔽现实；AR 在现实画面上叠加虚拟内容，关键是**对现实的理解**（追踪 + 场景理解） |
| 设备怎么知道自己在哪？（追踪原理） | VIO：相机特征点 + IMU 惯性数据融合，解算 6DoF 位姿（位置 + 朝向）；弱纹理/快速运动/纯白墙会跟丢 |
| 什么是 6DoF？ | 3 个平移自由度 + 3 个旋转自由度。只有 3DoF（如仅转头）就无法支持走近观察 |
| 平面检测和特征点是什么关系？ | 先提取稀疏特征点云，再从中拟合出平面（水平/竖直），平面是比点云更高层的语义 |
| 为什么要锚点（Anchor）？ | 抵消 VIO 漂移、保证虚拟物体相对现实固定不动；关机重开要能恢复就需要**持久化锚点/云锚点** |
| 光照估计有什么用？ | 让虚拟物体的明暗、色温与现实一致，否则叠加物会显得"假"、像贴纸 |
| ARFoundation 是什么？ | Unity 的跨平台上层抽象，统一封装 ARKit / ARCore，业务代码写一套，底层插件切换 |
| AR 里为什么不能直接用 Physics.Raycast？ | 物理世界里没有现实物体的碰撞体，只能用 ARRaycast 与检测到的平面/点云求交 |
|  occlusion（遮挡）怎么做？ | 需要深度图/人体分割/场景网格（ARMeshManager），让虚拟物体能被现实物体挡住，才更真实 |
| 移动端性能注意什么？ | 控制面数与 Draw Call、避免每帧 new（GC）、合理降低追踪频率、注意发热降频 |

---

## 6. 已知未做 / 如实说明

1. **本项目没有真机 AR 演示**，也没有安装 AR Foundation 包——本文档是迁移方案，不是已实现功能。
2. 设备识别在 AR 下没有现成方案：虚拟版靠 Mesh 上挂 `InspectionTarget`，
   真实场景必须靠"空间锚点 + 设备模型对齐"或"二维码/标签识别"来替代，这是落地前必须解决的问题。
3. 坐标语义（`position` 是世界坐标 vs 相对锚点偏移）在迁移时需要数据迁移，工作量已在上文说明。
4. 弱纹理表面的平面检测失败率、户外强光下的可见性，需要真机实测才有结论，我这里不做估计。
