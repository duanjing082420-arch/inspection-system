# AI_LOG.md

本项目（现场巡检问题标注系统）开发过程中，本人向 AI 发出的全部主要问题与指令记录。

- 三端：`backend/`（Go）、`frontend/`（React + TypeScript）、Unity 客户端（团结引擎工程位于本机 `D:/Tuanjie Cowork/HubFiles/InspectMark`）。
- 记录顺序 = 实际提问顺序。
- **格式说明**：P-001 ～ P-008 的 Prompt 原文在本会话上下文压缩时丢失，已按上下文还原要点并明确标注，**未编造原文**；P-009 之后的均为逐字原文。
- 全文不含 API Key、Token、账号密码等敏感信息；本机回环地址 `127.0.0.1` 为本地开发地址，保留无风险。

---

## P-001

- 目标：解决提交按钮无响应、重复创建标记、提交失败后记录丢失三类 bug，并整合三个脚本文件。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 整合 `InspectionTarget` / `IssueFormController` / `InspectionApiClient` 三个文件，给出可以直接替换的完整版本，解决三个问题：提交按钮点了没反应、重复创建标记、提交失败后记录丢失。

### 结果

- **采用**
- 三个文件按职责重写：射线点击归 `InspectionRaycaster`、表单归 `IssueFormController`、通信归 `InspectionApiClient`、设备身份归 `InspectionTarget`。
- 验证：编辑器日志 `error CS` 计数为 0；Play 后单击设备只生成 1 个标记，重复点击会先销毁旧标记。

---

## P-002

- 目标：按固定顺序做端到端验收，避免"改完就说好了"。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 按"编译 → 场景 → 表单 → 后端 → 数据"五步验收，确保端到端跑通。

### 结果

- **采用**，并沿用为后续所有改动的标准验收流程。
- 验证：每一步都留下可核对的证据（编译产物时间戳、场景文件字段、日志、HTTP 状态码、后端 JSON）。

---

## P-003

- 目标：统一前后端优先级字段，避免中文枚举发给后端被拒。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 界面显示中文"低/中/高"，发给后端的必须是英文 `low/medium/high`，不要直接把中文发出去。

### 结果

- **采用**
- `InspectionApiClient.NormalizePriority()` 做唯一一次翻译，`PriorityLabels` / `PriorityValues` 两个数组分别管显示值和上报值。
- 验证：提交日志里 `优先级：下拉「高」-> high`。

---

## P-004

- 目标：排查"后端返回成功，但场景里的标记没变绿甚至消失"。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 提交成功了，但标记没有变绿，有时候还直接不见了，查一下原因。

### 结果

- **采用**，查到两个真因并修复：
  1. URP 材质颜色属性是 `_BaseColor` 而不是 `_Color`，只写 `_Color` 会静默失效 → `ApplyColor()` 两个都写。
  2. `IssueFormController.OpenWithMarker()` 开头有一句"如果已有待提交标记就先取消"，而 `PlaceMarker()` 是先建标记再调 `OpenWithMarker()`，等于刚建的标记立刻被自己删掉 → 删除该清理逻辑。
- 验证：日志里不再出现"已提交成功"紧跟"当前没有待提交标记"。
- **这是本项目中第一次对 AI 既有结果做纠错**：AI 早先为"防止重复标记"加的清理逻辑，在新的调用顺序下反而成了 bug。教训是清理逻辑必须放到"用户主动取消"的分支，不能放在"打开表单"的入口。

---

## P-005

- 目标：约束 AI 的交付节奏（流程类指令）。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 不要一下子做完，一步一步来，做完一步回报后再给下一步。

### 结果

- **采用**
- 后续改为"每轮只推进一个可验证的闭环"，并在每轮结尾给出可对照的数字表。
- 验证：每轮结束都能给出可复现的验收步骤和期望值。

---

## P-006

- 目标：在 Unity 内建"巡检记录"面板（列表 / 详情 / 刷新 / 空状态 / 错误提示）。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 任务三：在 Unity 里做一个巡检记录面板，只读后端 `GET /api/issues`，不要改后端，也不要动提交脚本。

### 结果

- **采用**
- 新建 `Assets/Scripts/InspectionRecordList.cs` 与 `Assets/Editor/RecordPanelBuilder.cs`。
- 验证：面板能列出后端 17 条记录；后端不可用时显示错误提示而非白屏。

---

## P-007

- 目标：修复 Play 后 UI 巨大、文字被裁。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 一 Play 界面就变得特别大，字都显示不全，修一下。

### 结果

- **采用**
- 真因：`CanvasScaler` 参考分辨率是竖屏 750x1334 且 `match=0`（只按宽度匹配），PC 宽屏下缩放飙到 2.5 倍以上。改为 1920x1080 + `match=0.5`，并加了菜单项"修复 UI 缩放"。
- 验证：Play 后面板完整落在屏幕内，无裁切。

---

## P-008

- 目标：给记录详情加状态操作按钮，能改 `open / in_progress / resolved` 并确认后端持久化。

### Prompt 原文

> 【原文已随会话上下文压缩丢失，以下为依据上下文还原的要点，非逐字原文】
> 任务四（状态管理）：在记录详情区加状态操作按钮，Unity 发 PATCH 更新状态，刷新后确认后端真的存下来了。先查后端支不支持，不支持再补。

### 结果

- **采用**
- 查证后端 `main.go` 已有 `PATCH /api/issues/{id}/status`，无需新增；Unity 侧补 `UpdateIssueStatus()` 与三个按钮。
- 验证：`tools/status_smoke.py` 15 项冒烟全通过；改完刷新列表状态保持。

---

## P-009

- 目标：在记录列表上方增加筛选区（关键词搜索 + 设备 / 优先级 / 状态三个下拉 + 重置）。

### Prompt 原文

