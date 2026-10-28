// InspectionApiClient.cs（整合版）
//
// 职责：只负责把一条巡检记录 POST 给 Go 后端 + 保证「失败不丢记录」。
// 不做 UI、不创建标记、不做业务判断。表单（IssueFormController）负责收集字段与显示结果，
// 通信层负责发请求、本地留底、自动重发。
//
// 挂载位置：场景中唯一的 InspectionServices 空物体（第13步菜单自动创建）。
// 注意：不要挂到每台设备上，也不要挂多份，否则一次提交会发多个请求，后端出现重复记录。
//
// 整合版修掉的三个坑：
//   1. 「卡在正在提交…」：SendWebRequest() 在同步阶段就可能抛异常（Unity 禁止 HTTP 明文），
//      直接 yield return 会终止整个协程，回调永远不执行。这里只把「构造 + 发起」放进 try，
//      等待改成外层轮询 isDone，并且协程内部自带超时，超时一定回调失败。
//   2. 「失败后记录丢失」：提交前先写本地待发队列（outbox），成功才删除，失败保留，
//      下次启动自动重发。文件在 Application.persistentDataPath/inspection_outbox.json。
//   3. 「重复记录」：HTTP 2xx 一律按成功处理（失败由 4xx 分支负责）。
//      判成失败会导致同一条记录被重发一次，后端就多一条 —— 宁可少提示一次成功。
//
// 与后端的契约：POST /api/issues，请求体
//   { targetId, targetName, title, description, priority, position:{x,y,z}, createdAt }
// 与后端 Issue、React 端完全同一套字段名；priority ∈ low|medium|high，title 必填。
// 注意该接口开了 DisallowUnknownFields，多传一个字段就是 400「请求 JSON 格式不正确」，
// 所以 InspectionRecordDto 加字段时必须同步改后端入参白名单。
// 早期用过 POST /api/inspections（扁平 x/y/z），已废弃，仅保留运行时纠正逻辑。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>与 Go 后端 InspectionRecord 一一对应的扁平契约（坐标是顶层 x/y/z）。</summary>
[Serializable]
public class InspectionRecordDto
{
    public string targetId;
    public string targetName;
    public string zone;       // 所属区域；留空时后端兜底为「未分区」
    public string title;
    public string description;
    public string priority;   // low | medium | high

    /// <summary>
    /// 场景坐标。用**嵌套对象**而不是扁平 x/y/z，与后端 Issue、React 端保持同一套字段名。
    /// 早期这里发的是扁平 x/y/z（走 /api/inspections），和验收要求的数据结构对不上；
    /// JsonUtility 要求嵌套类型带 [Serializable]，否则整段解析成 0。
    /// </summary>
    public IssuePositionDto position;

    public string createdAt;  // ISO 8601

