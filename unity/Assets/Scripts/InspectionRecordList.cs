// InspectionRecordList.cs
//
// 职责：从 Go 后端 GET /api/issues 拉取巡检记录，在 Unity 的「巡检记录」面板里展示，
//       点某条看详情，可刷新，可**关键词搜索 + 设备/优先级/状态筛选**，空数据和连不上后端都有明确提示。
//
// 数据以**后端保存的为准**，不从场景里的绿色标记生成 —— 这样 Unity 重开、标记消失后，
// 之前提交的问题照样能查到。
//
// 挂载位置很关键：挂在 Canvas 下常驻可见的 InspectionRecordList 物体上，
// **不要挂在面板自己身上**。面板初始是隐藏的，组件挂在隐藏物体上 Awake 不会执行，
// 按钮事件就绑不上，表现就是「点了没反应」（这正是上报面板踩过的坑）。
// 面板本体通过 panelRoot 字段引用。
//
// 筛选的核心原则（改这块代码时不要破坏）：
//   items   = 后端拉回来的**全量**数据，只有 Refresh 才动它
//   visible = 按当前筛选条件算出来的**显示**集合，只由 ApplyFilter 重算
//   界面永远只画 visible。这样「清空筛选条件」一定能恢复完整列表，
//   因为原始数据从来没被删过 —— 筛选只改显示集合，不改数据源。

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class InspectionRecordList : MonoBehaviour
{
    [Header("面板")]
    public GameObject panelRoot;

    [Header("列表")]
    public Transform listContent;      // ScrollView 的 Content
    public GameObject itemTemplate;    // 列表项模板（初始隐藏）
    public TMP_Text emptyText;         // 空状态 / 错误提示

    [Header("详情（面板右侧）")]
    public TMP_Text detailTitle;
    public TMP_Text detailMeta;
    public TMP_Text detailDesc;

    [Header("操作")]
    public Button openButton;          // 左上角常驻「巡检记录」按钮
    public Button refreshButton;
    public Button deleteButton;        // 删除当前选中的记录（点两次确认）
    public Button closeButton;
    public TMP_Text statusText;
    public TMP_Text countText;

    [Header("状态操作")]
    // 三个按钮对应后端 StatusValues：open / in_progress / resolved
    public Button statusOpenButton;
    public Button statusProgressButton;
    public Button statusResolvedButton;

    [Header("筛选（任务四）")]
    public TMP_InputField searchInput;     // 关键词：设备名 / 设备编号 / 标题 / 描述
    public Button searchButton;
    public TMP_Dropdown deviceDropdown;    // 设备：选项由数据动态生成
    public TMP_Dropdown priorityDropdown;  // 优先级：全部 / 高 / 中 / 低
    public TMP_Dropdown statusDropdown;    // 状态：全部 / 待处理 / 处理中 / 已解决
    public Button resetFilterButton;

    [Header("三维联动（任务五）")]
    [Tooltip("留空会自动查找 Main Camera 上的 InspectionRaycaster")]
    public InspectionRaycaster raycaster;
    [Tooltip("用于聚焦飞行；留空自动取 Camera.main")]
    public Camera viewCamera;
    [Tooltip("「＋ 新增问题」按钮：进入上报模式")]
    public Button reportModeButton;
    [Tooltip("上报模式横幅（不在面板里，面板关掉时也要能看到）")]
    public TMP_Text modeBanner;
    [Tooltip("留空会自动 Ensure 一个")]
    public IssueMarkerLayer markerLayer;

    /// <summary>列表点击记录后，目标在屏幕上的横向落点（-1 最左、0 居中、1 最右）。</summary>
    [Tooltip("面板占着屏幕中间，目标居中就会被挡住；0.66 落在面板右侧空白区")]
    public float focusScreenOffset = 0.66f;

    [Header("后端通信")]
    public InspectionApiClient apiClient;

    static readonly Color ColorNormal = new Color(0.96f, 0.96f, 0.96f, 1f);
    static readonly Color ColorSelected = new Color(0.82f, 0.91f, 1f, 1f);
    static readonly Color StatusInfo = new Color(0.09f, 0.41f, 0.67f);
    static readonly Color StatusError = new Color(0.83f, 0.19f, 0.19f);
    static readonly Color StatusOk = new Color(0.11f, 0.55f, 0.28f);
    static readonly Color ColorMuted = new Color(0.35f, 0.35f, 0.38f);

    // 优先级下拉：显示文案 -> 后端英文值。索引必须和 PriorityFilter 一一对齐。
    static readonly string[] PriorityOptions = { "全部优先级", "高优先级", "中优先级", "低优先级" };
    static readonly string[] PriorityFilter = { "", "high", "medium", "low" };

    // 状态下拉：显示文案 -> 后端英文值。
    // 取值来自后端 main.go：status 只能是 open / in_progress / resolved，不要凭感觉加别的。
    static readonly string[] StatusOptions = { "全部状态", "待处理", "处理中", "已解决" };
    static readonly string[] StatusFilter = { "", "open", "in_progress", "resolved" };

    /// <summary>设备下拉第一项：表示不按设备筛。</summary>
    const string ALL_DEVICES = "全部设备";

    /// <summary>
    /// 没有设备编号的记录（早期「消防通道」那几条）在下拉里的占位 id。
    /// 不能直接用空串 "" —— 那是「全部设备」的含义，会把「未知设备」这一档吞掉。
    /// </summary>
    const string UNKNOWN_DEVICE_ID = "__unknown__";

    /// <summary>设备下拉每一项对应的 targetId，下标与 dropdown.options 严格对齐（0 号是「全部设备」）。</summary>
    readonly List<string> deviceIds = new List<string>();

    /// <summary>后端全量数据。只有 Refresh 会写它，筛选永远不碰。</summary>
    readonly List<IssueDto> items = new List<IssueDto>();

    /// <summary>筛选后的显示集合。界面只画这个。</summary>
    readonly List<IssueDto> visible = new List<IssueDto>();

    readonly List<GameObject> rows = new List<GameObject>();

    /// <summary>和 rows 一一对应，用来判断某一行是不是当前选中项。</summary>
    readonly List<IssueDto> rowIssues = new List<IssueDto>();

    /// <summary>当前选中的记录。用引用而不是下标 —— 筛选后下标会变，引用不会。</summary>
    IssueDto selected;

    /// <summary>选中记录的编号。刷新后靠它把选中项找回来。</summary>
    string selectedId;

    /// <summary>
    /// 重建设备下拉时置 true，挡掉 TMP_Dropdown 自己抛出来的 onValueChanged。
    /// TMP_Dropdown 的 value 赋值（ClearOptions / AddOptions / value=）在 Play 模式下会触发回调，
    /// 不挡住就会在加载数据时递归进 ApplyFilter。
    /// </summary>
    bool suppressFilterEvents;

    bool loading;
    float loadingStartedAt;

    /// <summary>状态更新进行中。防止连点：一次只让一个 PATCH 在飞。</summary>
    bool updatingStatus;
    float updatingStartedAt;

    /// <summary>删除进行中（一次只让一个 DELETE 在飞）。</summary>
    bool deleting;

    /// <summary>
    /// 删除的二次确认：第一次点只是「上膛」，5 秒内不再点就自动取消，
    /// 避免演示时误触直接把记录删掉（删除不可逆）。
    /// </summary>
    bool deleteArmed;
    float deleteArmedAt;
    const float DeleteArmSeconds = 5f;
    const string DELETE_LABEL = "删除记录";

    /// <summary>
    /// updatingStatus 兜底解锁时间（秒）。
    /// 和 loading 同理：协程里的异常抓不住，万一 PATCH 回调没回来，
    /// 超过这个时间强制解锁，否则三个状态按钮会永久置灰。
    /// </summary>
    const float StatusGuardSeconds = 15f;

    /// <summary>
    /// loading 兜底解锁时间（秒）。
    /// 协程内部抛出的异常无法被 try/catch 捕获（C# 不允许在 try 里 yield return），
    /// 一旦协程中途死掉，回调不会触发，loading 就永远是 true，
    /// Refresh 开头的 if (loading) return 会把之后所有点击都吞掉 —— 表现为「不能刷新」。
    /// 超过这个时间强制解锁，宁可重复请求一次，也不能让界面变成死的。
    /// </summary>
    const float LoadingGuardSeconds = 15f;

    void Awake()
    {
        EnsureApiClient();
        EnsureLinkage();

        // 运行时绑定按钮：编辑期 AddListener 是非持久化监听器，不写进场景，Play 就丢。
        Bind(openButton, Open);
        Bind(refreshButton, Refresh);
        Bind(deleteButton, OnDeleteClicked);
        Bind(closeButton, Close);

        Bind(statusOpenButton, () => ChangeStatus("open"));
        Bind(statusProgressButton, () => ChangeStatus("in_progress"));
        Bind(statusResolvedButton, () => ChangeStatus("resolved"));

        // 筛选：按钮是「点了才筛」，下拉是「一改就筛」，输入框是「边打边筛」
        Bind(searchButton, () => ApplyFilter(true));
        Bind(resetFilterButton, () => ResetFilter(true));

        Bind(reportModeButton, EnterReportMode);

        if (searchInput != null)
        {
            searchInput.onValueChanged.RemoveAllListeners();
            searchInput.onValueChanged.AddListener(_ => ApplyFilter(false));

            // 回车也算一次搜索。onSubmit 只在按回车/失去焦点时触发，和上面不冲突。
            searchInput.onSubmit.RemoveAllListeners();
            searchInput.onSubmit.AddListener(_ => ApplyFilter(true));
        }

        if (deviceDropdown != null)
        {
            deviceDropdown.onValueChanged.RemoveAllListeners();
            deviceDropdown.onValueChanged.AddListener(_ => ApplyFilter(false));
        }

        if (priorityDropdown != null)
        {
            priorityDropdown.onValueChanged.RemoveAllListeners();
            priorityDropdown.onValueChanged.AddListener(_ => ApplyFilter(false));
        }

        if (statusDropdown != null)
        {
            statusDropdown.onValueChanged.RemoveAllListeners();
            statusDropdown.onValueChanged.AddListener(_ => ApplyFilter(false));
        }

        InitStaticDropdowns();

        // 还没有数据就没有选中项，先把三个状态按钮置灰，
        // 免得面板刚打开、列表还没加载完时用户点下去只得到一句报错。
        ApplyStatusButtonsState();

        // 横幅默认隐藏：只有进入上报模式才显示
        SetBanner("", false);

        if (panelRoot != null) panelRoot.SetActive(false);
    }

    /// <summary>
    /// 开局自动拉一次记录：三维里的问题标记来自后端数据，
    /// 不先拉的话，用户进场景看到的是一片"干净的"设备，得先点开面板才冒出标记。
    /// 延迟 0.8 秒是为了让 apiClient 的本地队列重发先跑完，避免同一时刻两个请求撞在一起。
    /// </summary>
    void Start()
    {
        Invoke(nameof(Refresh), 0.8f);
    }

    /// <summary>
    /// 补齐三维联动需要的引用。这些字段在旧场景里是空的（面板是后加的），
    /// 所以一律「留空就自己找」，避免因为忘了拖引用就整条链路失效。
    /// </summary>
    void EnsureLinkage()
    {
        if (viewCamera == null) viewCamera = Camera.main;

        if (raycaster == null)
        {
            if (viewCamera != null) raycaster = viewCamera.GetComponent<InspectionRaycaster>();
            if (raycaster == null) raycaster = FindObjectOfType<InspectionRaycaster>(true);
        }

        // 反向接线：射线脚本点设备时要回调本组件
        if (raycaster != null && raycaster.recordList == null)
            raycaster.recordList = this;

        markerLayer = IssueMarkerLayer.Ensure();
    }

    /// <summary>
    /// 优先级 / 状态这两个下拉的选项是固定的（设备那个要等数据回来才知道有哪些），
    /// 在这里按 PriorityOptions / StatusOptions 建好，索引就是筛选值的下标。
    /// </summary>
    void InitStaticDropdowns()
    {
        suppressFilterEvents = true;

        if (priorityDropdown != null)
        {
            priorityDropdown.ClearOptions();
            priorityDropdown.AddOptions(new List<string>(PriorityOptions));
            priorityDropdown.value = 0;
            priorityDropdown.RefreshShownValue();
        }

        if (statusDropdown != null)
        {
            statusDropdown.ClearOptions();
            statusDropdown.AddOptions(new List<string>(StatusOptions));
            statusDropdown.value = 0;
            statusDropdown.RefreshShownValue();
        }

        suppressFilterEvents = false;
    }

    void Bind(Button button, Action action)
    {
        if (button == null || action == null) return;

        button.onClick.RemoveAllListeners();

        // Button.onClick.AddListener 要的是 UnityEngine.Events.UnityAction，
        // 它和 System.Action 是两个不同的委托类型，C# 不会隐式转换（CS1503），
        // 必须用 lambda 包一层。
        button.onClick.AddListener(() => action());
    }

    void EnsureApiClient()
    {
        if (apiClient != null) return;

        apiClient = FindObjectOfType<InspectionApiClient>();

        if (apiClient == null)
        {
            GameObject services = new GameObject("InspectionServices");
            apiClient = services.AddComponent<InspectionApiClient>();
            Debug.LogWarning("[巡检] 场景里没有 InspectionApiClient，已运行时创建 InspectionServices。");
        }
    }

    // ---------------- 打开 / 关闭 / 刷新 ----------------

    public void Open()
    {
        // 从上报模式点「巡检记录」回来：先退出上报模式，
        // 否则面板虽然打开了，下一次点场景还是会偷偷放标记。
        if (raycaster != null && raycaster.reportMode) ExitReportMode();

        if (panelRoot != null) panelRoot.SetActive(true);
        Refresh();
    }

    public void Close()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
    }

    /// <summary>点「刷新」按钮走这里：回到第一条。</summary>
    public void Refresh()
    {
        RefreshInternal(null);
    }

    /// <summary>
    /// selectId 不为空时，拉完列表按这个编号重新选中 —— 改完状态刷新时用，
    /// 否则会跳回第一条，用户刚操作的那条就找不到了。
    /// </summary>
    void RefreshInternal(string selectId)
    {
        // 先看是不是上一次请求卡死了（协程被打断，回调没回来），卡死就强制解锁。
        if (loading && Time.unscaledTime - loadingStartedAt > LoadingGuardSeconds)
        {
            Debug.LogWarning("[巡检] 上一次加载超过 " + LoadingGuardSeconds +
                             " 秒仍未回调，判定协程已中断，强制解除 loading 锁。");
            loading = false;
        }

        if (loading) return;

        EnsureApiClient();

        if (apiClient == null)
        {
            ShowError("找不到后端通信组件，无法加载记录");
            return;
        }

        loading = true;
        loadingStartedAt = Time.unscaledTime;
        SetStatus("正在加载…", StatusInfo);
        SetEmpty("");

        apiClient.GetIssues((success, message, list) =>
        {
            loading = false;

            if (!success)
            {
                ShowError(message);
                return;
            }

            items.Clear();
            if (list != null) items.AddRange(list);

            string wanted = !string.IsNullOrEmpty(selectId) ? selectId : selectedId;
            selected = FindById(wanted);
            if (selected == null && items.Count > 0) selected = items[0];
            selectedId = selected != null ? selected.id : null;

            // 设备下拉靠数据生成，所以必须在数据回来之后重建。
            // 放在 ApplyFilter 之前，否则下拉里还是上一次的选项。
            RebuildDeviceOptions();

            // 三维标记跟着后端数据走：记录有就建、状态变了就改色、记录没了就删。
            // 必须用 items（全量）而不是 visible（筛选后），否则一筛选就会把别的标记清掉。
            if (markerLayer != null) markerLayer.Sync(items);

            // 用户点设备时数据还没加载过：现在数据到了，接着把设备筛选应用上
            // （ApplyDeviceSelection 内部会自己 ApplyFilter 并写状态栏，这里直接返回）
            if (!string.IsNullOrEmpty(pendingDeviceId))
            {
                ApplyDeviceSelection(true);
                return;
            }

            SetStatus(message, StatusOk);
            ApplyFilter(false);
        });
    }

    /// <summary>按编号找记录；编号为空或找不到就返回 null。</summary>
    IssueDto FindById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null && items[i].id == id) return items[i];
        }

        return null;
    }

    void ShowError(string message)
    {
        SetStatus(message, StatusError);
        SetEmpty("加载失败\n" + message + "\n\n请确认 Go 后端已启动（http://127.0.0.1:8080），然后点击刷新重试。");

        if (countText != null) countText.text = "";

        Debug.LogError("[InspectionRecordList] " + message);
    }

    // ---------------- 筛选（任务四） ----------------
    //
    //  数据来源只有一处（items），筛选只产出 visible。
    //  不管用户怎么筛，items 都不会少一条，所以「重置筛选」一定能回到完整列表。

    /// <summary>
    /// 按当前筛选条件重算 visible 并重画列表。
    /// </summary>
    /// <param name="fromUser">是不是用户主动点的（搜索按钮 / 重置 / 回车）。只影响是否打日志。</param>
    public void ApplyFilter(bool fromUser)
    {
        if (suppressFilterEvents) return;

        string keyword = KeywordValue();
        string device = DeviceFilterValue();
        string priority = PriorityFilterValue();
        string status = StatusFilterValue();

        visible.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            if (Matches(items[i], keyword, device, priority, status))
                visible.Add(items[i]);
        }

        // 选中的那条被筛掉了就换第一条 —— 否则详情区显示一条列表里根本看不见的记录，
        // 用户会以为界面坏了。列表空了就没有选中项。
        if (!IsVisible(selected))
        {
            selected = visible.Count > 0 ? visible[0] : null;
            selectedId = selected != null ? selected.id : null;
        }

        if (fromUser || visible.Count != items.Count)
        {
            Debug.Log("[巡检] 筛选：关键词「" + keyword + "」设备「" + device +
                      "」优先级「" + priority + "」状态「" + status +
                      "」→ 命中 " + visible.Count + " / " + items.Count + " 条");
        }

        Render();
    }

    /// <summary>清空全部筛选条件，回到完整列表。</summary>
    public void ResetFilter(bool fromUser)
    {
        suppressFilterEvents = true;

        // 用 SetTextWithoutNotify / 直接改 value：
        // 普通赋值会触发 onValueChanged，那会再调一次 ApplyFilter，没必要，
        // 而且 TMP_InputField.text 的 setter 在 Play 模式下是会发事件的。
        if (searchInput != null) searchInput.SetTextWithoutNotify("");
        if (deviceDropdown != null) { deviceDropdown.value = 0; deviceDropdown.RefreshShownValue(); }
        if (priorityDropdown != null) { priorityDropdown.value = 0; priorityDropdown.RefreshShownValue(); }
        if (statusDropdown != null) { statusDropdown.value = 0; statusDropdown.RefreshShownValue(); }

        suppressFilterEvents = false;

        if (fromUser) SetStatus("已重置筛选条件", StatusInfo);

        ApplyFilter(fromUser);
    }

    /// <summary>一条记录是否满足当前全部筛选条件。</summary>
    static bool Matches(IssueDto issue, string keyword, string device, string priority, string status)
    {
        if (issue == null) return false;

        // 优先级：统一过 NormalizePriority，后端写法不标准（"高"/"High"）也能对上
        if (!string.IsNullOrEmpty(priority))
        {
            if (InspectionApiClient.NormalizePriority(issue.priority) != priority) return false;
        }

        // 状态：同理走 NormalizeStatus。后端只认 open / in_progress / resolved，
        // 出现别的取值时归一结果是 null，那就只会出现在「全部状态」里，不会被错分到某一档。
        if (!string.IsNullOrEmpty(status))
        {
            if (InspectionApiClient.NormalizeStatus(issue.status) != status) return false;
        }

        if (!string.IsNullOrEmpty(device))
        {
            // 按**设备编号**筛，不按显示名。
            // 设备改名后记录不会因此"掉队"——编号（PUMP-A）才是稳定的关联依据。
            string id = (issue.targetId ?? "").Trim();

            if (device == UNKNOWN_DEVICE_ID)
            {
                if (id.Length > 0) return false;
            }
            else if (!string.Equals(id, device, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            // 四个字段任意一个命中就算命中
            if (Contains(issue.targetName, keyword)
                || Contains(issue.targetId, keyword)
                || Contains(issue.title, keyword)
                || Contains(issue.description, keyword))
            {
                return true;
            }

            return false;
        }

        return true;
    }

    /// <summary>大小写无关的包含判断。targetId 是英文编号（PUMP-A），用户可能小写输入。</summary>
    static bool Contains(string source, string keyword)
    {
        if (string.IsNullOrEmpty(source)) return false;
        return source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>选中的记录还在显示集合里吗（引用比较，不用下标）。</summary>
    bool IsVisible(IssueDto issue)
    {
        if (issue == null) return false;

        for (int i = 0; i < visible.Count; i++)
        {
            if (ReferenceEquals(visible[i], issue)) return true;
        }

        return false;
    }

    /// <summary>输入框里的关键词，已 Trim。没接输入框就当不筛。</summary>
    string KeywordValue()
    {
        if (searchInput == null) return "";
        return (searchInput.text ?? "").Trim();
    }

    /// <summary>设备下拉当前选中的设备编号；「全部设备」返回空串。</summary>
    string DeviceFilterValue()
    {
        if (deviceDropdown == null) return "";

        int index = deviceDropdown.value;
        if (index <= 0 || index >= deviceIds.Count) return "";

        return deviceIds[index] ?? "";
    }

    /// <summary>优先级下拉当前选中的后端值（"" / high / medium / low）。</summary>
    string PriorityFilterValue()
    {
        int index = DropdownIndex(priorityDropdown);
        if (index < 0 || index >= PriorityFilter.Length) return "";
        return PriorityFilter[index];
    }

    /// <summary>状态下拉当前选中的后端值（"" / open / in_progress / resolved）。</summary>
    string StatusFilterValue()
    {
        int index = DropdownIndex(statusDropdown);
        if (index < 0 || index >= StatusFilter.Length) return "";
        return StatusFilter[index];
    }

    static int DropdownIndex(TMP_Dropdown dropdown)
    {
        if (dropdown == null) return 0;

        int index = dropdown.value;

        // 下拉选项数量比映射表少时（比如旧场景的下拉只有两项）不要越界
        if (dropdown.options != null && index >= dropdown.options.Count) index = 0;

        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// 按当前数据重建设备下拉：选项从记录里**按设备编号**去重后动态生成，
    /// 以后加了新设备不用改代码，刷新一次列表就会出现在下拉里。
    ///
    /// 为什么按编号去重而不是按名字：名字是给人看的、会改；编号是给程序用的、不会改。
    /// 同一台设备如果历史上改过名，按名字会裂成两项，按编号仍是一项（显示最新的名字）。
    /// </summary>
    void RebuildDeviceOptions()
    {
        if (deviceDropdown == null) return;

        suppressFilterEvents = true;

        // 记住用户当前选的设备编号，重建完还选回去（刷新列表不该把筛选条件弄丢）
        string previous = DeviceFilterValue();

        List<string> ids = new List<string>();                          // 出现顺序
        Dictionary<string, string> labelOf = new Dictionary<string, string>();
        Dictionary<string, int> labelCount = new Dictionary<string, int>();

        for (int i = 0; i < items.Count; i++)
        {
            IssueDto issue = items[i];
            if (issue == null) continue;

            string id = (issue.targetId ?? "").Trim();
            string name = (issue.targetName ?? "").Trim();

            string key = id.Length > 0 ? id : UNKNOWN_DEVICE_ID;

            if (!labelOf.ContainsKey(key))
            {
                ids.Add(key);
                labelOf[key] = name;
            }
            else if (labelOf[key].Length == 0 && name.Length > 0)
            {
                // 同编号的记录里只要有一条带名字，就用它当标签
                labelOf[key] = name;
            }
        }

        // 同名不同编号时（两个"控制柜 A"），标签后面补编号，否则下拉里两项一模一样
        foreach (KeyValuePair<string, string> kv in labelOf)
        {
            string label = kv.Value.Length > 0 ? kv.Value : (kv.Key == UNKNOWN_DEVICE_ID ? "未知设备" : kv.Key);
            labelCount[label] = labelCount.ContainsKey(label) ? labelCount[label] + 1 : 1;
        }

        List<string> labels = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            string key = ids[i];
            string label = labelOf[key].Length > 0 ? labelOf[key] : (key == UNKNOWN_DEVICE_ID ? "未知设备" : key);

            if (labelCount[label] > 1 && key != UNKNOWN_DEVICE_ID)
                label = label + "（" + key + "）";

            labels.Add(label);
        }

        // 按标签排序，顺带把"未知设备"沉到最后
        List<int> order = new List<int>();
        for (int i = 0; i < ids.Count; i++) order.Add(i);

        order.Sort((a, b) =>
        {
            bool ua = ids[a] == UNKNOWN_DEVICE_ID;
            bool ub = ids[b] == UNKNOWN_DEVICE_ID;
            if (ua != ub) return ua ? 1 : -1;
            return string.Compare(labels[a], labels[b], StringComparison.Ordinal);
        });

        List<string> options = new List<string> { ALL_DEVICES };
        List<string> idsOrdered = new List<string> { "" };

        for (int i = 0; i < order.Count; i++)
        {
            int src = order[i];
            options.Add(labels[src]);
            idsOrdered.Add(ids[src]);
        }

        deviceIds.Clear();
        deviceIds.AddRange(idsOrdered);

        deviceDropdown.ClearOptions();
        deviceDropdown.AddOptions(options);

        int restore = deviceIds.IndexOf(previous);
        if (restore < 0) restore = 0;

        deviceDropdown.value = restore;
        deviceDropdown.RefreshShownValue();

        suppressFilterEvents = false;

        Debug.Log("[巡检] 设备下拉已重建：" + (options.Count - 1) + " 个设备（" +
                  string.Join("、", options.GetRange(1, options.Count - 1).ToArray()) + "）");
    }

    // ---------------- 三维设备 → 记录（任务五 操作二 / 操作三） ----------------
    //
    //  关联键是 targetId，不是显示名。一台设备有多条问题时全部列出，不是只给最新一条。

    /// <summary>等着应用(或数据还没到、等刷新回来再应用)的设备编号。</summary>
    string pendingDeviceId;
    string pendingDeviceName;

    /// <summary>
    /// 操作二：点三维设备后调用。只留下这台设备的记录并打开面板。
    /// 数据还没加载过就先拉一次，等回调里再应用（否则下拉里根本没有这台设备）。
    /// </summary>
    public void ShowDeviceRecords(string targetId, string displayName)
    {
        pendingDeviceId = (targetId ?? "").Trim();
        pendingDeviceName = string.IsNullOrWhiteSpace(displayName) ? pendingDeviceId : displayName.Trim();

        // 点设备时可能还在上报模式里，先退出来，否则下一次点击又会放标记
        if (raycaster != null && raycaster.reportMode) ExitReportMode();

        if (panelRoot != null) panelRoot.SetActive(true);

        if (items.Count == 0)
        {
            SetStatus("正在加载「" + pendingDeviceName + "」的记录…", StatusInfo);
            Refresh();
            return;
        }

        ApplyDeviceSelection(true);
    }

    /// <summary>把筛选条件切成「某台设备」，并刷新列表。</summary>
    void ApplyDeviceSelection(bool focus)
    {
        string id = pendingDeviceId ?? "";
        string name = string.IsNullOrEmpty(pendingDeviceName) ? id : pendingDeviceName;

        suppressFilterEvents = true;

        // 只看这台设备，其他条件都清掉（用户点设备的意图就是"看这台设备的全部问题"）
        if (searchInput != null) searchInput.SetTextWithoutNotify("");
        if (priorityDropdown != null) { priorityDropdown.value = 0; priorityDropdown.RefreshShownValue(); }
        if (statusDropdown != null) { statusDropdown.value = 0; statusDropdown.RefreshShownValue(); }

        int index = string.IsNullOrEmpty(id) ? -1 : deviceIds.IndexOf(id);

        if (deviceDropdown != null && deviceIds.Count > 0)
        {
            deviceDropdown.value = index > 0 ? index : 0;
            deviceDropdown.RefreshShownValue();
        }

        suppressFilterEvents = false;

        pendingDeviceId = null;
        pendingDeviceName = null;

        if (index <= 0 && !string.IsNullOrEmpty(id))
        {
            // 这台设备一条记录都没有：不要退回"显示全部"（那会让人以为它有记录）
            visible.Clear();
            ClearRows();
            UpdateCountText();

            SetEmpty("「" + name + "」暂无巡检记录\n\n点击「＋ 新增问题」，再点这台设备即可上报。");
            ShowDetail(null);
            SetStatus(name + " 暂无巡检记录", StatusInfo);
        }
        else
        {
            ApplyFilter(true);

            int count = visible.Count;
            SetStatus(name + " 共 " + count + " 条巡检记录", count > 0 ? StatusOk : StatusInfo);
        }

        if (!focus) return;

        // 飞过去看这台设备，并把这台设备的所有标记一起闪一下
        Vector3 pos;
        if (TryGetDevicePosition(id, out pos)) FocusCamera(pos, 3.8f);

        if (markerLayer != null)
        {
            int highlighted = markerLayer.HighlightByTarget(id);
            Debug.Log("[巡检] 设备「" + name + "」（" + id + "）共 " + visible.Count +
                      " 条记录，" + highlighted + " 个标记已高亮。");
        }
    }

    /// <summary>
    /// 上报模式：进入后单击设备才会放标记 + 弹表单。
    /// 面板要先收起来，否则它会盖住大半个场景，点不到设备。
    /// </summary>
    public void EnterReportMode()
    {
        if (raycaster == null) EnsureLinkage();

        if (raycaster == null)
        {
            SetStatus("找不到射线检测组件，无法进入上报模式", StatusError);
            return;
        }

        raycaster.reportMode = true;
        raycaster.IsFormOpen = false;

        if (markerLayer != null) markerLayer.ClearHighlight();
        if (panelRoot != null) panelRoot.SetActive(false);

        SetBanner("上报模式：点击设备放置问题标记　（按 Esc 退出）", true);

        Debug.Log("[巡检] 已进入上报模式：单击设备将放置问题标记。");
    }

    public void ExitReportMode()
    {
        if (raycaster != null) raycaster.reportMode = false;

        SetBanner("", false);

        Debug.Log("[巡检] 已退出上报模式（单击设备恢复为查看记录）。");
    }

    /// <summary>提交成功后由射线脚本回调：退出上报模式、刷新列表，让新标记按数据出现。</summary>
    public void OnIssueSubmitted(string recordId)
    {
        ExitReportMode();

        Debug.Log("[巡检] 上报完成，正在刷新列表与三维标记（新记录 " + recordId + "）。");

        // 传 recordId：刷新后仍然选中刚提交的这条，标记也会被高亮
        RefreshInternal(recordId);
    }

    void SetBanner(string text, bool visible)
    {
        if (modeBanner == null) return;

        modeBanner.text = text ?? "";

        // 只关文字的话，橙色底条会留在屏幕上 —— 必须连同父物体（ReportModeBanner）一起关。
        // 结构由 RecordPanelBuilder.BuildModeBanner 创建：Banner(Image) → Label(TMP_Text)。
        Transform parent = modeBanner.transform.parent;
        GameObject root = parent != null ? parent.gameObject : modeBanner.gameObject;

        root.SetActive(visible);
    }

    // ---------------- 渲染 ----------------

    void Render()
    {
        ClearRows();
        UpdateCountText();

        // 列表显示几条，场景就亮几个球。
        // Sync 是按全量建的（保证重开 Play / 历史记录都在），这里再按 visible 关掉没筛中的，
        // 否则会出现「列表只有 2 条、墙上却有 5 个球」的错觉 —— 那些球是别的记录，不是重复生成。
        if (markerLayer != null) markerLayer.ApplyVisibility(visible);

        if (visible.Count == 0)
        {
            // 两种情况要分开提示：一条数据都没有，和有数据但被筛没了 —— 后者用户还能救回来。
            bool noDataAtAll = items.Count == 0;

            SetEmpty(noDataAtAll
                ? "暂无巡检记录\n\n在场景中点击设备即可上报一条问题。"
                : "没有符合条件的巡检记录\n\n试试放宽筛选条件，或点击「重置筛选」。");

            ShowDetail(null);
            return;
        }

        SetEmpty("");

        for (int i = 0; i < visible.Count; i++)
        {
            GameObject row = CreateRow(visible[i], i);
            if (row == null) continue;

            rows.Add(row);
            rowIssues.Add(visible[i]);
        }

        // 留一条能直接对账的日志：数据几条、筛出几条、行建了几条、挂在哪、用的哪个模板。
        // 出现「数据加载成功但列表空白」时，先看这行就知道是没建行还是建了看不见。
        Debug.Log("[巡检] 已渲染 " + rows.Count + " / " + visible.Count + " 行列表项" +
                  "（总数 " + items.Count + "，父物体：" + (listContent != null ? listContent.name : "null") +
                  "，模板：" + (itemTemplate != null ? itemTemplate.name : "null") + "）");

        if (rows.Count < visible.Count)
        {
            Debug.LogWarning("[巡检] 有 " + (visible.Count - rows.Count) +
                             " 行没能创建 —— itemTemplate 或 listContent 为空引用，请重跑 Tools ▸ 巡检 ▸ 创建巡检记录面板 RecordPanel");
        }

        HighlightSelected();
        ShowDetail(selected);
    }

    /// <summary>「共找到 N 条记录」；筛选生效时显示成「共找到 N / M 条记录」，让人知道被筛掉了多少。</summary>
    void UpdateCountText()
    {
        if (countText == null) return;

        bool narrowed = visible.Count != items.Count;

        countText.text = narrowed
            ? "共找到 " + visible.Count + " / " + items.Count + " 条记录"
            : "共找到 " + visible.Count + " 条记录";

        // 一条都筛不出来时给个红字，比默默变 0 更容易被发现
        countText.color = (narrowed && visible.Count == 0) ? StatusError : ColorMuted;
    }

    GameObject CreateRow(IssueDto issue, int index)
    {
        if (itemTemplate == null || listContent == null) return null;

        GameObject row = Instantiate(itemTemplate, listContent);
        row.name = "RecordItem_" + index;
        row.SetActive(true);

        TMP_Text label = row.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.text = issue.SafeTitle + "\n"
                + issue.SafeTargetName + " · " + PriorityToChinese(issue.priority)
                + " · " + StatusToChinese(issue.status)
                + " · " + FormatTime(issue.createdAt);
        }

        Button button = row.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => Select(issue));
        }

        return row;
    }

    void ClearRows()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == null) continue;

            // 先 SetActive(false) 再 Destroy：Destroy 只是标记删除，物体要到帧末才真消失。
            // 搜索框是「边打边筛」，每敲一个字都会走到这里重画；不先隐藏的话，
            // 新旧两批行会在同一帧里同时参与 VerticalLayoutGroup 排版，
            // 表现成列表高度忽长忽短、内容跳一下。
            rows[i].SetActive(false);
            Destroy(rows[i]);
        }

        rows.Clear();
        rowIssues.Clear();
    }

    /// <summary>选中某条：右侧详情区跟着切换。用引用定位，筛选后下标变了也不会选错。</summary>
    public void Select(IssueDto issue)
    {
        if (issue == null) return;

        // 换了一条记录，上一次的删除确认就作废 —— 否则会出现
        // 「给 A 上膛、点了 B、结果把 B 删了」。
        if (deleteArmed && (selected == null || selected.id != issue.id)) DisarmDelete();

        selected = issue;
        selectedId = issue.id;

        HighlightSelected();
        ShowDetail(issue);

        // 操作一：右侧出详情的同时，三维里飞过去 + 标记高亮
        LocateIssue(issue);
    }

    /// <summary>
    /// 按记录 id 选中（点三维标记时用）：面板关着就先打开，然后走和「列表点一条」完全相同的路径。
    /// 返回 false 表示这条记录在列表里已经不存在（多半是后端删了、场景还没同步），
    /// 这种情况必须给提示，不能静默 —— 否则表现为「点了标记没反应」。
    /// </summary>
    public bool SelectById(string recordId)
    {
        if (string.IsNullOrEmpty(recordId)) return false;

        IssueDto issue = null;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null && items[i].id == recordId)
            {
                issue = items[i];
                break;
            }
        }

        if (issue == null)
        {
            SetStatus("这个标记对应的记录不在列表里了（可能已被删除），点「刷新列表」再试", StatusError);
            return false;
        }

        if (panelRoot != null && !panelRoot.activeSelf) Open();

        Select(issue);
        return true;
    }

    /// <summary>
    /// 操作一：把相机送到这条记录的位置，并高亮它的标记。
    ///
    /// 定位优先级：标记位置 &gt; 设备位置 &gt; 记录坐标。
    /// 用标记位置是最准的（问题就发生在那个点，同一台设备的不同问题能区分开）；
    /// 没有标记（坐标缺失）才退回设备中心。
    /// </summary>
    public void LocateIssue(IssueDto issue)
    {
        if (issue == null) return;

        Vector3 pos;
        string where;
        float distance;

        IssueMarkerDot dot = null;

        if (markerLayer != null && markerLayer.TryGetMarker(issue.id, out dot) && dot != null)
        {
            pos = dot.transform.position;
            where = "问题标记";
            distance = 2.6f;
        }
        else if (TryGetDevicePosition(issue.targetId, out pos))
        {
            where = "设备";
            distance = 3.4f;
        }
        else if (TryRecordPosition(issue, out pos))
        {
            where = "记录坐标";
            distance = 3.4f;
        }
        else
        {
            SetStatus("这条记录没有可用于定位的坐标", StatusError);
            return;
        }

        FocusCamera(pos, distance);

        bool highlighted = markerLayer != null && markerLayer.Highlight(issue.id);

        SetStatus("已定位到「" + issue.SafeTargetName + "」的问题：" + issue.SafeTitle +
                  (highlighted ? "（标记已高亮）" : "（该记录没有三维标记，已按" + where + "定位）"),
                  highlighted ? StatusOk : StatusInfo);
    }

    /// <summary>按设备编号找场景里的设备，返回它的世界坐标（抬高一点，对准机身而不是脚底）。</summary>
    bool TryGetDevicePosition(string targetId, out Vector3 pos)
    {
        pos = Vector3.zero;

        string id = (targetId ?? "").Trim();
        if (id.Length == 0) return false;

        InspectionTarget[] all = FindObjectsOfType<InspectionTarget>(true);

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;

            InspectionTarget owner = all[i].GetOwner();
            if (owner == null) continue;

            if (string.Equals(owner.TargetId, id, StringComparison.Ordinal))
            {
                pos = owner.transform.position + Vector3.up * 0.45f;
                return true;
            }
        }

        return false;
    }

    /// <summary>记录自带的坐标（后端 position）。全 0 视为没有坐标。</summary>
    static bool TryRecordPosition(IssueDto issue, out Vector3 pos)
    {
        pos = Vector3.zero;

        if (issue == null || issue.position == null) return false;

        float x = issue.position.x, y = issue.position.y, z = issue.position.z;

        if (Mathf.Abs(x) < 0.001f && Mathf.Abs(y) < 0.001f && Mathf.Abs(z) < 0.001f) return false;

        pos = new Vector3(x, y, z);
        return true;
    }

    void FocusCamera(Vector3 worldPos, float distance)
    {
        Camera cam = viewCamera != null ? viewCamera : Camera.main;
        if (cam == null) return;

        InspectionCamera controller = cam.GetComponent<InspectionCamera>();

        if (controller == null)
        {
            Debug.LogWarning("[巡检] 相机上没有 InspectionCamera，无法做聚焦飞行。");
            return;
        }

        // 面板开着的时候把目标送到右侧空白区，否则飞到哪都被面板挡住
        bool panelOpen = panelRoot != null && panelRoot.activeSelf;
        controller.FocusOn(worldPos, distance, panelOpen ? focusScreenOffset : 0f);
    }

    /// <summary>选中行换底色。按引用比对，不依赖行在列表里的位置。</summary>
    void HighlightSelected()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == null) continue;

            Image bg = rows[i].GetComponent<Image>();
            if (bg == null) continue;

            bool isSelected = i < rowIssues.Count && ReferenceEquals(rowIssues[i], selected);
            bg.color = isSelected ? ColorSelected : ColorNormal;
        }
    }

    void ShowDetail(IssueDto issue)
    {
        if (issue == null)
        {
            if (detailTitle != null) detailTitle.text = "";
            if (detailMeta != null) detailMeta.text = items.Count == 0 ? "" : "请选择一条记录";
            if (detailDesc != null) detailDesc.text = "";

            ApplyStatusButtonsState();
            return;
        }

        if (detailTitle != null)
            detailTitle.text = issue.SafeTitle;

        if (detailMeta != null)
        {
            string pos = issue.position != null
                ? string.Format("({0:F2}, {1:F2}, {2:F2})",
                    issue.position.x, issue.position.y, issue.position.z)
                : "(无坐标)";

            detailMeta.text =
                "设备名称：" + issue.SafeTargetName + "\n" +
                "设备编号：" + (string.IsNullOrWhiteSpace(issue.targetId) ? "—" : issue.targetId) + "\n" +
                "所属区域：" + issue.SafeZone + "\n" +
                "优先级：" + PriorityToChinese(issue.priority) + "\n" +
                "状态：" + StatusToChinese(issue.status) + "\n" +
                "位置：" + pos + "\n" +
                "创建时间：" + FormatTime(issue.createdAt) + "\n" +
                "更新时间：" + FormatTime(issue.updatedAt) + "\n" +
                // 显示记录 id：用来核对「三维标记记住的是不是这一条」。
                // 后端 id 是时间戳串（20261028162845.586277000），太长，只留后 10 位。
                "记录编号：" + ShortId(issue.id);
        }

        if (detailDesc != null)
            detailDesc.text = string.IsNullOrWhiteSpace(issue.description)
                ? "（无描述）"
                : issue.description;

        ApplyStatusButtonsState();
    }

    // ---------------- 状态操作 ----------------
    //
    //  按钮点下去 → PATCH /api/issues/{id}/status → 后端返回 200 → **重新拉一次列表**。
    //  最后这步「重新拉」不能省：只有后端返回的数据真的变了，才说明状态管理是真的生效，
    //  而不是界面本地假装改了一下、刷新后又打回原形。

    /// <summary>三个状态按钮共用的处理。targetStatus 传后端英文枚举：open / in_progress / resolved。</summary>
    public void ChangeStatus(string targetStatus)
    {
        if (updatingStatus && Time.unscaledTime - updatingStartedAt > StatusGuardSeconds)
        {
            Debug.LogWarning("[巡检] 上一次状态更新超过 " + StatusGuardSeconds +
                             " 秒仍未回调，判定协程已中断，强制解除更新锁。");
            updatingStatus = false;
        }

        if (updatingStatus)
        {
            Debug.Log("[巡检] 上一次状态更新还没回来，这次点击已忽略（防止连点重复提交）。");
            return;
        }

        if (selected == null)
        {
            SetStatus("请先在左侧选择一条记录再改状态", StatusError);
            return;
        }

        if (string.IsNullOrWhiteSpace(selected.id))
        {
            SetStatus("这条记录没有编号，无法更新状态", StatusError);
            return;
        }

        string target = InspectionApiClient.NormalizeStatus(targetStatus);
        if (target == null)
        {
            SetStatus("状态值不正确：" + targetStatus, StatusError);
            return;
        }

        // 已经是这个状态就直接提示，别白跑一趟请求。
        string current = InspectionApiClient.NormalizeStatus(selected.status);
        if (current == target)
        {
            SetStatus("这条记录已经是「" + StatusToChinese(target) + "」了", StatusInfo);
            return;
        }

        EnsureApiClient();

        if (apiClient == null)
        {
            ShowError("找不到后端通信组件，无法更新状态");
            return;
        }

        string id = selected.id;

        updatingStatus = true;
        updatingStartedAt = Time.unscaledTime;
        ApplyStatusButtonsState();
        SetStatus("正在更新状态…", StatusInfo);

        apiClient.UpdateIssueStatus(id, target, (success, message, updated) =>
        {
            updatingStatus = false;

            if (!success)
            {
                ApplyStatusButtonsState();
                SetStatus(message, StatusError);
                Debug.LogError("[InspectionRecordList] 状态更新失败：" + message);
                return;
            }

            Debug.Log("[巡检] 状态更新成功：" + id + " -> " + target +
                      "，重新从后端加载列表以确认已保存。");

            SetStatus("已更新为「" + StatusToChinese(target) + "」，正在从后端重新加载…", StatusOk);

            // 用后端回传的整条记录先顶上，避免刷新那一瞬间详情区闪回旧状态。
            if (updated != null) ShowDetail(updated);

            // 再拉一次全量列表：这一步是「后端确实存了」的唯一证据。
            // 传 id 是为了刷新后仍然选中刚操作的那条，不会跳回第一条。
            RefreshInternal(id);
        });
    }

    // ---------------- 删除记录 ----------------

    /// <summary>二次确认的超时取消：上膛后 5 秒没再点，按钮自动恢复成「删除记录」。</summary>
    void Update()
    {
        if (!deleteArmed) return;

        if (Time.unscaledTime - deleteArmedAt > DeleteArmSeconds)
        {
            DisarmDelete();
            SetStatus("已取消删除（超时未确认）", StatusInfo);
        }
    }

    //
    //  删除是不可逆操作，所以做成「点两次」：
    //  第一次点 → 按钮变成「再点一次确认」，状态栏提示要删哪条；
    //  再点一次 → 真的发 DELETE。
    //  不用弹窗是因为运行时没有现成的确认对话框（EditorUtility 只在编辑器里能用），
    //  而二次确认在演示时同样能挡住误触，5 秒内不确认会自动取消。

    void OnDeleteClicked()
    {
        if (selected == null)
        {
            SetStatus("请先在左侧选择一条记录再删除", StatusError);
            return;
        }

        if (string.IsNullOrWhiteSpace(selected.id))
        {
            SetStatus("这条记录没有编号，无法删除", StatusError);
            return;
        }

        if (!deleteArmed)
        {
            deleteArmed = true;
            deleteArmedAt = Time.unscaledTime;

            SetDeleteButtonLabel("再点一次确认");
            SetStatus("再点一次「" + DELETE_LABEL + "」确认删除：「" +
                      (string.IsNullOrEmpty(selected.title) ? selected.id : selected.title) +
                      "」（5 秒内不确认会自动取消）", StatusError);
            return;
        }

        PerformDelete();
    }

    void PerformDelete()
    {
        DisarmDelete();

        EnsureApiClient();

        if (apiClient == null)
        {
            ShowError("找不到后端通信组件，无法删除");
            return;
        }

        string id = selected.id;
        string title = selected.title;

        selected = null;
        selectedId = null;

        deleting = true;
        ApplyStatusButtonsState();
        SetStatus("正在删除…", StatusInfo);

        apiClient.DeleteIssue(id, (success, message) =>
        {
            deleting = false;

            if (!success)
            {
                SetStatus(message, StatusError);
                Debug.LogError("[InspectionRecordList] 删除失败：" + message);
                Refresh();
                return;
            }

            Debug.Log("[巡检] 删除成功：" + id + "「" + title + "」，重新从后端加载列表。");
            SetStatus(message, StatusOk);

            // 必须重新拉一次：后端没了 → Sync 发现这条不在 alive 里 → 场景里对应的球也会被移除。
            // 这一步是「标记跟着数据消失」的验证点，不能用本地删 items 代替。
            Refresh();
        });
    }

    void DisarmDelete()
    {
        deleteArmed = false;
        SetDeleteButtonLabel(DELETE_LABEL);
    }

    void SetDeleteButtonLabel(string label)
    {
        if (deleteButton == null) return;

        TMP_Text t = deleteButton.GetComponentInChildren<TMP_Text>(true);
        if (t != null) t.text = label;
    }

    /// <summary>
    /// 状态按钮的可点击状态：没选中记录、或正在更新/删除时，全部置灰。
    /// 置灰比点了才报错友好 —— 用户不会去点一个明显不可用的按钮。
    /// </summary>
    void ApplyStatusButtonsState()
    {
        bool hasSelection = selected != null && !string.IsNullOrWhiteSpace(selected.id);

        bool on = hasSelection && !updatingStatus && !deleting;

        if (statusOpenButton != null) statusOpenButton.interactable = on;
        if (statusProgressButton != null) statusProgressButton.interactable = on;
        if (statusResolvedButton != null) statusResolvedButton.interactable = on;
        if (deleteButton != null) deleteButton.interactable = hasSelection && !deleting;
    }

    // ---------------- 显示层翻译（英文枚举 -> 中文） ----------------
    // 提交时是「中文 -> 英文」，这里是反向：后端存的永远是英文枚举。

    public static string PriorityToChinese(string priority)
    {
        switch ((priority ?? "").Trim().ToLowerInvariant())
        {
            case "low": return "低";
            case "high": return "高";
            case "medium": return "中";
            default: return string.IsNullOrEmpty(priority) ? "—" : priority;
        }
    }

    public static string StatusToChinese(string status)
    {
        switch ((status ?? "").Trim().ToLowerInvariant())
        {
            case "open": return "待处理";
            case "in_progress": return "处理中";
            // 和筛选下拉、状态按钮用同一个词。后端存的还是 resolved，只是显示成中文。
            case "resolved": return "已解决";
            default: return string.IsNullOrEmpty(status) ? "—" : status;
        }
    }

    /// <summary>
    /// 时间格式化。后端返回的时区格式不统一（有 +08:00 也有 Z），
    /// 这里不做时区换算，只截取日期和时间部分，避免解析失败。
    /// </summary>
    public static string FormatTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (raw.Length < 19) return raw;

        return raw.Substring(0, 10) + " " + raw.Substring(11, 8);
    }

    /// <summary>id 太长，只留尾部用于界面显示（后端 id 是时间戳串）。</summary>
    static string ShortId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "—";
        return id.Length <= 10 ? id : id.Substring(id.Length - 10);
    }

    void SetStatus(string message, Color color)
    {
        if (statusText == null) return;
        statusText.text = message;
        statusText.color = color;
    }

    void SetEmpty(string message)
    {
        if (emptyText == null) return;

        emptyText.text = message;
        emptyText.gameObject.SetActive(!string.IsNullOrEmpty(message));
    }
}