> 任务四：巡检记录筛选与搜索
> 1. 最终希望实现什么效果？
> 在你现在的"巡检记录"窗口顶部或左侧列表上方，增加一个筛选区域。
>
> 例如：
> ```
> ┌──────────────────────────────────────────────────────┐
> │                       巡检记录                       │
> ├──────────────────────────────────────────────────────┤
> │ 关键词：[输入设备名或问题标题……] [搜索]              │
> │                                                      │
> │ 设备：[全部设备 ▼]  优先级：[全部 ▼]  状态：[全部 ▼] │
> │                                                      │
> │  [重置筛选]                         共找到 17 条记录  │
> ├──────────────────────┬───────────────────────────────┤
> │ 巡检记录列表          │ 记录详情                      │
> │                      │                               │
> │ 水泵异响              │ 设备名称：水泵机组 A          │
> │ 优先级：高            │ 问题标题：水泵                │
> │ 状态：待处理          │ 问题描述：水泵运行异常        │
> │                      │ 优先级：高                    │
> │ 阀门损坏              │ 状态：待处理                  │
> │ 优先级：中            │                               │
> │ 状态：处理中          │                               │
> └──────────────────────┴───────────────────────────────┘
> ```
>
> 这是目标示意图，不要求你重新制作整个窗口。保留现有界面，只需要在记录列表上方增加筛选控件即可。
>
> 2. 建议实现的四种功能
> 功能 A：关键词搜索
> 用户可以输入：
> - 水泵：查找设备名称或问题标题包含"水泵"的记录。
> - PUMP-A：查找对应设备编号的记录。
> - 异响：查找问题描述中包含"异响"的记录。
> 建议先支持以下字段：
> - 设备名称 targetName
> - 设备编号 targetId
> - 问题标题 title
> - 问题描述 description
>
> 功能 B：按优先级筛选
> 增加一个下拉框：
> - 全部优先级
> - 高优先级 high
> - 中优先级 medium
> - 低优先级 low
> 例如选择"高优先级"，列表只显示优先级为 high 的记录。
>
> 功能 C：按处理状态筛选
> 增加一个下拉框：
> - 全部状态
> - 待处理 open
> - 处理中
> - 已解决
> 这里需要注意：你的现有记录中已经出现了 status: "open"，但其他状态的具体取值还需要与你的 Go 后端保持一致。不要直接假定后端已经支持所有状态。
>
> 功能 D：按设备筛选
> 增加一个下拉框：
> - 全部设备
> - 水泵机组 A
> - 水泵机组 B
> - 阀门 A
> - 其他设备……
> 设备选项最好根据实际设备记录动态生成，而不是写死在代码中。这样以后增加设备时，不必再修改筛选代码。
>
> 3. Unity 中应该怎么实现？
> 建议采用以下结构：
> ```
> Canvas
> └── InspectionRecordPanel
>     ├── Header
>     ├── FilterPanel
>     │   ├── SearchInput
>     │   ├── SearchButton
>     │   ├── DeviceDropdown
>     │   ├── PriorityDropdown
>     │   ├── StatusDropdown
>     │   └── ResetButton
>     ├── RecordListPanel
>     │   └── Content
>     └── RecordDetailPanel
> ```
> 这些是建议新增或调整的对象名称。如果你的现有对象名称不同，可以继续使用原来的名称，不必为了统一命名而全部重建。
>
> 实现逻辑：
> 1. 从后端获取巡检记录。
> 2. 将获取的数据保存到一个完整的记录集合中。
> 3. 用户输入关键词或改变下拉框时，筛选这份集合。
> 4. 清空左侧列表中的旧条目。
> 5. 根据筛选结果重新生成列表项。
> 6. 更新"共找到多少条记录"的文字。
> 7. 如果筛选结果为空，显示"没有符合条件的巡检记录"，而不是留下旧记录。
> 需要特别注意：筛选时不要直接删除原始记录数据。 只改变界面显示的记录集合，否则用户清空筛选条件后，可能无法恢复完整列表。

### 结果

- **采用**
- 数据分两层：`items` 存后端全量、`visible` 存筛选结果，筛选只重算 `visible`，从不删 `items`，因此重置一定能回到全量。
- 设备下拉按记录去重**动态生成**（含 4 条空设备名的脏数据，归到"未知设备"）。
- 状态取值严格照后端 `main.go`（`open / in_progress / resolved`）实现，未假定第四个值。
- 关于下拉展开被遮住的顾虑：查证 TMP 展开时会自带 `overrideSorting=30000` 的 Canvas，**不会**被列表背景遮住（我一开始的注释写错了，已改正，避免留下错误结论）。
- 顺带修了一处：`ClearRows()` 改成先 `SetActive(false)` 再 `Destroy`。`Destroy` 只标记删除、帧末才真消失，而搜索框是边打边筛，不先隐藏会让新旧两批行同帧参与排版、列表高度乱跳。
- 验证：写 `tools/filter_smoke.py` 拉后端全量按同一套规则算一遍，给出对照表（关键词"水泵"应 8 条、"PUMP-A" 5 条、"异响" 0 条走空状态、重置回 17 条）；编辑器日志里抓到实机运行的筛选记录，设备下拉生成 5 个设备、叠加筛选 3→2→0→17 全部对上。

---

## P-010

- 目标：确认 P-009 的筛选功能是否真的完成了。

### Prompt 原文

> 任务四成功了吗

### 结果

- **未采用 AI 的措辞，改为如实说明**：上一轮说的"任务四完成"指的是**状态管理**（PATCH 改状态），不是这次的筛选。筛选当时一行功能代码都还没写。
- 这一步的作用是纠偏：AI 内部把两个"任务四"混为一谈了，用户一追问才暴露。之后明确区分"任务四-状态管理"与"任务四-筛选搜索"。

---

## P-011

- 目标：把上一轮没做完的筛选功能补完。

### Prompt 原文

> 请继续完成未完成的任务。

### 结果

- **采用**
- 完成筛选代码后，又做了两件验证：解析 `.scene` 文件确认控制器 7 个筛选字段全部非空；从编辑器日志里提取实机运行记录（发现用户已 Play 过，设备/优先级/状态/空状态/重置五项实机通过，唯独关键词输入框没敲过字）。
- 中途 `PriorityDropdown` 在场景文件里出现 2 次，查证是"上报表单"里的同名字段，**不是重复物体**（同名不等于重复，要看父级链）。
- 验证：编译 `Tundra build success`，`error CS` 计数 0。

---

## P-012

- 目标：实现列表与三维场景的双向联动，并用设备编号（而非显示名）关联记录。

### Prompt 原文

