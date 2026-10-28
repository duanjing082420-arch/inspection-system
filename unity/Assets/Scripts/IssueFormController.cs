// IssueFormController.cs（整合版）
//
// 职责：把「点击场景放标记 → 填表单 → 提交到后端」这条链路真正闭起来：
//         点击场景物体 → 射线放一个待提交标记 → 弹出表单并显示巡检对象 + 命中坐标
//         → 本地校验（标题 / 描述 / 优先级 / 设备标识）→ 提交
//         → 成功：标记变绿留在场景、从本地队列移除、1.2 秒后关表单
//         → 失败：表单和标记都保留、记录留在本地队列待重发、可直接改完重试
//         → 取消：删除尚未提交的标记、关闭表单
//
// 整合版针对三个具体问题：
//   A. 提交按钮无响应
//      根因：面板生成器在编辑期用 onClick.AddListener 绑的是「非持久化监听器」，
//      不写进场景文件，一进 Play 就全丢。
//      修法：Awake 里按名字运行时重绑，并且绑定失败时不隐藏面板（保持可见 + 红字报错），
//      这样 Start 还能再绑一次；按钮引用缓存下来，提交中置灰防止连点。
//   B. 重复创建标记
//      修法：OpenWithMarker 先把可能残留的待提交标记清掉；提交成功后用 ConfirmCurrentMarker
//      把标记转成「已确认」（置空引用），后续点击不会误删；关闭面板用 token 版本号，
//      避免上一次的延时关闭把新打开的表单关掉。
//   C. 失败后丢失巡检记录
//      修法：提交前先写本地队列（InspectionApiClient.QueuePending），成功才移除；
//      失败/超时/断网记录都留在磁盘上，下次启动自动重发。取消只是关表单，不删队列。
//
// 挂载位置：IssueFormPanel 面板根物体（Tools ▸ 巡检 ▸ 创建上报面板 会自动挂好并接线）。
// 字段与 public 方法名全部沿用旧版，替换文件后不需要重新拖引用。