    /// <summary>提交前统一整理：去空格、priority 归一化、createdAt 兜底。返回 false 表示必填项缺失。</summary>
    public bool Normalize(out string error)
    {
        targetId = (targetId ?? "").Trim();
        targetName = (targetName ?? "").Trim();
        zone = (zone ?? "").Trim();
        title = (title ?? "").Trim();
        description = (description ?? "").Trim();
        priority = (priority ?? "").Trim();

        if (string.IsNullOrEmpty(priority))
        {
            priority = "medium";
        }
        else
        {
            // 「显示值 → 上报值」的翻译只在这里做一次，别处一律用英文枚举。
            // 注意：NormalizePriority 定义在 InspectionApiClient 里（通信层），
            // 这里在 DTO 内部，属于跨类调用，必须带类名，否则 CS0103。
            string normalized = InspectionApiClient.NormalizePriority(priority);
            if (normalized == null)
            {
                // 认不出来就按 medium 上报并告警。
                // 后端对非法枚举直接返回 400，为一个非关键字段让整条记录提交失败不值得
                // （记录不会丢，会进本地队列，但现场看到的是红色失败）。
                Debug.LogWarning("[巡检] 优先级无法识别：「" + priority + "」，已按 medium 上报。");
                normalized = "medium";
            }
            priority = normalized;
        }

        if (string.IsNullOrEmpty(createdAt))
            createdAt = DateTime.UtcNow.ToString("o");

        // 坐标兜底：命中点理论上不会是 NaN，但一旦是，后端 JSON 序列化会失败、
        // 整条记录提交报错。这里归零，宁可位置不准也不能提交不了。
        if (position == null) position = new IssuePositionDto();
        if (float.IsNaN(position.x) || float.IsInfinity(position.x)) position.x = 0f;
        if (float.IsNaN(position.y) || float.IsInfinity(position.y)) position.y = 0f;
        if (float.IsNaN(position.z) || float.IsInfinity(position.z)) position.z = 0f;

        if (string.IsNullOrEmpty(targetId))
        {
            error = "设备标识为空";
            return false;
        }

        if (string.IsNullOrEmpty(title))
        {
            error = "问题标题为空";
            return false;
        }

        if (priority != "low" && priority != "medium" && priority != "high")
        {
            error = "优先级必须是 low / medium / high，当前是 " + priority;
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// 提交成功响应体（POST /api/issues）。
/// 后端回的是 {"message":"巡检问题创建成功","item":{...}} —— **id 在 item 里面，没有顶层 id**，
/// 也没有 success 字段（成功与否看 HTTP 状态码）。
/// 早期走 /api/inspections 时回包是 {success, message, targetId, id}，字段位置完全不同，
/// 换通道后必须跟着改，否则 LastCreatedId 永远是空、标记绑不上记录。
/// </summary>
[Serializable]
public class InspectionApiResponse
{
    public string message;

    /// <summary>新建成功的完整记录。null 表示响应体里没有这一项。</summary>
    public IssueDto item;

    // 兼容旧通道 /api/inspections 的回包，防止哪天又切回去时静默拿到空 id。
    public bool success;
    public string targetId;
    public string id;
}

/// <summary>
/// GET /api/issues 返回的坐标（**嵌套对象**，与提交接口的扁平 x/y/z 正好相反）。
/// JsonUtility 要求嵌套类型也必须是 [Serializable]，否则整条记录解析出来是 0。
/// </summary>
[Serializable]
public class IssuePositionDto
{
    public float x;
    public float y;
    public float z;
}

/// <summary>GET /api/issues 返回的一条巡检记录。</summary>
[Serializable]
public class IssueDto
{
    public string id;
    public string targetId;
    public string targetName;
    public string zone;                  // 所属区域（后端对空值兜底为「未分区」）
    public string title;
    public string description;
    public string priority;              // low | medium | high
    public IssuePositionDto position;    // 嵌套 {x,y,z}
    public string status;                // open | in_progress | resolved
    public string createdAt;
    public string updatedAt;

    /// <summary>设备名为空的早期脏数据兜底，界面上不能显示空白。</summary>
    public string SafeTargetName
    {
        get { return string.IsNullOrWhiteSpace(targetName) ? "未知设备" : targetName.Trim(); }
    }

    public string SafeTitle
    {
        get { return string.IsNullOrWhiteSpace(title) ? "(无标题)" : title.Trim(); }
    }

    /// <summary>区域兜底：老数据可能没有 zone，界面不能显示空白。</summary>
    public string SafeZone
    {
        get { return string.IsNullOrWhiteSpace(zone) ? "未分区" : zone.Trim(); }
    }

    /// <summary>优先级兜底：拿不到就当 medium，避免标记尺寸按 unknown 走。</summary>
    public string SafePriority
    {
        get { return string.IsNullOrWhiteSpace(priority) ? "medium" : priority.Trim().ToLowerInvariant(); }
    }
}

/// <summary>GET /api/issues 的响应包装。JsonUtility 不能直接解析顶层数组，必须包一层。</summary>
[Serializable]
public class IssueListResponse
{
    public List<IssueDto> items;
    public int total;
}

/// <summary>本地待发队列里的一条（clientId 只用于本地去重，不发给后端）。</summary>
[Serializable]
public class PendingInspection
{
    public string clientId;
    public string queuedAt;
    public int attempts;
    public InspectionRecordDto record;
}

/// <summary>待发队列文件。JsonUtility 不能直接序列化顶层 List，必须包一层。</summary>
[Serializable]
public class InspectionOutboxFile
{
    public List<PendingInspection> items = new List<PendingInspection>();
}

public class InspectionApiClient : MonoBehaviour
{
    [Header("后端地址")]
    [SerializeField]
    // 提交与查询走同一个 /api/issues（POST 提交、GET 拉列表），字段结构完全一致：
    // { targetId, targetName, title, description, priority, position:{x,y,z}, createdAt }。
    // 早期走的是 /api/inspections（扁平 x/y/z），与验收要求的数据结构不一致，已废弃。
    private string apiUrl = "http://127.0.0.1:8080/api/issues";

    [Header("网络")]
    [Tooltip("单次请求超时（秒）。超时后一定会回调失败，界面不会卡在「正在提交…」。")]
    public float timeoutSeconds = 10f;

    [Header("本地留底（失败不丢记录）")]
    [Tooltip("提交前先把记录写进本地队列，成功才删除。后端连不上时记录不会丢，下次启动自动重发。")]
    public bool enableOutbox = true;

    [Tooltip("启动时自动重发队列里未成功的记录。")]
    public bool autoRetryOnStart = true;

    [Tooltip("单次启动最多重发多少条，避免堆积太多时一直占着网络。")]
    public int maxRetryPerStart = 20;

    /// <summary>
    /// 最近一次提交成功后后端回传的记录 id（主键）。
    ///
    /// 为什么需要它：三维标记必须记住自己对应哪条记录，否则列表点某条记录时，
    /// 代码不知道场景里哪个球是它，也就没法高亮。后端 POST /api/issues 的
    /// 成功回包是 {"message":"巡检问题创建成功","item":{...}}，**id 在 item 里**，这里把它接住。
    /// </summary>
    public string LastCreatedId { get; private set; }

    /// <summary>后端地址（编辑器脚本用 SerializedObject 写入私有序列化字段）。</summary>
    public string ApiUrl
    {
        get { return apiUrl; }
        set { apiUrl = value; }
    }

    /// <summary>
    /// 把任意来源的优先级统一成后端枚举 low / medium / high。
    /// 返回 null 表示无法识别（调用方自行兜底）。
    ///
    /// 这是「界面显示」和「线上数据」之间唯一的翻译点：
    /// 下拉框给用户看的是中文（低/中/高），JSON 里必须是英文枚举，
    /// 中文直接发过去会被后端 400 拒掉（实测 priority:"高" → 400）。
    /// </summary>
    public static string NormalizePriority(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "low":
            case "低":
            case "一般":
                return "low";

            case "medium":
            case "中":
            case "重要":
            case "普通":
                return "medium";

            case "high":
            case "高":
            case "紧急":
                return "high";
        }

        return null;
    }

    /// <summary>用基础地址设置接口地址（表单的 serverUrl 同步过来时用）。</summary>
    public void SetBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        apiUrl = baseUrl.Trim().TrimEnd('/') + "/api/issues";
    }

    /// <summary>本地待发队列文件路径。</summary>
    public static string OutboxPath
    {
        get { return Path.Combine(Application.persistentDataPath, "inspection_outbox.json"); }
    }

    /// <summary>当前还有几条没发出去的记录。</summary>
    public int PendingCount
    {
        get { return LoadOutbox().items.Count; }
    }

    // 同一时刻只允许一个请求在飞：避免连点提交 / 自动重发叠加造成后端重复记录。
    private bool inflight;
    private float inflightStartedAt;

    private void Awake()
    {
        // 场景里可能存着早期的示例地址 192.168.1.100（并不存在），这里统一纠一次。
        // 注意只纠「示例地址 / 空值」，绝不把 http 改成 https ——
        // 本地 Go 后端只监听 http，改协议会直接连不上。明文被拦要走 Player Settings。
        // apiUrl 是序列化字段，场景文件里存着上一次保存的值，改代码**不会**自动生效。
        // 所以这里必须把旧通道 /api/inspections 一并纠正，否则用户不手动改 Inspector
        // 就永远还走老接口，表现是「代码改了但抓包还是扁平 x/y/z」。
        bool legacyChannel = apiUrl.EndsWith("/api/inspections");

        if (string.IsNullOrWhiteSpace(apiUrl) || apiUrl.Contains("192.168.1.100") || legacyChannel)
        {
            string host = string.IsNullOrWhiteSpace(apiUrl) || apiUrl.Contains("192.168.1.100")
                ? "http://127.0.0.1:8080"
                : apiUrl.Substring(0, apiUrl.Length - "/api/inspections".Length);

            apiUrl = host.TrimEnd('/') + "/api/issues";
            Debug.Log("[巡检] apiUrl 是旧通道/示例地址，已自动切到 " + apiUrl);
        }
    }

    private void Start()
    {
        if (!enableOutbox || !autoRetryOnStart) return;

        int pending = PendingCount;
        if (pending <= 0) return;

        Debug.Log("[巡检] 本地有 " + pending + " 条未提交的巡检记录，开始自动重发。");
        StartCoroutine(RetryPendingCoroutine());
    }

    // =====================================================================
    //  提交
    // =====================================================================

    /// <summary>
    /// 拉取巡检记录列表（GET /api/issues）。
    /// 回调参数：是否成功 / 提示信息 / 记录列表（失败时为 null）。
    /// 与 Submit 一样：无论成功失败，回调一定会被调用（超时、异常、断网都一样）。
    /// </summary>
    public void GetIssues(Action<bool, string, List<IssueDto>> callback)
    {
        StartCoroutine(GetIssuesCoroutine(callback));
    }

    /// <summary>
    /// 查询地址。提交与查询现在已经合并到同一个 /api/issues（POST 提交 / GET 拉列表），
    /// 所以直接用 apiUrl；只保留对旧通道的反推，防止场景里还存着 /api/inspections。
    /// </summary>
    string IssuesUrl
    {
        get
        {
            const string legacy = "/api/inspections";
            string url = apiUrl.EndsWith(legacy)
                ? apiUrl.Substring(0, apiUrl.Length - legacy.Length) + "/api/issues"
                : apiUrl;

            return url.TrimEnd('/');
        }
    }

    private IEnumerator GetIssuesCoroutine(Action<bool, string, List<IssueDto>> callback)
    {
        string url = IssuesUrl;
        Debug.Log("[巡检] GET " + url);

        UnityWebRequest request = null;
        string sendError = null;

        // 与提交完全相同的处理：SendWebRequest() 的同步阶段可能抛异常
        // （Unity 禁止 HTTP 明文），直接 yield return 会终止协程、回调永不触发。
        // 所以只把「构造 + 发起」放进 try，等待改成外层轮询 isDone。
        try
        {
            request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            request.SendWebRequest();
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
        }

        if (sendError != null)
        {
            if (request != null) request.Dispose();

            string hint = sendError.Contains("Insecure connection")
                ? "Unity 禁止 HTTP 明文请求。执行菜单 Tools ▸ 巡检 ▸ 允许 HTTP 明文请求（本地后端）"
                : sendError;

            Debug.LogError("[InspectionApiClient] 请求未能发出：" + hint);
            callback?.Invoke(false, "无法连接后端：" + hint, null);
            yield break;
        }

        float elapsed = 0f;
        while (!request.isDone && elapsed < timeoutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!request.isDone)
        {
            request.Abort();
            request.Dispose();
            callback?.Invoke(false, "请求超时（" + timeoutSeconds + " 秒）", null);
            yield break;
        }

        // ===== 关键：所有需要访问原生对象的信息，必须在 Dispose 之前读完 =====
        // Dispose() 之后 UnityWebRequest 的原生对象已释放，再访问 request.error /
        // request.result / request.responseCode 会抛
        //     ArgumentNullException: Value cannot be null. Parameter name: _unity_self
        // 这个异常发生在协程内部且无法被 try/catch 捕获（C# 不允许在 try 块里 yield return），
        // 协程会直接终止、回调永远不触发 —— 界面就永久停在「正在加载…」，
        // 而 loading 标志一直是 true，连点刷新也会被 Refresh 开头的 if (loading) return 吞掉，
        // 表现出来就是「不能刷新」。这是列表面板卡死的根本原因。
        bool ok = request.result == UnityWebRequest.Result.Success
                  && request.responseCode >= 200 && request.responseCode < 300;

        string text = request.downloadHandler != null ? request.downloadHandler.text : "";
        long code = request.responseCode;
        string errorText = request.error;      // ← 必须在 Dispose 之前取

        request.Dispose();

        if (!ok)
        {
            string detail = string.IsNullOrEmpty(errorText)
                ? "HTTP " + code
                : errorText + "（HTTP " + code + "）";

            Debug.LogError("[InspectionApiClient] 拉取记录失败 url=" + url + " -> " + detail);
            callback?.Invoke(false, "加载失败：" + detail, null);
            yield break;
        }

        List<IssueDto> list = new List<IssueDto>();

        try
        {
            IssueListResponse response = JsonUtility.FromJson<IssueListResponse>(text);
            if (response != null && response.items != null)
                list = response.items;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[InspectionApiClient] 列表解析失败：" + e.Message);
            callback?.Invoke(false, "数据解析失败：" + e.Message, null);
            yield break;
        }

        Debug.Log("[巡检] 已拉取 " + list.Count + " 条巡检记录。");
        callback?.Invoke(true, "已加载 " + list.Count + " 条记录", list);
    }

    // =====================================================================
    //  状态更新（任务四）
    // =====================================================================
    //
    //  与 Go 后端的契约：PATCH /api/issues/{id}/status，请求体 {"status": "..."}
    //  后端开了 DisallowUnknownFields，请求体里**只能有 status 一个字段**，多一个就 400。
    //  合法值：open | in_progress | resolved（后端也会兜底认中文，但我们一律发英文枚举）。
    //  成功返回 200 {"message":"问题状态更新成功","item":{...整条更新后的记录}}。

    /// <summary>状态更新请求体。字段名必须就叫 status，且不能多出别的字段。</summary>
    [Serializable]
    public class StatusUpdateBody
    {
        public string status;
    }

    /// <summary>状态更新响应体。后端会把更新后的整条记录放在 item 里回传。</summary>
    [Serializable]
    public class StatusUpdateResponse
    {
        public string message;
        public IssueDto item;
    }

    /// <summary>后端认可的三个状态值（英文枚举）。别处一律用英文，显示时才翻成中文。</summary>
    public static readonly string[] StatusValues = { "open", "in_progress", "resolved" };

    /// <summary>把任意来源的状态统一成后端枚举，认不出返回 null。</summary>
    public static string NormalizeStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "open":
            case "待处理":
            case "未处理":
                return "open";

            case "in_progress":
            case "inprogress":
            case "处理中":
                return "in_progress";

            case "resolved":
            case "已完成":
            case "已解决":
                return "resolved";
        }