> 操作一：点击巡检记录 → 定位三维设备
>
> 用户在左侧列表点击"水泵"记录后：
> 1. 右侧显示这条记录的详细信息。
> 2. 三维场景定位到水泵机组 A。
> 3. 对应的问题标记高亮显示。
> 4. 用户可以直观地看到问题发生在哪台设备上。
>
> 操作二：点击三维设备 → 查看巡检记录
>
> 用户在三维场景中点击水泵机组 A 后：
> 1. 系统获取设备编号 PUMP-A。
> 2. 在已有巡检记录中查找该设备的问题。
> 3. 展示该设备相关的巡检记录。
> 4. 用户可以选择其中一条查看详细信息。
>
> 操作三：一个设备有多个问题
>
> 例如，水泵机组 A 有三条问题记录：
> - 水泵异响
> - 水泵振动异常
> - 水泵温度过高
> 点击水泵机组 A 时，应当显示这三条记录，而不是只显示最新的一条。不要仅凭设备显示名称关联记录。 设备名称可能修改，但稳定的设备编号更适合作为关联依据。
>
> 3. 三维标记也需要保存问题 ID
> 这一点尤其重要。
>
> 你现在已经实现提交成功后标记变绿并留在场景中。接下来需要确认每个标记是否保存了对应的巡检记录 id。

### 结果

- **采用**
- 两个设计点先征询了用户（AI 主动提问，未擅自猜测）：
  1. 单击设备是"查看记录"还是"上报问题"（两者抢同一个左键）→ 用户选"先选工具再点场景"：单击 = 查看记录，上报需先点"＋ 新增问题"进入上报模式。
  2. 三维标记是否按后端全量记录自动生成 → 用户选"全部记录自动生成"，颜色跟状态走（待处理红 / 处理中橙 / 已解决绿）。
- 新建 `Assets/Scripts/IssueMarkerLayer.cs`：每个球记住 `recordId` / `targetId`，按后端数据增删改，点列表任一条可精确高亮对应球。
- 关联依据改成 `targetId`（设备编号），不是显示名 —— 符合用户第 3 点要求。
- 记录 id 的绑定链路：后端 POST 回包 id → `InspectionApiClient.LastCreatedId` → `ConfirmCurrentMarker(id, targetId, status)` → `IssueMarkerDot.recordId`。
- 相机聚焦做了细节处理：面板占着屏幕中间，目标居中等于看不见，所以让相机偏转一个角度把设备送到面板右侧空白区（不是挪位置，挪了位置仍会 LookAt 回正中）。
- 验证：`tools/link_smoke.py` 对账，点水泵机组 A 应显示 5 条、水泵机组 B 3 条、控制柜 A 3 条、控制柜 B 2 条；无孤儿记录。

---

## P-013

- 目标：解释"为什么一台设备上有很多球，但列表里只有两个问题"。

### Prompt 原文

> @image#1:Clipboard_Screenshot.png @image#2:Clipboard_Screenshot-1.png 为什么设备上那么多球 但其实只有两个问题

### 结果

- **采用**，结论是**球并没有重复生成**：日志显示"新建 15、更新 0、跳过 2"，之后每次刷新都是"更新 15"，15 个球 = 15 条有坐标的记录（另 2 条坐标全 0 被跳过）。
- 真正原因是控制柜 B 那面墙上实际有 5 条记录，其中 3 条是早期（10-27 晚）提交的，当时没取到设备编号，`targetId` 为空 → 场景里看得见、列表里按编号筛不出来。
- 修复：给 `IssueMarkerLayer` 加 `ApplyVisibility()`，场景里的球跟着筛选结果走（没筛中的**隐藏而非删除**，重置后立即恢复）。
- 另写了 `tools/fix_orphan_targets.py` 补设备编号，但**只跑了预览、未执行**（属于会改动历史数据的不可逆操作，留给用户决定）。这是本项目中的一次"暂缓采用"。
- 验证：筛选"控制柜 B"后墙上只剩对应的球。

---

## P-014

- 目标：已处理的记录想删除。

### Prompt 原文

> 已经处理过的任务想删除怎么办

### 结果

- **采用**
- 查证后端 `main.go` 已有 `DELETE /api/issues/{id}`（`deleteIssueHandler`），**后端无需改动**；缺的是 Unity 侧调用与按钮。
- 补 `InspectionApiClient.DeleteIssue()` + 底部红色"删除记录"按钮 + **二次确认**（点两次，5 秒不确认自动取消，中途切换选中记录也会取消 —— 防止"给 A 上膛、点了 B、把 B 删了"）。
- 踩到的坑：`UnityWebRequest.Delete(url)` **不会自动挂 DownloadHandler**，必须手动加 `DownloadHandlerBuffer()`，否则读响应时空引用。
- 验证：建一条临时记录再删除的闭环自测，total 17 → 18 → 17，测试记录已清理，真实数据未动。

---

## P-015

- 目标：按题目"3. 必做功能"逐条核对是否都满足。

### Prompt 原文

> 核对一下 要求是否都满足
> 2. 固定技术栈
>
> | 部分        | 技术要求             | 作用                  |
> | --------- | ---------------- | ------------------- |
> | Unity 客户端 | Unity LTS、C#     | 虚拟场景、射线点击、空间标记和问题上报 |
> | 管理端       | React、TypeScript | 查看问题、修改状态           |
> | 后端        | Go               | 提供数据接口并持久化保存        |
>
> - Go 框架不限；
> - 数据可保存到 SQLite 或本地 JSON 文件；
> - 可以使用 AI 辅助开发，但不要求产品本身接入大模型；
> - 不要求使用任何付费素材或服务。
>
> ## 3. 必做功能
>
> ### 3.1 Unity 客户端
>
> 1. 使用 Unity 基础对象搭建一个简单的虚拟巡检现场，例如：
>    1. 地面和墙面；
>    2. 机柜、设备或管线；
>    3. 至少两个可以观察或点击的场景对象；
> 2. 支持用户移动并转动视角，能够查看场景中的不同位置；
> 3. 通过射线检测点击设备或场景表面；
> 4. 在实际命中位置放置一个简单的问题标记；
> 5. 填写并提交问题：
>    1. 标题：必填；
>    2. 描述：可选；
>    3. 优先级：`low`、`medium`、`high`；
>    4. 位置：标记的 `x/y/z` 坐标；
> 6. 提交成功或失败时，给用户明确提示；
> 7. 点击按钮或输入框时，不应误在场景中放置标记；
> 8. 一次有效点击最多创建一个待提交标记，取消填写后应移除未保存标记。
>
> 场景和标记可以直接使用立方体、球体、圆柱体等 Unity 基础对象，不考察美术效果。
>
> ### 3.2 React 管理端
>
> 1. 从 Go 后端获取问题列表；
> 2. 至少显示标题、优先级和状态；
> 3. 能将状态修改为：
>    1. `open`；
>    2. `in_progress`；
>    3. `resolved`；
> 4. 后端不可用时，页面应显示错误提示，不能直接白屏。
>
> ### 3.3 Go 后端
>
> Go 后端需要为 Unity 和 React 提供以下能力：
>
> 1. 提供简单的服务运行状态检查；
> 2. 接收并保存 Unity 上报的问题；
> 3. 返回已经保存的问题列表；
> 4. 根据问题 ID 修改处理状态；
> 5. 为新问题生成唯一 ID，默认状态为 `open`；
> 6. 标题为空或枚举值错误时，返回明确的参数错误；
> 7. 数据在 Go 服务重启后仍然存在；
> 8. Unity 和 React 使用同一个 Go 后端和同一份数据；
> 9. React 在浏览器中能够正常访问后端。