using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class IssueFormController : MonoBehaviour
{
    [Header("面板引用（生成器会自动接好）")]
    public GameObject panelRoot;
    public TMP_InputField titleInput;
    public TMP_InputField descInput;
    public TMP_Dropdown priorityDropdown;
    // 设备名称：显示射线命中的巡检对象（命中柜门/指示灯时会自动归到所属机柜）
    public TMP_Text objectText;
    public TMP_Text positionText;
    public TMP_Text statusText;

    [Header("后端通信")]
    // 唯一的通信客户端，挂在场景里的 InspectionServices 上（第13步菜单自动建）。
    // 表单不自己发 UnityWebRequest，只负责收集字段 + 显示结果，通信交给它。
    public InspectionApiClient apiClient;
    // PC 编辑器 + 本机 Go 后端：用 127.0.0.1
    // 真机（Android/iOS）调试时改成电脑的局域网 IP，例如 "http://192.168.1.8:8080"
    // 这个值会在 Awake 时同步给 apiClient（最终请求地址 = serverUrl + /api/issues）
    public string serverUrl = "http://127.0.0.1:8080";

    [Header("校验设置")]
    public int minTitleLength = 2;
    public int maxTitleLength = 100;
    public int maxDescriptionLength = 500;

    [Header("提交设置")]
    [Tooltip("提交成功后多久自动关闭表单（秒）")]
    public float autoCloseDelay = 1.2f;

    // 下拉框给用户看的是中文，发给后端的必须是英文枚举。
    // 两个数组一一对应，改任何一方都要同时改另一方（顺序必须一致）。
    static readonly string[] PriorityLabels = { "低", "中", "高" };          // 界面显示
    static readonly string[] PriorityValues = { "low", "medium", "high" };   // JSON 上报值

    static readonly Color StatusError = new Color(0.83f, 0.19f, 0.19f);
    static readonly Color StatusInfo = new Color(0.09f, 0.41f, 0.67f);
    static readonly Color StatusOk = new Color(0.11f, 0.55f, 0.28f);

    // 本次点击真正命中的物体（零件，如 Motor_Body / Cabinet_Door）
    private GameObject hitObject;
    // 由 InspectionTarget 解析出的归属设备
    private string currentTargetId = "";
    private string currentTargetName = "";
    private string currentZone = "";
    private bool currentTargetResolved;
    // 命中坐标（与面板显示一致，提交时一并上报，避免「显示值」和「上报值」不一致）
    private Vector3 markerPosition;
    // 放置标记的射线脚本（提交/取消后用来清理它维护的「当前标记」状态，并解锁场景点击）
    private InspectionRaycaster raycaster;

    private Button submitButton;
    private Button cancelButton;
    private bool submitting;
    private bool buttonsBound;

    // 表单版本号：每次打开/取消/提交都 +1。
    // 延时关闭表单的协程会带上开时的版本号，版本变了就不关 ——
    // 否则「提交成功 → 1.2 秒后关闭」会把这 1.2 秒内新打开的表单一起关掉。
    private int formToken;

    // 本次提交在本地队列里的编号（成功后用它移除，失败则保留）
    private string pendingClientId;

    void Awake()
    {
        if (panelRoot == null) panelRoot = gameObject;

        NormalizeServerUrl();
        EnsureApiClient();
        SyncApiClientUrl();
        SetupDropdown();

        buttonsBound = RebindButtons();
        SetStatus("");

        if (buttonsBound)
        {
            Hide();
        }
        else
        {
            // 按钮没找到就绝不隐藏面板：一旦隐藏，Start/Update 都不会再跑，
            // 就再也没有补救机会，用户只会看到一个「点了没反应」的表单。
            SetStatus("提交/取消按钮未绑定，请检查面板层级", StatusError);
            Debug.LogError("[巡检] IssueFormPanel 下没有找到 SubmitButton / CancelButton，按钮点击不会有反应。" +
                           "请确认它们仍在 IssueFormPanel 下且名字未改，或重新执行 Tools ▸ 巡检 ▸ 创建上报面板。");
        }
    }

    void Start()
    {
        // Awake 里绑定失败时的第二次机会（此时面板是可见的，Start 才会执行）。
        if (buttonsBound) return;

        buttonsBound = RebindButtons();
        if (buttonsBound)
        {
            Debug.Log("[巡检] 已在 Start 阶段补绑按钮。");
            SetStatus("");
            Hide();
        }
    }

    /// <summary>
    /// 地址兜底：场景里已经序列化的 serverUrl 不会被「改代码默认值」覆盖，
    /// 而模板里那个 192.168.1.100 是个并不存在的示例地址，忘了改就会一直连不上。
    /// 这里在运行时统一纠一次；只纠空值和示例地址，绝不把 http 改成 https。
    /// </summary>
    void NormalizeServerUrl()
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || serverUrl.Contains("192.168.1.100"))
        {
            serverUrl = "http://127.0.0.1:8080";
            Debug.Log("[巡检] serverUrl 是示例地址或为空，已自动切到 " + serverUrl);
        }
    }

    /// <summary>
    /// 确保拿到场景里唯一的 InspectionApiClient。
    /// 三级兜底：Inspector 手动拖的 → 场景里找 → 现场建一个 InspectionServices。
    /// 这样即使没跑第13步菜单，提交也不会因为「引用为空」直接失效。
    /// </summary>
    void EnsureApiClient()
    {
        if (apiClient != null) return;

        apiClient = FindObjectOfType<InspectionApiClient>();

        if (apiClient == null)
        {
            GameObject services = new GameObject("InspectionServices");
            apiClient = services.AddComponent<InspectionApiClient>();
            Debug.LogWarning("[巡检] 场景里没有 InspectionApiClient，已运行时创建 InspectionServices。" +
                             "建议执行 Tools ▸ 巡检场景 ▸ 第13步 创建后端服务对象，让它存进场景。");
        }
    }

    /// <summary>
    /// 把 serverUrl 同步成 apiClient 的实际请求地址。
    /// 旧版两边各存一份地址、以 apiClient 为准，在 Inspector 里改 serverUrl 不生效 —— 这是「改了没反应」的又一个来源。
    /// 只在 apiClient 地址异常（空 / 示例地址 / 与 serverUrl 不匹配）时才覆盖，不破坏手动配置。
    /// </summary>
    void SyncApiClientUrl()
    {
        if (apiClient == null) return;

        string current = apiClient.ApiUrl;
        bool broken = string.IsNullOrWhiteSpace(current) || current.Contains("192.168.1.100");
        bool mismatched = !broken && !string.IsNullOrWhiteSpace(serverUrl) &&
                          !current.StartsWith(serverUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        if (broken || mismatched)
        {
            apiClient.SetBaseUrl(serverUrl);
            Debug.Log("[巡检] 已同步后端地址：" + apiClient.ApiUrl);
        }
    }

    void SetupDropdown()
    {
        if (priorityDropdown == null) return;

        priorityDropdown.ClearOptions();
        priorityDropdown.AddOptions(new System.Collections.Generic.List<string>(PriorityLabels));
        priorityDropdown.value = 1; // 默认「中」= medium（与 PriorityValues[1] 对应）
        priorityDropdown.RefreshShownValue();
    }

    /// <summary>
    /// 按名字在面板层级里找到提交/取消按钮并绑定事件（运行时生效，幂等）。
    /// 返回是否两个按钮都绑上了。
    /// </summary>
    public bool RebindButtons()
    {
        Transform root = panelRoot != null ? panelRoot.transform : transform;

        submitButton = FindButton(root, "SubmitButton");
        cancelButton = FindButton(root, "CancelButton");

        if (submitButton != null)
        {
            // RemoveAllListeners 防止重复绑定（Awake + Start 各绑一次会触发两次提交）
            submitButton.onClick.RemoveAllListeners();
            submitButton.onClick.AddListener(OnSubmit);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.RemoveAllListeners();
            cancelButton.onClick.AddListener(Cancel);
        }

        bool ok = submitButton != null && cancelButton != null;

        if (!ok)
        {
            Debug.LogWarning("[巡检] 未在面板里找到 " +
                (submitButton == null ? "SubmitButton " : "") +
                (cancelButton == null ? "CancelButton" : "") +
                "，按钮点击不会有反应。请确认它们还在 IssueFormPanel 下且名字未改。");
        }

        return ok;
    }

    static Button FindButton(Transform root, string name)
    {
        // includeInactive = true：面板初始隐藏时也能找到（Awake 阶段面板可能已被 Hide）
        foreach (var b in root.GetComponentsInChildren<Button>(true))
        {
            if (b.gameObject.name == name) return b;
        }
        return null;
    }

    /// <summary>
    /// 由 InspectionRaycaster 在放置标记后调用。
    /// 第二个参数是「真正被射线命中的物体」（可能是零件），归属设备由 InspectionTarget 解析。
    /// 标记位置仍是 worldPosition（点击处的表面点），两者不要混用。
    /// </summary>
    public void OpenWithMarker(Vector3 worldPosition, GameObject hitObj, InspectionRaycaster source)
    {
        // 注意：这里绝对不能清理「待提交标记」。
        // 调用时序是 PlaceMarker 先建好标记、再调本方法，
        // 所以此刻 source.HasCurrentMarker 一定是 true，而那个标记就是本次刚创建的。
        // 曾经在这里加过一句「防重复清理」，结果把刚放出来的标记当场销毁，
        // 表现就是「标记一闪就没 / 提交成功也不变绿」。
        // 防重复的职责在 InspectionRaycaster.PlaceMarker 里：
        // 建新标记前销毁旧的，那里才是正确的位置。

        hitObject = hitObj;
        markerPosition = worldPosition;
        raycaster = source;
        pendingClientId = null;

        // 表单换一版：让上一次的延时关闭协程失效
        formToken++;

        // 设备归属统一走 InspectionTarget.ResolveInfo：
        // 一次拿到 id / name / 是否真的解析到设备，不用再自己拼 null 判断。
        ResolvedTarget resolved = InspectionTarget.ResolveInfo(hitObj);

        currentTargetId = resolved.Id;
        currentTargetName = resolved.Name;
        currentZone = resolved.Zone ?? "";
        currentTargetResolved = resolved.Resolved;

        if (objectText != null)
        {
            // 没挂 InspectionTarget 的物体也要能提交（用物体名兜底），
            // 但要在界面上说明，避免用户以为设备识别坏了。
            objectText.text = currentTargetResolved
                ? "巡检对象：" + currentTargetName
                : "巡检对象：" + currentTargetName + "（未挂 InspectionTarget）";
        }

        Debug.Log("[巡检] 巡检对象：" + currentTargetName +
                  "（ID: " + currentTargetId + "，命中零件: " +
                  (hitObject != null ? hitObject.name : "无") +
                  "，已识别标识: " + currentTargetResolved + "）");

        if (positionText != null)
        {
            positionText.text = string.Format(
                "X: {0:F2}   Y: {1:F2}   Z: {2:F2}",
                worldPosition.x, worldPosition.y, worldPosition.z);
        }

        if (titleInput != null) titleInput.text = "";
        if (descInput != null) descInput.text = "";
        if (priorityDropdown != null)
        {
            priorityDropdown.value = 1;
            priorityDropdown.RefreshShownValue();
        }

        submitting = false;
        SetSubmitInteractable(true);
        ShowPendingHint();

        if (panelRoot != null) panelRoot.SetActive(true);

        Debug.Log("[巡检] 表单已打开，命中坐标 = " + worldPosition);
    }

    public void Hide()
    {
        SetStatus("");
        if (panelRoot != null) panelRoot.SetActive(false);
    }

    /// <summary>
    /// 绑定到「取消」按钮：删除尚未提交的标记并关闭表单。
    /// 取消 = 丢弃这条记录，所以标记必须销毁，不留痕迹。
    /// 注意：本地队列里的记录不在这里删除 —— 那都是提交失败留下的，删了就真丢了。
    /// </summary>
    public void Cancel()
    {
        // 让还没跑完的延时关闭协程失效
        formToken++;

        if (raycaster != null)
        {
            raycaster.CancelCurrentMarker();   // 删除待提交标记
            raycaster.IsFormOpen = false;      // 解锁场景，允许再次放置
        }

        if (!string.IsNullOrEmpty(pendingClientId) && apiClient != null)
        {
            Debug.Log("[巡检] 本次记录此前提交失败，已留在本地队列（共 " +
                      apiClient.PendingCount + " 条），下次启动会自动重发。");
        }

        hitObject = null;
        currentTargetId = "";
        currentTargetName = "";
        currentZone = "";
        currentTargetResolved = false;
        pendingClientId = null;
        raycaster = null;
        submitting = false;
        SetSubmitInteractable(true);

        Hide();
    }

    /// <summary>绑定到「提交问题」按钮。</summary>
    public void OnSubmit()
    {
        if (submitting)
        {
            Debug.Log("[巡检] 上一条还在提交中，忽略这次点击。");
            return;
        }

        string error;
        if (!ValidateInput(out error))
        {
            SetStatus(error, StatusError);
            return;
        }

        EnsureApiClient();
        SyncApiClientUrl();

        if (apiClient == null)
        {
            SetStatus("提交失败：找不到后端通信组件", StatusError);
            return;
        }

        submitting = true;
        SetSubmitInteractable(false);
        SetStatus("正在提交…", StatusInfo);

        InspectionRecordDto record = BuildRecord();

        // 提交前把「界面显示值 → 上报值」打印出来，联调时直接在 Console 里核对：
        //   优先级：高 -> high
        //   提交的 JSON 里也必须是 "priority":"high"，不能是中文。
        Debug.Log("[巡检] 提交内容：targetId=" + record.targetId
                  + " / targetName=" + record.targetName
                  + " / 标题=" + record.title
                  + " / 优先级：下拉「" + CurrentPriorityLabel() + "」 -> " + record.priority
                  + " / 坐标=(" + (record.position != null ? record.position.x : 0f).ToString("F2") + ", "
                  + (record.position != null ? record.position.y : 0f).ToString("F2") + ", "
                  + (record.position != null ? record.position.z : 0f).ToString("F2") + ")");

        // 先本地留底再发请求：后端挂了、断网、超时，记录都在磁盘上，不会丢。
        pendingClientId = apiClient.QueuePending(record);

        int token = formToken;

        apiClient.Submit(record, (success, message) => OnSubmitResult(success, message, token));
    }

    void OnSubmitResult(bool success, string message, int token)
    {
        // 回调一定会被调用（超时/异常都有兜底），所以这里一定能把按钮复位。
        submitting = false;
        SetSubmitInteractable(true);

        if (token != formToken)
        {
            // 表单已经被新的点击接管（或已被取消），只记日志，不再改 UI。
            Debug.Log("[巡检] 提交结果返回时表单已被刷新，忽略本次结果：" + message);
            return;
        }

        Debug.Log("[巡检] 提交结果：" + (success ? "成功" : "失败") + " — " + message);

        if (success)
        {
            if (!string.IsNullOrEmpty(pendingClientId))
            {
                apiClient.RemovePending(pendingClientId);
                pendingClientId = null;
            }

            SetStatus(message, StatusOk);

            // 提交成功 = 确认记录：标记变绿留在场景里，不再属于「待提交」，
            // 后续点击新位置也不会把它删掉（与 Cancel 的销毁是两条不同的清理路径）。
            // 提交成功 = 确认记录：标记保留在场景里，并且**绑定这条记录的 id**，
            // 后续「列表点这条记录 → 高亮这个标记」才找得到它。
            // id 来自后端回包（InspectionApiClient.LastCreatedId），不是本地编的。
            if (raycaster != null)
            {
                string createdId = apiClient != null ? apiClient.LastCreatedId : null;

                // 优先级也要一起传：标记的尺寸按优先级区分（high 更大），
                // 不然这条要等下次 Sync 才会变尺寸，用户刚提交完看不到区别。
                // currentTargetId / 优先级都在后面被清空，所以要在清空之前读。
                string submittedPriority = CurrentPriority();
                raycaster.ConfirmCurrentMarker(createdId, currentTargetId, "open", submittedPriority);
                raycaster.IsFormOpen = false;
            }
            else
            {
                // 走到这里说明表单不知道标记是谁放的：标记不会变绿，
                // 而且下次点击场景时还会被当成「旧标记」销毁 —— 表现就是「标记消失了」。
                Debug.LogWarning("[巡检] 提交成功，但表单没有持有射线脚本引用，标记不会被确认变绿。");
            }

            hitObject = null;
            currentTargetId = "";
            currentTargetName = "";
            currentZone = "";
            currentTargetResolved = false;
            raycaster = null;

            StartCoroutine(CloseAfter(autoCloseDelay, token));
        }
        else
        {
            // 失败时保留表单和标记，允许改完直接重试；记录已在本地队列里。
            int pending = apiClient != null ? apiClient.PendingCount : 0;
            SetStatus(message + "（已暂存本地，共 " + pending + " 条待重发）", StatusError);
            Debug.LogError("[IssueFormController] 提交失败：" + message);
        }
    }

    /// <summary>本地校验：标题、描述长度、优先级、设备标识、坐标有效性。</summary>
    bool ValidateInput(out string error)
    {
        error = null;

        if (titleInput == null)
        {
            error = "标题输入框未接线，无法提交";
            return false;
        }

        string title = (titleInput.text ?? "").Trim();

        if (string.IsNullOrEmpty(title))
        {
            error = "问题标题不能为空";
            return false;
        }

        if (title.Length < minTitleLength)
        {
            error = "问题标题至少 " + minTitleLength + " 个字";
            return false;
        }

        if (title.Length > maxTitleLength)
        {
            error = "问题标题不能超过 " + maxTitleLength + " 个字";
            return false;
        }

        string desc = descInput != null ? (descInput.text ?? "").Trim() : "";
        if (desc.Length > maxDescriptionLength)
        {
            error = "问题描述不能超过 " + maxDescriptionLength + " 个字";
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentTargetId))
        {
            error = "没有识别到巡检对象，请重新点击场景中的设备";
            return false;
        }

        if (float.IsNaN(markerPosition.x) || float.IsNaN(markerPosition.y) || float.IsNaN(markerPosition.z))
        {
            error = "标记坐标无效，请重新放置标记";
            return false;
        }

        string priority = CurrentPriority();
        if (priority != "low" && priority != "medium" && priority != "high")
        {
            error = "优先级无效：" + priority;
            return false;
        }

        if (priorityDropdown != null &&
            (priorityDropdown.options == null || priorityDropdown.options.Count != PriorityValues.Length))
        {
            error = "优先级下拉项数量不对（应为 3 项）";
            return false;
        }

        return true;
    }

    InspectionRecordDto BuildRecord()
    {
        Vector3 p = markerPosition;   // 面板显示的命中坐标，保证「显示值」=「上报值」

        return new InspectionRecordDto
        {
            targetId = string.IsNullOrWhiteSpace(currentTargetId)
                ? (hitObject != null ? hitObject.name : "UNKNOWN")
                : currentTargetId,
            targetName = string.IsNullOrWhiteSpace(currentTargetName)
                ? (hitObject != null ? hitObject.name : "未知对象")
                : currentTargetName,

            // 区域：没配就留空，后端 normalize 兜成「未分区」。
            // 不能在这里编一个区域名 —— 那会让「未配置」和「配置成某某区」变得无法区分。
            zone = currentZone ?? "",

            title = (titleInput != null ? titleInput.text : "").Trim(),
            description = descInput != null ? (descInput.text ?? "").Trim() : "",
            priority = CurrentPriority(),   // 下拉「低/中/高」→ 枚举 low/medium/high

            // 嵌套 position，与后端 Issue、React 端字段名一致。
            // 后端 /api/issues 开了 DisallowUnknownFields，写成扁平 x/y/z 会直接 400。
            position = new IssuePositionDto { x = p.x, y = p.y, z = p.z },

            createdAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>若本地还有未发出的记录，在状态栏提醒一次（不打断正常填写）。</summary>
    void ShowPendingHint()
    {
        if (apiClient == null) return;

        int pending = apiClient.PendingCount;
        if (pending <= 0) return;

        SetStatus("有 " + pending + " 条记录未提交成功，将在联网后自动重发", StatusInfo);
    }

    void SetSubmitInteractable(bool value)
    {
        if (submitButton != null) submitButton.interactable = value;
        if (cancelButton != null) cancelButton.interactable = value;
    }

    IEnumerator CloseAfter(float seconds, int token)
    {
        if (seconds > 0f)
            yield return new WaitForSeconds(seconds);

        // 版本号对不上说明表单已经换了一次（用户又点了新位置，或已经取消），
        // 这时不能关 —— 否则新表单会被上一次的延时关闭顺手关掉。
        if (token != formToken)
        {
            Debug.Log("[巡检] 延时关闭已取消（表单已刷新）。");
            yield break;
        }

        Hide();
    }

    string CurrentPriority()
    {
        if (priorityDropdown == null) return "medium";

        // 先按「下拉框当前显示的文字」翻译：低/中/高 → low/medium/high。
        // 只按序号取值有个隐患：一旦有人改了下拉项顺序或文案，映射会悄悄错位，
        // 而界面上完全看不出来 —— 发出去的 JSON 才是唯一真相，所以以文本为准。
        string label = CurrentPriorityLabel();
        string byLabel = InspectionApiClient.NormalizePriority(label);
        if (byLabel != null) return byLabel;

        // 文本认不出来才退回按序号取，并且明确告警，方便定位是谁改了下拉项。
        int i = Mathf.Clamp(priorityDropdown.value, 0, PriorityValues.Length - 1);
        Debug.LogWarning("[巡检] 优先级下拉项「" + label + "」无法识别，按序号取：" + PriorityValues[i]);
        return PriorityValues[i];
    }

    /// <summary>下拉框当前选中的中文文本（如「高」）。</summary>
    string CurrentPriorityLabel()
    {
        if (priorityDropdown == null || priorityDropdown.options == null) return "";

        int v = priorityDropdown.value;
        if (v < 0 || v >= priorityDropdown.options.Count) return "";

        return priorityDropdown.options[v].text ?? "";
    }

    void SetStatus(string message, Color? color = null)
    {
        if (statusText == null) return;
        statusText.text = message;
        if (color.HasValue) statusText.color = color.Value;
    }
}