        return null;
    }

    /// <summary>
    /// 更新某条巡检记录的状态。
    /// 回调参数：是否成功 / 提示信息 / 后端回传的整条更新后记录（失败时为 null）。
    /// 无论成功失败回调一定触发，调用方可以放心复位按钮。
    /// </summary>
    public void UpdateIssueStatus(string id, string status, Action<bool, string, IssueDto> callback)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            callback?.Invoke(false, "记录编号为空，无法更新状态", null);
            return;
        }

        string normalized = NormalizeStatus(status);
        if (normalized == null)
        {
            callback?.Invoke(false, "状态值不正确：" + status + "（只能是 open / in_progress / resolved）", null);
            return;
        }

        StartCoroutine(UpdateStatusCoroutine(id.Trim(), normalized, callback));
    }

    /// <summary>
    /// 删除一条巡检记录（DELETE /api/issues/{id}）。
    /// 后端已实现：删掉后写回文件，写文件失败会回滚内存里的记录。
    /// </summary>
    public void DeleteIssue(string id, Action<bool, string> callback)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            callback?.Invoke(false, "记录编号为空，无法删除");
            return;
        }

        StartCoroutine(DeleteIssueCoroutine(id.Trim(), callback));
    }

    private IEnumerator DeleteIssueCoroutine(string id, Action<bool, string> callback)
    {
        string url = IssuesUrl + "/" + id;

        Debug.Log("[巡检] DELETE " + url);

        UnityWebRequest request = null;
        string sendError = null;

        try
        {
            // 快捷方法 UnityWebRequest.Delete 不会自动挂 DownloadHandler，
            // 不挂就读不到响应体（downloadHandler 为 null，直接抛空引用）。
            request = UnityWebRequest.Delete(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            request.SendWebRequest();
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
        }

        if (sendError != null)
        {
            if (request != null) request.Dispose();

            Debug.LogError("[InspectionApiClient] 删除请求未能发出：" + sendError);
            callback?.Invoke(false, "无法连接后端：" + sendError);
            yield break;
        }

        float elapsed = 0f;
        while (!request.isDone && elapsed < timeoutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!request.isDone)
        {
            request.Abort();
            request.Dispose();
            callback?.Invoke(false, "删除超时（" + timeoutSeconds + " 秒）");
            yield break;
        }

        // 老规矩：responseCode / error 必须在 Dispose 之前读完
        bool ok = request.result == UnityWebRequest.Result.Success
                  && request.responseCode >= 200 && request.responseCode < 300;

        long code = request.responseCode;
        string errorText = request.error;
        string text = request.downloadHandler != null ? request.downloadHandler.text : "";

        request.Dispose();

        if (!ok)
        {
            string detail = string.IsNullOrEmpty(errorText)
                ? "HTTP " + code
                : errorText + "（HTTP " + code + "）";

            Debug.LogError("[InspectionApiClient] 删除失败 url=" + url + " -> " + detail);

            // 404 最常见的原因：这条记录在别处已经被删了（比如手动改过数据文件）
            string friendly = code == 404
                ? "后端没有找到这条记录（可能已被删除）"
                : "删除失败：" + detail;

            callback?.Invoke(false, friendly);
            yield break;
        }

        string msg = "已删除";

        try
        {
            DeleteResponse response = JsonUtility.FromJson<DeleteResponse>(text);
            if (response != null && !string.IsNullOrEmpty(response.message)) msg = response.message;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[InspectionApiClient] 删除响应解析失败：" + e.Message);
        }

        Debug.Log("[InspectionApiClient] 删除成功：" + url);
        callback?.Invoke(true, msg);
    }

    [Serializable]
    class DeleteResponse
    {
        public string message;
    }

    private IEnumerator UpdateStatusCoroutine(string id, string status,
        Action<bool, string, IssueDto> callback)
    {
        string url = IssuesUrl + "/" + id + "/status";

        // 请求体只放 status 一个字段（后端 DisallowUnknownFields 会拒掉多余字段）
        StatusUpdateBody body = new StatusUpdateBody { status = status };
        string json = JsonUtility.ToJson(body);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        Debug.Log("[巡检] PATCH " + url + " -> " + json);

        UnityWebRequest request = null;
        string sendError = null;

        try
        {
            // UnityWebRequest 没有 PATCH 的快捷方法，只能自己指定 method。
            // 两个 handler 必须手动挂：UploadHandlerRaw 发送 JSON，DownloadHandlerBuffer 接收响应。
            request = new UnityWebRequest(url, "PATCH");
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            request.SendWebRequest();
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
        }

        if (sendError != null)
        {
            if (request != null) request.Dispose();

            Debug.LogError("[InspectionApiClient] 状态更新请求未能发出：" + sendError);
            callback?.Invoke(false, "无法连接后端：" + sendError, null);
            yield break;
        }

        float elapsed = 0f;
        while (!request.isDone && elapsed < timeoutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!request.isDone)
        {
            request.Abort();
            request.Dispose();
            callback?.Invoke(false, "状态更新超时（" + timeoutSeconds + " 秒）", null);
            yield break;
        }

        // 和查询一样：所有原生对象的信息必须在 Dispose 之前读完，
        // 否则 request.error / responseCode 会抛 ArgumentNullException(_unity_self)，
        // 而协程内的异常无法被 try/catch 捕获（C# 不允许在 try 里 yield return），
        // 结果是回调永远不触发、界面永久卡在「正在更新…」。
        bool ok = request.result == UnityWebRequest.Result.Success
                  && request.responseCode >= 200 && request.responseCode < 300;

        string text = request.downloadHandler != null ? request.downloadHandler.text : "";
        long code = request.responseCode;
        string errorText = request.error;

        request.Dispose();

        if (!ok)
        {
            string detail = string.IsNullOrEmpty(errorText)
                ? "HTTP " + code
                : errorText + "（HTTP " + code + "）";

            Debug.LogError("[InspectionApiClient] 状态更新失败 url=" + url + " -> " + detail);

            // 404 单独给一句人话，排查时最常见的情况就是编号对不上。
            string friendly = code == 404
                ? "后端没有找到这条记录（可能已被删除）"
                : "状态更新失败：" + detail;

            callback?.Invoke(false, friendly, null);
            yield break;
        }

        IssueDto updated = null;
        string msg = "状态已更新";

        try
        {
            StatusUpdateResponse response = JsonUtility.FromJson<StatusUpdateResponse>(text);
            if (response != null)
            {
                updated = response.item;
                if (!string.IsNullOrEmpty(response.message)) msg = response.message;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[InspectionApiClient] 状态更新响应解析失败：" + e.Message);
        }

        // 解析不出 item 也按成功处理：状态其实已经改了，
        // 判失败会让用户以为没成功而再点一次，反而多一次请求。
        Debug.Log("[巡检] 状态更新成功：" + id + " -> " + status);
        callback?.Invoke(true, msg, updated);
    }

    /// <summary>
    /// 提交一条巡检记录。回调第一个参数是是否成功，第二个是可直接显示给用户/写进日志的说明。
    /// 无论成功失败，回调都一定会被调用（超时、异常、断网都一样），调用方可以放心复位按钮。
    /// </summary>
    public void Submit(InspectionRecordDto record, Action<bool, string> callback)
    {
        if (record == null)
        {
            callback?.Invoke(false, "巡检记录为空");
            return;
        }

        string error;
        if (!record.Normalize(out error))
        {
            callback?.Invoke(false, "记录不完整：" + error);
            return;
        }

        // 并发保护：上一条还没回来就再点提交，直接拒绝，避免后端出现重复记录。
        // 兜底清锁：万一协程被异常打断没复位，超过「超时 + 3 秒」自动解锁。
        if (inflight && Time.unscaledTime - inflightStartedAt < timeoutSeconds + 3f)
        {
            callback?.Invoke(false, "上一条记录还没提交完，请稍候再试");
            return;
        }

        inflight = true;
        inflightStartedAt = Time.unscaledTime;

        StartCoroutine(PostRecord(record, (ok, msg) =>
        {
            inflight = false;
            callback?.Invoke(ok, msg);
        }));
    }

    private IEnumerator PostRecord(InspectionRecordDto record, Action<bool, string> callback)
    {
        string json = JsonUtility.ToJson(record);
        byte[] body = Encoding.UTF8.GetBytes(json);

        Debug.Log("[巡检] POST " + apiUrl + " → " + json);

        UnityWebRequest request = null;
        string sendError = null;

        // 关键：C# 不允许在 try-catch 里写 yield，而「Unity 禁止 HTTP 明文」的
        // InvalidOperationException 恰恰是在 SendWebRequest() 的同步阶段抛出的。
        // 如果按常见写法直接 yield return request.SendWebRequest()，异常会终止整个协程，
        // 回调永远不会被调用，界面就卡死在「正在提交…」。
        // 所以这里只把「构造 + 发起」放进 try，等待改成外层轮询 isDone。
        try
        {
            request = new UnityWebRequest(apiUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            request.SendWebRequest();
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
        }

        if (sendError != null)
        {
            if (request != null) request.Dispose();

            string hint = sendError.Contains("Insecure connection")
                ? "Unity 禁止 HTTP 明文请求。执行菜单 Tools ▸ 巡检 ▸ 允许 HTTP 明文请求（本地后端）"
                : sendError;

            Debug.LogError("[InspectionApiClient] 请求未能发出：" + hint);
            callback?.Invoke(false, "请求失败：" + hint);
            yield break;
        }

        // 自己计时，不依赖 request.timeout：协程内超时一定会走到下面的失败分支，
        // 不会出现「既不成功也不失败、界面永远转圈」。
        float elapsed = 0f;
        while (!request.isDone && elapsed < timeoutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!request.isDone)
        {
            request.Abort();
            request.Dispose();
            callback?.Invoke(false, "请求超时（" + timeoutSeconds + " 秒），记录已暂存本地");
            yield break;
        }

        bool ok = request.result == UnityWebRequest.Result.Success
                  && request.responseCode >= 200 && request.responseCode < 300;

        string responseText = request.downloadHandler != null ? request.downloadHandler.text : "";

        if (!ok)
        {
            string detail = string.IsNullOrEmpty(request.error)
                ? "HTTP " + request.responseCode + " " + responseText
                : request.error + "（HTTP " + request.responseCode + "）";

            Debug.LogError("[InspectionApiClient] 提交失败 url=" + apiUrl + " -> " + detail);
            request.Dispose();
            callback?.Invoke(false, "提交失败：" + detail);
            yield break;
        }

        // HTTP 2xx 分支：区分「后端明确拒绝」和「响应体没法解析」。
        InspectionApiResponse response = null;
        bool parsed = false;
        try
        {
            response = JsonUtility.FromJson<InspectionApiResponse>(responseText);
            parsed = response != null;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[InspectionApiClient] 响应解析失败：" + e.Message);
        }

        request.Dispose();

        // 走到这里 HTTP 已经是 2xx，一律按成功处理。
        // 注意不能再写 `if (!response.success)`：POST /api/issues 的回包里**没有** success 字段，
        // JsonUtility 对缺失的 bool 一律填 false，照旧判断会把每次成功提交都打成失败，
        // 记录既留在 outbox 被重发、界面又显示红色报错 —— 数据没丢但用户以为失败了。
        // 真正的失败（标题空 / 枚举错）后端会返回 4xx，已在上面的 !ok 分支处理。

        // parsed==false（响应体为空/不是预期格式）时同样按成功处理：
        // 数据已经写进后端了，判成失败会让同一条记录被重发，后端就多一条重复记录。
        string message = (parsed && !string.IsNullOrEmpty(response.message))
            ? response.message
            : "已提交（后端未返回确认信息）";

        // 记下这次新建记录的 id：调用方（表单 → 射线脚本）要用它把三维标记绑到记录上。
        // 新通道的 id 在 item 里，旧通道在顶层 id，两个都取，谁有值用谁。
        // 解析不到就置空，让上层知道"这次没拿到 id"，而不是拿到上一次的旧 id。
        LastCreatedId = null;
        if (parsed)
        {
            if (response.item != null && !string.IsNullOrEmpty(response.item.id))
                LastCreatedId = response.item.id;
            else if (!string.IsNullOrEmpty(response.id))
                LastCreatedId = response.id;
        }

        if (!string.IsNullOrEmpty(LastCreatedId))
        {
            Debug.Log("[巡检] 后端已创建记录，id=" + LastCreatedId);
        }

        callback?.Invoke(true, message);
    }

    // =====================================================================
    //  本地待发队列（失败不丢记录）
    // =====================================================================

    /// <summary>
    /// 把记录写进本地队列，返回本地编号。
    /// 顺序很重要：先留底再发请求。后端挂了 / 网络断了，记录已经在磁盘上，
    /// 关掉 Unity 也不会丢，下次启动自动重发。
    /// </summary>
    public string QueuePending(InspectionRecordDto record)
    {
        if (!enableOutbox || record == null) return null;

        string error;
        record.Normalize(out error);

        string id = Guid.NewGuid().ToString("N").Substring(0, 12);

        try
        {
            InspectionOutboxFile file = LoadOutbox();
            file.items.Add(new PendingInspection
            {
                clientId = id,
                queuedAt = DateTime.UtcNow.ToString("o"),
                attempts = 0,
                record = record
            });
            SaveOutbox(file);
            Debug.Log("[巡检] 已本地留底（" + id + "），当前待发 " + file.items.Count + " 条。文件：" + OutboxPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[巡检] 本地留底失败（不影响本次提交）：" + e.Message);
            return null;
        }

        return id;
    }

    /// <summary>提交成功后把记录从队列里移除。</summary>
    public void RemovePending(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return;

        try
        {
            InspectionOutboxFile file = LoadOutbox();
            int before = file.items.Count;
            file.items.RemoveAll(p => p != null && p.clientId == clientId);

            if (file.items.Count != before)
            {
                SaveOutbox(file);
                Debug.Log("[巡检] 已提交成功，本地队列移除 1 条，剩余 " + file.items.Count + " 条。");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[巡检] 更新本地队列失败：" + e.Message);
        }
    }

    /// <summary>清空本地队列（编辑器菜单可调用，用户确认后使用）。</summary>
    public static void ClearPending()
    {
        try
        {
            if (File.Exists(OutboxPath)) File.Delete(OutboxPath);
            Debug.Log("[巡检] 已清空本地待发队列。");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[巡检] 清空本地队列失败：" + e.Message);
        }
    }

    /// <summary>启动自动重发：逐条发，成功就移除，失败立即停（保留剩余，下次再试）。</summary>
    private IEnumerator RetryPendingCoroutine()
    {
        InspectionOutboxFile file = LoadOutbox();
        int total = file.items.Count;
        int sent = 0;

        for (int i = file.items.Count - 1; i >= 0 && sent < maxRetryPerStart; i--)
        {
            PendingInspection pending = file.items[i];
            if (pending == null || pending.record == null) continue;

            bool done = false;
            bool ok = false;
            string msg = "";

            inflight = true;
            inflightStartedAt = Time.unscaledTime;

            yield return StartCoroutine(PostRecord(pending.record, (success, message) =>
            {
                ok = success;
                msg = message;
                done = true;
                inflight = false;
            }));

            // PostRecord 内部已保证一定会回调，这里再兜一层防止意外。
            if (!done)
            {
                inflight = false;
                yield break;
            }

            if (ok)
            {
                RemovePending(pending.clientId);
                sent++;
                Debug.Log("[巡检] 重发成功：" + msg);
            }
            else
            {
                Debug.LogWarning("[巡检] 重发失败，停止本次重发（记录仍保留在本地）：" + msg);
                yield break;
            }
        }

        Debug.Log("[巡检] 自动重发结束：共 " + total + " 条，成功 " + sent + " 条，剩余 " + PendingCount + " 条。");
    }

    // =====================================================================
    //  队列文件读写
    // =====================================================================

    private static InspectionOutboxFile LoadOutbox()
    {
        InspectionOutboxFile file = new InspectionOutboxFile();

        try
        {
            string path = OutboxPath;
            if (!File.Exists(path)) return file;

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return file;

            InspectionOutboxFile parsed = JsonUtility.FromJson<InspectionOutboxFile>(text);
            if (parsed == null) return file;
            if (parsed.items == null) parsed.items = new List<PendingInspection>();

            return parsed;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[巡检] 读取本地队列失败（按空队列处理）：" + e.Message);
            return file;
        }
    }

    private static void SaveOutbox(InspectionOutboxFile file)
    {
        string path = OutboxPath;

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 先写临时文件再替换：写到一半崩了也不会把已有记录全毁掉。
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(file, true), Encoding.UTF8);

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[巡检] 写入本地队列失败：" + e.Message);
        }
    }
}