### 结果

- **采用**，逐条实测（不凭记忆）：
  - 技术栈三项全部符合；数据用 `backend/issues.json` 持久化，无付费服务。
  - 3.1 Unity：8 条全过（场景对象、WASD/右键视角、`Physics.Raycast`、命中点放标记、标题必填+优先级+坐标、成功失败提示、`EventSystem.IsPointerOverGameObject()` 拦截 UI 误点、单标记约束）。
  - 3.3 Go：9 条全过（健康检查 200、标题空 400、priority 错 400、status 错 400、不存在 404、重启后数据仍在、共用同一份数据、Vite `/api → 8080` 代理已配）。
  - 3.2 React：**原本不合格，已重写 `App.tsx`**。
- 核查过程中发现一件急事：后端 `issues.json` 在 17:47 被清空（total = 0）。用 17:30 拉的全量快照恢复，17 条全部回来，空文件留档为 `issues.json.bak_empty_1747`。原因是后端 `saveLocked` 直接 `WriteFile`、没有原子替换，进程重启/并发写时可能被截断成空。
- **React 端的具体问题（AI 早先产出的代码，本轮查出并修正）**：前端 `Issue` 类型读的是 `item.level` 和中文 `status`，后端实际返回 `priority`（`low/medium/high`）和英文 `status`（`open/in_progress/resolved`）。后果是优先级列空白、状态统计全 0、改状态必定 400（下拉把中文"处理中"发给只认英文的后端）、新增表单因 `DisallowUnknownFields` 也 400。已重写并对齐字段。
- 验证：`tsc -b` 零错误；通过前端代理端到端验证 `open → in_progress → open` 均 200，非法值 `done` 返回 400，数据已复原。

---

## P-016

- 目标：核对题目"4. 基础数据结构"——三端字段名称是否一致。

### Prompt 原文

> 检查4. 基础数据结构
> 三端字段名称应保持一致：
> ```json
> {
>   "id": "由后端生成",
>   "title": "1 号机柜温度异常",
>   "description": "机柜右侧温度明显偏高",
>   "priority": "high",
>   "status": "open",
>   "position": {
>     "x": 1.25,
>     "y": 0.8,
>     "z": 2.4
>   },
>   "createdAt": "由后端生成",
>   "updatedAt": "由后端生成"
> }
> ```

### 结果

- **采用**，查出**唯一一处不一致并修复**：Go 与 React 本来就一致，**Unity 的提交报文**用扁平 `x/y/z`，与题目要求的嵌套 `position` 不符。
- 具体改动：
  - `InspectionRecordDto`：删掉 `x/y/z`，换成 `public IssuePositionDto position`。
  - 提交通道 `/api/inspections` → `/api/issues`（与查询同一个接口，字段结构完全一致）。
  - `LastCreatedId` 改从 `item.id` 取（新接口回包没有顶层 `id`）。
  - 连带改了两处编辑器脚本常量和场景文件里已存的 `apiUrl`。
- 三个不改就会翻车的坑：
  1. 新接口回包**没有 `success` 字段**，而原代码有 `if (!response.success)` —— `JsonUtility` 对缺失的 bool 一律填 `false`，会把每次成功提交都判成失败（记录重复进 outbox + 界面报红）。已改为"HTTP 2xx 即成功"。
  2. `apiUrl` 是序列化字段，改 C# 默认值**不会**改场景里已存的值 → 在 `Awake()` 里加了旧通道纠正。
  3. 新接口开了 `DisallowUnknownFields`，DTO 加字段必须同步改后端白名单，否则 400。
- 验证：用 `JsonUtility` 输出格式的报文 POST → **201**，`position` 落库 `{x:1.25, y:0.8, z:2.4}` 正确，status 默认 `open`，`id` / `createdAt` / `updatedAt` 均由后端生成；DELETE 后 total 回到 17，未动真实数据。
- 说明：题目示例里没有 `targetId` / `targetName`，这两个是为"按设备编号关联记录"（P-012）加的扩展字段，不构成不一致。

---

## P-017

- 目标：生成本文件，记录开发过程中发给 AI 的全部指令。

### Prompt 原文

> 请在项目根目录提交 AI_LOG.md，按实际顺序保存本题中所有由你主动发给 AI 的问题或指令，包括追问和纠错。
> 每条记录使用以下简化格式即可：
> ## P-001
>
> - 目标：解决什么问题
>
> ### Prompt 原文
>
> > 实际发送给 AI 的问题或指令
>
> ### 结果
>
> - 采用 / 修改后采用 / 未采用
> - 简要说明如何验证
>
> 要求：
> - 必须保留 Prompt 原文，不能只写总结；
> - 不需要记录 IDE 的逐字自动补全；
> - 至少记录一次你检查、修改或放弃 AI 结果的过程；
> - 你需要能够解释最终提交的核心代码；
> - 不得提交 API Key、Token、账号、个人隐私或其他敏感信息；如需脱敏，请使用类似 [REDACTED: API token] 的占位内容。

### 结果

- **采用**
- 文件写在 `InspectionSystem/` 项目根目录（即本文件）。
- 对"至少记录一次检查/修改/放弃 AI 结果"的落实：P-004（纠错 AI 早先的标记清理逻辑）、P-013（放弃执行 `fix_orphan_targets.py`，仅预览）、P-015（重写 AI 早先产出的 React `App.tsx`）、P-016（修正 AI 早先的扁平坐标提交报文）。
- 早期 8 条 Prompt 原文随会话压缩丢失，已如实标注为还原要点，未编造逐字原文。
- 无敏感信息需要脱敏。

---

## P-018

- 目标：对照评分标准第 8 条（加分项）与第 9 条（主要关注点），核查哪些已具备、哪些是缺口。

### Prompt 原文

> 8. 鼓励的加分项参考
> 完成必做功能后，可以根据巡检场景自主选择有价值的扩展。以下方向仅供参考，不要求全部实现，也不限于此：
> - 从后端加载问题，并在固定虚拟场景中恢复对应标记；
> - 点击标记查看问题信息；
> - 根据优先级或状态改变标记样式；
> - 为设备增加编号或区域信息；
> - React 增加筛选、详情或删除；
> - 更完整、易用的场景操作和 UI；
> - 使用 AR Foundation XR Simulation 展示向 AR 版本迁移的思路；
> - Docker、自动化测试或更规范的工程组织；
> - 其他能够解决现场巡检实际问题的功能。
> 自主扩展是纯加分项，未实现不会扣分。加分主要依据业务价值、实现质量和稳定性，不以功能数量或技术复杂度为准。
> 9. 我们主要关注
> 1. 是否正确使用射线检测，在场景实际命中位置放置标记；
> 2. Unity、React、Go 是否形成可运行的最小闭环；
> 3. 场景操作、UI 输入和网络失败是否有基本处理；
> 4. 是否理解真实 AR 与普通 Unity 3D 场景的主要差异；
> 5. 遇到问题时，是否能够定位、修正并验证；
> 6. 是否合理使用 AI，而不是直接复制无法解释的代码；
> 7. 是否如实说明未完成内容和已知问题。
> 选择本备选题不会因为未提供真实 AR 真机演示而扣分，后续交流中可能会进一步了解候选人的 AR 基础知识。

### 结果

- **采用（作为核查清单）**
- 我逐条读源码核对而不是凭记忆报结论，结论：加分项 4 项已具备（后端恢复标记、React 筛选/详情/删除、场景操作与 UI、离线 outbox 重发）、3 项部分具备、2 项缺失（AR Foundation、Docker/测试）。
- 缺口明确后我把 4 个可补项交给用户选择，用户全部选中 → 见 P-019 ~ P-022。
- 关注点 4（AR 差异）当时代码和文档里都没有体现，是最大缺口 → 由 P-019 的 `AR_MIGRATION.md` 补齐。

---

## P-019

- 目标：补齐「AR 迁移思路」这一加分项，并给出能口头解释的 AR 基础要点。

### Prompt 原文

> AR 迁移说明文档（推荐先做）｜写 AR_MIGRATION.md：真实 AR 与虚拟 3D 场景的差异（相机位姿/坐标系与尺度/平面检测/光照/射线来源）+ 对应代码改动点 + 面试常问的 AR 基础问答要点。不用装 AR Foundation 包，不影响现有功能

### 结果

- **采用**
- 产出 `AR_MIGRATION.md`（项目根）。七个差异逐条对应到现有代码的具体位置：相机由 `TrackedPoseDriver` 驱动、`InspectionCamera` 手动控制要删、`Physics.Raycast` 换 `ARRaycastManager`、世界坐标换「锚点 + 相对偏移」、`InspectionTarget` 靠 Mesh 认设备在 AR 里不成立。
- 明确写出**没有真机 AR 演示**，文档是迁移方案不是已实现功能；同时说明 `position` 字段在 AR 版必须重新定义（世界坐标 → 相对锚点偏移），这是迁移的主要工作量。
- 验证：文档中引用的行号与方法名均从当前源码 grep 核对（`InspectionRaycaster.cs:163/200`、`PlaceMarker()`、`IssueMarkerLayer.Sync()`）。

---

## P-020

- 目标：标记按优先级区分样式；给设备加区域信息并三端打通。

### Prompt 原文

> 标记按优先级 + 设备区域｜high/medium/low 用不同球大小和光环区分；InspectionTarget 增加区域字段（如"一号泵房"），列表和 React 都能按区域看

### 结果

- **采用（有一处按实际情况调整）**
- 优先级不用"光环"改用**大小**：颜色通道已经被状态占用（待处理/处理中/已解决），再挤进优先级用户分不清"红色"是状态还是优先级。改成一个通道一个维度——状态=颜色、优先级=大小（high 1.45 倍 / medium 1.0 / low 0.72）。
- 区域三端打通：`InspectionTarget.zone` → 提交报文 → 后端 `Issue.Zone` + `normalize()` 兜底「未分区」→ React 详情与筛选下拉 → Unity 详情新增「所属区域」一行。场景生成脚本按类型分区（泵/阀=一号泵房，控制柜=配电区）。
- 验证：后端 `go build` 通过；新写的 `go test` 里 `TestCreateIssueSavesZoneAndDefaultsWhenMissing` 覆盖"带区域入库"与"不带区域兜底"；重启服务后 `GET /api/issues` 17 条都在、历史记录 zone 全部兜底为「未分区」；前端 `tsc -b` 0 错误。
- 未做：Unity 列表的"按区域筛选下拉"没加（面板要新增控件并重跑菜单，收益有限），区域目前可查看、React 端可筛选。

---

## P-021

- 目标：让三维标记可以直接点击查看详情。

### Prompt 原文

> 点击标记查看问题信息｜给标记球加可点击能力：射线分层（先设备层、没命中再标记层），点到球就高亮 + 打开详情 + 相机飞过去

### 结果

- **修改后采用**
- 没有采用"射线分层"：给球加 Collider 会让它挡住设备点击（之前正是为了避免这个才把 Collider 删掉的），而且要额外配 Layer、改场景设置，风险高。
- 改成**屏幕空间最近标记拾取**：`IssueMarkerLayer.PickByScreenPoint()` 把可见标记投影到屏幕，取光标 26px 内最近的一个。完全不参与物理求交，既不挡设备点击也不用配 Layer，被筛选隐藏的球自动退出拾取。
- 顺序定为"设备优先、标记兜底"：反过来的话点设备会先被标记吃掉，想看"这台设备的全部记录"的用户会被单条问题打断。
- 命中后走 `InspectionRecordList.SelectById()`，与"列表里点一条"是同一条路径（开面板 + 切详情 + 飞相机 + 高亮），行为不会分叉。
- 状态：代码已写完。（**后续更新**：19:59 已编译通过，`Assembly-CSharp.dll` 重新生成、日志无
  `error CS`；但此时演示数据已被删空，标记拾取本身仍未实机点过一次 —— 详见 P-025。）

---

## P-022

- 目标：补工程规范性——Docker 一键编排 + 自动化测试。

### Prompt 原文

> Docker + 自动化测试｜Go 多阶段 Dockerfile + React 构建托管 + docker-compose 一键起，再补一个后端接口冒烟测试脚本

### 结果

- **采用（冒烟脚本改成 Go 原生测试）**
- 没写 Python 冒烟脚本，改写成 `backend/main_test.go`（`go test ./...`）：更规范、可进 CI、不依赖额外运行时。11 个用例全绿。
- 关键设计：每个用例 `chdir` 到 `t.TempDir()`，否则 `appendRawRecord` 会把测试数据写进真实的 `inspection_records.json`；持久化断言靠"重新 `NewIssueStore` 读同一文件"而不是看内存切片。
- **一次检查并修正 AI 结果**：初版测试有两条断言写错了（期望中文状态返回 400、期望改文件后接口立即返回旧数据），实测发现后端对中文状态是**故意兜底**的、store 启动时已把数据读进内存。我改的是测试断言而不是后端行为——后端的兜底是兼容早期数据的正确设计。
- Docker：`backend/Dockerfile`（多阶段 + 静态二进制 + HEALTHCHECK + 数据卷）、`frontend/Dockerfile`（Node 构建 → nginx，`nginx.conf` 反代 `/api` 到 backend 容器）、`docker-compose.yml`。
- 为容器化把 `dataFile` 从 const 改成 var，支持 `INSPECTION_DATA_FILE` 环境变量指到挂载卷。
- **未实机验证**：本机没有安装 Docker Desktop，`docker` 命令不存在，编排文件只做了静态编写，已在 README 里如实标注。

---

## P-023

- 目标：把 Unity、React、Go 三端的完整源码汇总到 `D:\Astudy\work\AgentS\InspectionSystem`。

### Prompt 原文

> 1. Unity、React 和 Go 的完整源码打包在到D:\Astudy\work\AgentS\InspectionSystem

### 结果

- **采用**
- Unity 工程原本在团结引擎 Hub 的工作目录 `D:\Tuanjie Cowork\HubFiles\InspectMark`。
  把它的源码部分（`Assets/` + `Packages/` + `ProjectSettings/`，43MB）复制到 `InspectionSystem/unity/`，
  **剔除 `Library/`（452MB 引擎缓存）、`Temp/`、`Logs/`、`UserSettings/`**——
  这些是打开工程后自动重建的产物，不是源码。顺带剔除了 `SampleScene.scene.bak_before_urlfix`。
- 复制后校验：13 个 `.cs`（运行时 8 + 编辑器 5）齐全；场景里 `apiUrl` 为
  `http://127.0.0.1:8080/api/issues`；8 台设备的 `targetId`（PUMP-A~C / VALVE-A / CABINET-A~D）都在；
  `ProjectVersion.txt` 为 2022.3.62t17。
- 38MB 字体资源**全部保留**：两个 SDF 都被代码引用 —— `InspectionYaHei SDF.asset` 是现役动态字体，
  `6a40e936c7590 SDF.asset` 在 `IssueFormPanelBuilder.cs:39` 作兜底、`.ttf` 是它的源字体。
  提前查过引用关系才决定不删，否则中文界面会全部变成方块。
- 新增根 `.gitignore`（Unity 缓存 / node_modules / dist / `*.exe`）；README 补「源码清单」章节，
  「三端在哪」表的 Unity 行从引擎工作目录改指 `unity/`，并说明如何用引擎打开。
- 另出交付压缩包 `D:\Astudy\work\AgentS\InspectionSystem-source-20261028.zip`
  （16.4MB / 198 文件，排除 node_modules、dist、`backend.exe` 与引擎缓存），便于整体交付。
- 规模：Unity 8265 行 C#（13 个文件）；Go 1061 行（`main.go` + `main_test.go`）；React 1544 行（`App.tsx` + `App.css`）。

**检查**：逐项比对复制后目录与源工程的文件清单；再用 `zipfile` 回读压缩包，确认关键文件
（13 个 `.cs`、`SampleScene.scene`、字体资产、`main.go`、`main_test.go`、`App.tsx`、
`AI_LOG.md`、`AR_MIGRATION.md`、`docker-compose.yml`）全部在位，且 `node_modules` 与 `.exe` 条目数为 0。

**修改**：最初只想复制 `Assets/Scripts`，检查后改为三个目录整体复制 —— 缺
`Packages/manifest.json`（含 `cn.tuanjie.codely.bridge` 等包依赖）和 `ProjectSettings/`
（URP、质量、输入、Tag 配置）会导致工程根本打不开。

---

## P-024

- 目标：回应「ticket-agent 已经用 Docker 容器化部署，为什么你说机器上不存在 Docker」这一质疑，
  并真正验证 `Dockerfile` / `docker-compose.yml` 能不能构建出镜像。

### Prompt 原文

> 我电商售后工单 Agent那个项目都用了docker容器化部署，为什么你又说机器上不存在

### 结果

- **采用**
- 先核实而不是辩解：`ticket-agent` 确实有 `Dockerfile` + `.dockerignore`，
  但它的 `README.md` 第 371 行原文写着「**本机无 Docker 环境**，现由 `ci.yml` 的 docker build job
  在 GitHub Runner 上验证」。两件事不矛盾——那个项目也从未在本机跑过容器。
- **我的失误**：明知本机无 Docker，上一轮却只在 README 标一句「未验证」就交差，
  没有套用项目里已有的「CI 云端验证」惯例。质疑的真正指向是「那你为什么不去验证」。
- 补 `backend/.dockerignore`（排除 `issues.json` / `inspection_records.json` / `*.exe`，
  运行期数据不该固化进镜像）与 `frontend/.dockerignore`。
  后者最关键的是 `node_modules/`：Dockerfile 先 `npm ci` 再 `COPY . .`，
  不排除的话宿主机 Windows 原生模块会覆盖容器里刚装好的 Linux 版，`npm run build` 必炸。
- 新增 `.github/workflows/ci.yml`，4 个 job：`go vet` + `go test`、
  `backend docker build`、`tsc -b && vite build`、`frontend docker build`。
- 建公开仓并推送 `https://github.com/duanjing082420-arch/inspection-system`
  （201 文件 / 67644 行）。三次 push 的 CI **全部 success**：
  `backend docker build` 27s、`frontend docker build` 14s、`go vet + go test` 22s、
  `tsc + vite build` 11s。
- README 与遗留项措辞同步改为「已验证能构建，未验证运行时」。

**检查**：离线核对了四处 `docker build` 的高频坑——
`go.mod` 声明 `go 1.22` 与 `golang:1.22-alpine` 对齐（写更高版本镜像编译器会拒绝构建）；
`package-lock.json` 存在；`nginx.conf` 上游写 compose 服务名 `backend:8080` 而非 localhost；
`NewIssueStore` 在数据文件不存在时返回空存储（容器首次启动挂空卷不会崩）。

**修改**：CI 首跑出现 `Restore cache failed ... go.sum` 警告，
不是失败但输出不干净 —— 后端只用标准库没有 `go.sum`，给 `setup-go` 加 `cache: false` 消除。

**放弃**：没有为了让 `docker compose` 能在本机跑而安装 Docker Desktop。
本机外网大文件下载极慢（历史实测 15MB 量级要几十分钟），装一个几百 MB 的桌面应用不划算，
且它并不能比 CI 多验证出什么——CI 已证明镜像能构建。

---

## P-025

- 目标：让 `AI_LOG.md` 与 `README.md` 反映项目**当前真实状态**，而不是上一轮写完时的状态。

### Prompt 原文

> 更新AI_log 和readme

（中间用户补充说明：）

> 我自己删掉的

### 结果

- **采用**
- 没有直接重写文档，而是先把当前状态查了一遍，发现两处已与文档不符：
  1. **Unity 已经编译了**：`Assembly-CSharp.dll` 从 18:06 更新到 **19:59**，
     `Editor.log` 全文无 `error CS` —— 文档里"待编译"的说法已过期。
  2. **后端数据变成 0 条**：`issues.json` 只剩 2 字节（`[]`），时间戳 20:00。
- 数据清空一度被我当成后端 bug（`saveLocked` 覆盖写把文件写坏）去查，
  查 `Editor.log` 后发现多条 `DELETE /api/issues/{id}` **成功**记录，
  向用户确认后得知：是在 Unity 里逐条测试删除功能删掉的。**所以没有去"修"一个不是 bug 的问题。**
- README 新增「**实机验证记录**」章节，6 条证据全部来自 `Editor.log` 原文：
  编译通过、射线命中 `Pump_Housing`→「水泵机组 A」(PUMP-A)、点设备列记录、
  命中坐标 `(1.01, 0.69, -1.32)`、上报模式与取消、多条 DELETE 成功。
- 同时把「**没验证到的**」也写进同一节：演示数据删空后，标记生成 / 按优先级改大小 /
  点击标记看详情 / `zone` 入库这四项在空数据下跑不出来，补一条带坐标的记录即可验证。
- README 已知问题第 4 条改写为「数据现在是空的 + 如何恢复」，并给出
  `git show 5700d2d:backend/issues.json > backend/issues.json`。
- AI_LOG 两处过期事实纠正：遗留项里"待编译"改为"已编译但标记四项未验证"；
  P-021 的"等编译"状态补了后续注记，避免后人读到旧状态被误导。

**检查**：先 `git show 5700d2d:backend/issues.json | grep -c '"id"'` 确认 17 条备份确实还在，
才把恢复命令写进文档 —— 没验证过的命令不写。另确认 `Editor.log` 真实路径是
`C:\Users\31379\AppData\Local\Tuanjie\Editor\Editor.log`（不是 Unity 默认的 `LocalLow\DefaultCompany\`，
团结引擎的日志位置不同）。

**修改**：初稿想在 README 写"17 条数据"，检查后改掉 —— 当前实际是 0 条，
写旧数字等于给人一份对不上的文档。

---

## P-026

**目标**：对比第二份同题参考实现 `D:\31379\Downloads\ar-inspection-main` 的 server 结构，并按差异补做缺失项。

**Prompt 原文**：

> "D:\31379\Downloads\ar-inspection-main 这个人的呢"

（随后我在四个候选项中选择了：**后端 panic 恢复 + 优雅退出（推荐）**）

**结果**：

这份与我的技术选型最近（同为 net/http 标准库、同样有 DELETE），差异在：

| 维度 | 参考实现 | 我 |
|---|---|---|
| 文件组织 | 4 个文件同包（api/model/store/main）共 600 行 | 1 个 main.go |
| 路由写法 | `HandleFunc("GET /api/issues/{id}")`（Go 1.22+ 带方法与路径参数） | `HandleFunc("/api/issues/", …)` 手工切字符串 |
| 存储 | SQLite（modernc.org/sqlite） | JSON 文件 |
| 健壮性 | CORS / 日志 / panic 恢复 / 请求体上限 / 优雅退出 | 只有 CORS + 请求体上限 |
| 数据字段 | 多一个 `normal`（表面法线，标记朝向存库） | 多 `targetId` / `targetName` / `zone` |
| AR | 真装了 `com.unity.xr.arfoundation 5.2.2` + 独立演示场景 | 只有 AR_MIGRATION.md 文档 |

**检查后确认**：两份参考实现**都没有设备关联字段**，也都没把 issueId 写回标记对象
（第二份 README 第 7 节自述："位置只有世界坐标，没有设备编号／区域等业务语义，
同一坐标在不同时间上报无法区分设备"）。这两条是我独有且对题的加分项，
因此**不跟着对方改数据模型**。

**修改**（只动后端，零破坏）：

1. `backend/main.go` 新增 `withRecovery` 中间件——handler 内 panic 时返回 500，
   不再让整个进程崩掉（一次空指针不会把所有请求一起带走）。
2. `backend/main.go` 的 `main()` 改为 `http.Server` + `signal.NotifyContext`
   （`os.Interrupt` / `SIGTERM`）+ `Shutdown` 5 秒超时。
   中间件顺序 `withRecovery(withCORS(mux))`：先兜 panic，再挂 CORS。
3. `backend/main_test.go` 新增 `TestRecoveryMiddlewareTurnsPanicInto500`，
   用 httptest 直接打一个必 panic 的 handler，断言 500 与响应体。

**验证**：`gofmt -l` 无输出、`go vet` 干净、`go test ./...` **12 个用例全过**（新增 1 个）。
重编译后用新二进制重启服务：`/api/health` 正常、`/api/issues` 仍 17 条，
正常请求的 200 与 404 行为不受中间件影响。

**放弃的一项（如实说明）**：想实机验证「收到信号后优雅退出」，
Windows 上需用 `GenerateConsoleCtrlEvent` 发 CTRL_C_EVENT，
但该信号发往**整个控制台进程组**，会波及同一控制台上的 shell 及其他进程，风险不可控，故放弃。
**结论**：panic 恢复已实机验证；优雅退出的信号触发路径**未实机验证**，
Windows 上应在控制台按 Ctrl+C 触发（Go 在该平台支持 `os.Interrupt`），
被 `taskkill /F` 强杀则不会走优雅退出。

---

## 附录：核心代码索引（便于口头解释）

| 关注点 | 文件 | 说明 |
|---|---|---|
| 三端共用的数据结构 | `backend/main.go` `type Issue struct` | `id / targetId / targetName / title / description / priority / position{x,y,z} / status / createdAt / updatedAt`，三端完全一致 |
| Unity 通信层 | `unity/Assets/Scripts/InspectionApiClient.cs` | 提交 `Submit()`、拉列表 `GetIssues()`、改状态 `UpdateIssueStatus()`、删除 `DeleteIssue()`；失败进本地 outbox、下次启动自动重发 |
| Unity 记录面板 | `unity/Assets/Scripts/InspectionRecordList.cs` | 全量 `items` / 筛选后 `visible` 两层数据；筛选、详情、状态、删除 |
| 三维标记层 | `unity/Assets/Scripts/IssueMarkerLayer.cs` | 每个标记记住 `recordId` 与 `targetId`，按后端数据增删改，按状态着色 |
| 设备身份 | `unity/Assets/Scripts/InspectionTarget.cs` | 设备编号 `TargetId` 是关联记录的稳定依据 |
| React 管理端 | `frontend/src/App.tsx` | 类型对齐后端，界面中文 / 传输英文，改状态 PATCH |
| 后端持久化 | `backend/issues.json` | JSON 文件存储，重启后数据仍在 |
| 标记拾取 | `unity/Assets/Scripts/IssueMarkerLayer.cs` `PickByScreenPoint()` | 标记无碰撞体，靠屏幕投影距离拾取，不挡设备点击 |
| 设备区域 | `unity/Assets/Scripts/InspectionTarget.cs` `zone` | 区域随提交入库，React 可筛选、Unity 详情可看 |
| 自动化测试 | `backend/main_test.go` | `go test ./...`，11 个用例，临时目录隔离真实数据 |
| AR 迁移 | `AR_MIGRATION.md` | 差异对照 + 代码改动映射 + AR 基础问答要点 |

### 我能解释的三处关键实现

1. **为什么筛选不会把数据筛没**：筛选只重算 `visible` 列表，`items` 永远是后端返回的全量，重置只是把 `visible` 指回 `items`，所以一定能恢复。
2. **为什么标记能精确对应到一条记录**：提交成功时后端回包带 `id`，经 `LastCreatedId` 传给 `ConfirmCurrentMarker()`，存进该标记的 `IssueMarkerDot.recordId`；点列表某条时按 `recordId` 精确查到那个球，一台设备有 5 条记录也不会串。
3. **为什么改状态/删除后场景会跟着变**：标记层每次拉完列表都用全量 `items` 做一次 `Sync()`，后端没有的记录对应的球会被移除，状态变了颜色跟着变 —— 场景是数据的投影，不是独立状态。
4. **为什么点标记不用射线检测**：标记贴在设备表面，给它加 Collider 就会挡住设备点击（`CreateDot` 里特意把 Collider 删掉就是这个原因）。改用"世界坐标投影到屏幕、取光标 26px 内最近的一个"，既不进物理世界也不用配 Layer，还能让被筛选隐藏的球自动退出拾取。相应的代价是判定是"就近"而不是"精确命中"，所以半径要小（26px）。

### 遗留项（已知、未做）

- **没有真机 AR 演示**：`AR_MIGRATION.md` 是迁移方案文档，不是已实现功能。
- **Docker 只验证了构建、没验证运行时**：本机没有 Docker 客户端，
  `docker build` 已由 `.github/workflows/ci.yml` 在 GitHub Runner 上跑通（4 个 job 全绿），
  但 `docker compose up` 之后的容器联动、数据卷挂载没有实测过。
- **三维标记相关功能未实机验证**：P-020/P-021 改的 5 个脚本（区域链路 + 标记拾取）
  已于 **19:59 编译通过**（`Assembly-CSharp.dll` 已更新，日志无 `error CS`），
  但演示数据当时已被逐条删除，空数据下**标记生成 / 按优先级改大小 / 点击标记看详情 /
  `zone` 入库这四项跑不出来**，补一条带坐标的记录才能验证。
- 演示数据 17 条已被主动删除（测试删除功能），`issues.json` 现为 `[]`；
  首次提交 `5700d2d` 里仍有完整备份可随时恢复。
- React 新增表单目前未传 `targetId`，网页建出的记录会落进"未知设备"分组，三维场景里没有标记。
- 后端 `saveLocked` 是整文件覆盖写，理论上强杀进程可能写坏文件，原子替换（临时文件 + rename）未做。
