// IssueMarkerLayer.cs
//
// 职责：把「巡检记录」变成「三维场景里的问题标记」，并且让每个标记都记住自己对应的记录 id。
//
// 为什么要有这一层：
//   之前提交成功后留下的绿球，只是场景里一个普通的 Sphere，除了位置什么都没记住 ——
//   列表里点某条记录，代码根本不知道哪个球是它。修法有两条路：
//     (a) 提交时把 id 塞进球的名字里 —— 只能覆盖「本次会话提交的」，历史记录照样没标记；
//     (b) **以数据为唯一来源**：每次拉到记录列表，就按记录里的坐标重建/更新标记。
//   这里选 (b)：标记是记录的投影，不是记录的副本。好处是重开 Unity 标记还在、
//   历史 17 条记录照样能看到、点列表任何一条都能高亮，而且永远不会有「球和记录对不上」。
//
// 关联用 targetId（PUMP-A 这种稳定编号），不用 targetName —— 设备显示名随时可能改，
// 编号不会。所以标记上同时存 recordId 和 targetId：
//   recordId → 列表点某条记录时精确高亮那一个球（一台设备多条记录也不会串）
//   targetId → 点三维设备时能反查这台设备的全部记录
//
// 标记球不挂 Collider：它不能挡住设备点击（否则点上去只会打到球）。

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>挂在单个问题标记上的数据。记录 id 就是在这里保存的。</summary>
public class IssueMarkerDot : MonoBehaviour
{
    [Tooltip("对应后端那条巡检记录的 id（主键），列表点选时靠它对上")]
    public string recordId;

    [Tooltip("设备稳定编号，如 PUMP-A。一台设备的多条记录靠它聚在一起")]
    public string targetId;

    [Tooltip("后端状态：open / in_progress / resolved")]
    public string status;

    [Tooltip("后端优先级：high / medium / low。只看颜色分不出轻重，所以用大小区分")]
    public string priority;

    public Renderer cachedRenderer;

    [NonSerialized] public Color baseColor;
    [NonSerialized] public float baseScale = 1f;
}

/// <summary>
/// 场景里的问题标记集合。全局只有一个，用 IssueMarkerLayer.Instance 访问；
/// 没挂到场景里也没关系，Ensure() 会运行时建一个（不写进场景文件，退出 Play 自动消失）。
/// </summary>
public class IssueMarkerLayer : MonoBehaviour
{
    public static IssueMarkerLayer Instance { get; private set; }

    [Header("外观")]
    public float markerSize = 0.12f;
    [Tooltip("标记沿法线方向抬起的高度，避免和表面 z-fighting")]
    public float surfaceLift = 0.02f;

    [Header("按优先级缩放（颜色归状态管，大小归优先级管）")]
    [Tooltip("high 的标记放大，一眼能看出哪条最急")]
    public float highScale = 1.45f;
    public float mediumScale = 1.0f;
    [Tooltip("low 缩小，避免小问题在视觉上和大问题一样重")]
    public float lowScale = 0.72f;
    [Tooltip("优先级缺失（老数据）时的兜底倍率")]
    public float unknownPriorityScale = 1.0f;

    [Header("按状态着色（对应后端 open / in_progress / resolved）")]
    public Color openColor = new Color(0.90f, 0.24f, 0.20f, 1f);
    public Color progressColor = new Color(0.98f, 0.62f, 0.15f, 1f);
    public Color resolvedColor = new Color(0.16f, 0.70f, 0.36f, 1f);
    public Color unknownColor = new Color(0.55f, 0.57f, 0.62f, 1f);

    [Header("高亮")]
    public Color highlightColor = new Color(1f, 0.92f, 0.25f, 1f);
    public float highlightSeconds = 2.2f;

    readonly Dictionary<string, IssueMarkerDot> dots = new Dictionary<string, IssueMarkerDot>();
    readonly List<string> staleBuffer = new List<string>();

    Transform root;
    Coroutine highlightRoutine;
    IssueMarkerDot highlighted;

    /// <summary>当前标记数量（供日志/自检用）。</summary>
    public int Count { get { return dots.Count; } }

    /// <summary>找场景里的标记层；没有就建一个。挂在场景根节点，不进 Canvas。</summary>
    public static IssueMarkerLayer Ensure()
    {
        if (Instance != null) return Instance;

        IssueMarkerLayer found = FindObjectOfType<IssueMarkerLayer>(true);
        if (found != null)
        {
            Instance = found;
            return found;
        }

        GameObject go = new GameObject("IssueMarkerLayer");
        IssueMarkerLayer layer = go.AddComponent<IssueMarkerLayer>();
        Instance = layer;
        Debug.Log("[巡检] 场景里没有 IssueMarkerLayer，已运行时创建。");
        return layer;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // 重复的层没有意义，直接关掉自己，避免两个层同时建球
            Debug.LogWarning("[巡检] 检测到重复的 IssueMarkerLayer，已禁用多余的那个。");
            enabled = false;
            return;
        }

        Instance = this;

        GameObject rootGo = new GameObject("Markers");
        rootGo.transform.SetParent(transform, false);
        root = rootGo.transform;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------
    //  与后端数据同步
    // ------------------------------------------------------------------

    /// <summary>
    /// 用后端返回的记录列表刷新场景标记：
    /// 记录有的标记要有、颜色跟着状态走、记录没了标记也要没。
    /// 传进来的必须是**全量**记录（不是筛选后的），否则筛选一次就会把别的标记删掉。
    /// </summary>
    public void Sync(List<IssueDto> records)
    {
        if (root == null) return;

        if (records == null)
        {
            ClearAll();
            return;
        }

        HashSet<string> alive = new HashSet<string>();
        int created = 0, updated = 0, skipped = 0;

        for (int i = 0; i < records.Count; i++)
        {
            IssueDto issue = records[i];
            if (issue == null || string.IsNullOrEmpty(issue.id))
            {
                skipped++;
                continue;
            }

            alive.Add(issue.id);

            Vector3 pos;
            bool hasPos = TryGetPosition(issue, out pos);
            if (!hasPos)
            {
                // 早期记录（消防通道那几条）坐标是全 0，落在世界原点会很怪。
                skipped++;
                continue;
            }

            if (dots.ContainsKey(issue.id))
            {
                UpdateDot(dots[issue.id], issue, pos);
                updated++;
            }
            else
            {
                IssueMarkerDot dot = CreateDot(issue, pos);
                if (dot != null) created++;
            }
        }

        // 后端已经没了的记录，标记也要清掉（避免删数据后场景里留孤球）
        staleBuffer.Clear();
        foreach (KeyValuePair<string, IssueMarkerDot> kv in dots)
        {
            if (!alive.Contains(kv.Key)) staleBuffer.Add(kv.Key);
        }

        for (int i = 0; i < staleBuffer.Count; i++)
        {
            RemoveDot(staleBuffer[i]);
        }

        Debug.Log("[巡检] 三维标记已同步：新建 " + created + "、更新 " + updated +
                  "、跳过 " + skipped + "、移除 " + staleBuffer.Count + "，当前共 " + dots.Count + " 个。");
    }

    /// <summary>
    /// 按「当前列表里显示出来的记录」控制标记显隐。
    ///
    /// 为什么需要它：Sync 是按**全量**记录建球的（这样重开 Play、历史记录都还在），
    /// 但用户眼里只有列表 —— 列表筛出 2 条、场景里却亮着 15 个球，就会觉得「球是重复生成的」。
    /// 这里把没被筛中的球隐藏（不销毁，切回来立刻能显示），让「列表几条 = 场景几个球」永远一致。
    ///
    /// 传 null 表示不做筛选，全部显示。
    /// </summary>
    public void ApplyVisibility(List<IssueDto> displayed)
    {
        if (root == null) return;

        HashSet<string> show = null;

        if (displayed != null)
        {
            show = new HashSet<string>();
            for (int i = 0; i < displayed.Count; i++)
            {
                if (displayed[i] != null && !string.IsNullOrEmpty(displayed[i].id))
                    show.Add(displayed[i].id);
            }
        }

        int shown = 0, hidden = 0;

        foreach (KeyValuePair<string, IssueMarkerDot> kv in dots)
        {
            IssueMarkerDot dot = kv.Value;
            if (dot == null) continue;

            bool on = show == null || show.Contains(kv.Key);

            if (dot.gameObject.activeSelf != on) dot.gameObject.SetActive(on);

            if (on) shown++; else hidden++;
        }

        // 只在「有球被隐藏」时打日志，避免每敲一个字都刷一行
        if (hidden > 0)
        {
            Debug.Log("[巡检] 三维标记按筛选显示：" + shown + " 个可见 / " + hidden + " 个已隐藏" +
                      "（隐藏的球还在，清空筛选就回来）");
        }
    }

    /// <summary>
    /// 收编一个已经在场景里的标记球（提交成功时用）。
    ///
    /// 为什么不在提交成功后就地销毁、等下一次 Sync 重建：那样会有一小段「标记消失」的空档，
    /// 用户会以为提交失败。这里直接把待提交的球接管过来、贴上 recordId，
    /// 立刻就能按状态上色；之后 Sync 按 id 找到它，只会更新颜色，不会再建一个。
    /// </summary>
    public IssueMarkerDot Adopt(GameObject marker, string recordId, string targetId, string status)
    {
        return Adopt(marker, recordId, targetId, status, "");
    }

    /// <summary>带优先级的收编：提交时知道优先级，尺寸一次到位，不用等下一次 Sync。</summary>
    public IssueMarkerDot Adopt(GameObject marker, string recordId, string targetId, string status, string priority)
    {
        if (marker == null || string.IsNullOrEmpty(recordId)) return null;

        if (root != null)
        {
            // worldPositionStays=true：挂到层根节点下但不改变世界坐标
            marker.transform.SetParent(root, true);
        }

        IssueMarkerDot dot = marker.GetComponent<IssueMarkerDot>();
        if (dot == null) dot = marker.AddComponent<IssueMarkerDot>();

        dot.recordId = recordId;
        dot.targetId = targetId;
        dot.status = status;
        dot.priority = priority;
        dot.cachedRenderer = marker.GetComponent<Renderer>();

        // 尺寸按优先级重算：待提交的球是统一的 markerSize，收编后要和 Sync 生成的球口径一致
        dot.baseScale = markerSize * ScaleForPriority(priority);
        if (dot.baseScale > 0f)
            marker.transform.localScale = Vector3.one * dot.baseScale;

        dot.baseColor = ColorForStatus(status);

        if (dot.cachedRenderer != null && dot.cachedRenderer.material != null)
            ApplyColor(dot.cachedRenderer.material, dot.baseColor);

        dots[recordId] = dot;
        return dot;
    }

    /// <summary>记录的坐标。全 0 视为没有坐标（早期数据没有 position）。</summary>
    static bool TryGetPosition(IssueDto issue, out Vector3 pos)
    {
        pos = Vector3.zero;

        if (issue.position == null) return false;

        float x = issue.position.x;
        float y = issue.position.y;
        float z = issue.position.z;

        if (Mathf.Abs(x) < 0.001f && Mathf.Abs(y) < 0.001f && Mathf.Abs(z) < 0.001f)
            return false;

        pos = new Vector3(x, y, z);
        return true;
    }

    IssueMarkerDot CreateDot(IssueDto issue, Vector3 pos)
    {
        float scale = markerSize * ScaleForPriority(issue.SafePriority);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "IssueMarker_" + ShortId(issue.id);
        go.transform.SetParent(root, false);
        go.transform.position = pos + Vector3.up * surfaceLift;
        go.transform.localScale = Vector3.one * scale;

        // 标记不能被射线打到：点设备时如果先打到球，就取不到设备编号了
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer rend = go.GetComponent<Renderer>();
        if (rend != null) rend.material = CreateMaterial(ColorForStatus(issue.status));

        IssueMarkerDot dot = go.AddComponent<IssueMarkerDot>();
        dot.cachedRenderer = rend;
        dot.baseScale = scale;
        UpdateDot(dot, issue, pos);

        dots[issue.id] = dot;
        return dot;
    }

    void UpdateDot(IssueMarkerDot dot, IssueDto issue, Vector3 pos)
    {
        if (dot == null) return;

        dot.recordId = issue.id;
        dot.targetId = issue.targetId;
        dot.status = issue.status;
        dot.priority = issue.SafePriority;

        Color c = ColorForStatus(issue.status);
        dot.baseColor = c;

        // 优先级决定大小。正在脉冲高亮的球由协程接管缩放，这里不能抢，否则会打断动画
        float scale = markerSize * ScaleForPriority(issue.SafePriority);
        dot.baseScale = scale;
        if (highlighted != dot)
            dot.transform.localScale = Vector3.one * scale;

        // 位置允许后端被修正过（比如以后支持拖动标记），每次都跟数据走
        if (dot.transform.position != pos + Vector3.up * surfaceLift)
            dot.transform.position = pos + Vector3.up * surfaceLift;

        if (dot.cachedRenderer != null && dot.cachedRenderer.material != null)
            ApplyColor(dot.cachedRenderer.material, c);
    }

    void RemoveDot(string recordId)
    {
        IssueMarkerDot dot;
        if (!dots.TryGetValue(recordId, out dot)) return;

        dots.Remove(recordId);

        if (dot == null) return;
        if (highlighted == dot) highlighted = null;

        Destroy(dot.gameObject);
    }

    public void ClearAll()
    {
        foreach (KeyValuePair<string, IssueMarkerDot> kv in dots)
        {
            if (kv.Value != null) Destroy(kv.Value.gameObject);
        }

        dots.Clear();
        highlighted = null;
    }

    // ------------------------------------------------------------------
    //  查询 / 高亮
    // ------------------------------------------------------------------

    /// <summary>按记录 id 找标记（列表点某条记录时用）。</summary>
    public bool TryGetMarker(string recordId, out IssueMarkerDot dot)
    {
        dot = null;
        if (string.IsNullOrEmpty(recordId)) return false;
        return dots.TryGetValue(recordId, out dot) && dot != null;
    }

    /// <summary>
    /// 按屏幕坐标找光标附近最近的可见标记（点三维标记时用）。
    ///
    /// 为什么不给球加 Collider 走 Physics.Raycast：标记贴在设备表面，
    /// 一旦有碰撞体，点设备就会先打到球、拿不到设备编号
    /// （CreateDot 里特意把 Collider 删掉就是这个原因）。
    /// 用「世界坐标投影到屏幕 → 取距离最小的」完全不参与物理求交：
    /// 既不会挡住设备点击，也不用额外配 Layer，标记隐藏时也自动退出拾取。
    /// </summary>
    public IssueMarkerDot PickByScreenPoint(Vector2 screenPoint, Camera camera, float maxPixels)
    {
        if (camera == null || root == null) return null;

        IssueMarkerDot best = null;
        float bestDistance = maxPixels;

        foreach (KeyValuePair<string, IssueMarkerDot> kv in dots)
        {
            IssueMarkerDot dot = kv.Value;
            if (dot == null || dot.gameObject == null) continue;
            if (!dot.gameObject.activeSelf) continue;   // 被筛选隐藏的标记不参与拾取

            Vector3 screen = camera.WorldToScreenPoint(dot.transform.position);
            if (screen.z <= 0f) continue;               // 在相机背后

            float distance = Vector2.Distance(new Vector2(screen.x, screen.y), screenPoint);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = dot;
            }
        }

        return best;
    }

    /// <summary>按设备编号取该设备的全部标记（点三维设备时用）。</summary>
    public List<IssueMarkerDot> GetMarkersByTarget(string targetId)
    {
        List<IssueMarkerDot> result = new List<IssueMarkerDot>();

        if (string.IsNullOrEmpty(targetId)) return result;

        foreach (KeyValuePair<string, IssueMarkerDot> kv in dots)
        {
            if (kv.Value != null && kv.Value.targetId == targetId) result.Add(kv.Value);
        }

        return result;
    }

    /// <summary>高亮某条记录的标记。返回 false 表示这条记录没有标记（比如坐标缺失）。</summary>
    public bool Highlight(string recordId)
    {
        IssueMarkerDot dot;
        if (!TryGetMarker(recordId, out dot)) return false;

        // 这条记录可能是被筛选隐藏的（比如在「全部」视图里点了一个不在当前筛选内的 id）。
        // 要闪就得先让它出现，否则表现为「点了没反应」。
        if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);

        if (highlightRoutine != null)
        {
            StopCoroutine(highlightRoutine);
            RestoreDot(highlighted);
        }

        highlighted = dot;
        highlightRoutine = StartCoroutine(PulseRoutine(dot));
        return true;
    }

    /// <summary>设备高亮：这台设备的所有标记一起闪（点三维设备时用）。</summary>
    public int HighlightByTarget(string targetId)
    {
        List<IssueMarkerDot> list = GetMarkersByTarget(targetId);
        if (list.Count == 0) return 0;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null && !list[i].gameObject.activeSelf)
                list[i].gameObject.SetActive(true);
        }

        if (highlightRoutine != null)
        {
            StopCoroutine(highlightRoutine);
            RestoreDot(highlighted);
        }

        highlighted = list[0];
        highlightRoutine = StartCoroutine(PulseRoutine(list));
        return list.Count;
    }

    public void ClearHighlight()
    {
        if (highlightRoutine != null)
        {
            StopCoroutine(highlightRoutine);
            highlightRoutine = null;
        }

        RestoreDot(highlighted);
        highlighted = null;
    }

    IEnumerator PulseRoutine(IssueMarkerDot dot)
    {
        return PulseRoutine(new List<IssueMarkerDot> { dot });
    }

    /// <summary>
    /// 脉冲：放大 + 变亮，来回两次。用 Mathf.Sin 做平滑，比直接 Lerp 更像"呼吸"。
    /// 结束后必须把缩放和颜色还原，否则标记会一直保持高亮状态，下次就分不清哪个是新点的。
    /// </summary>
    IEnumerator PulseRoutine(List<IssueMarkerDot> targets)
    {
        float elapsed = 0f;
        const float pulsePeriod = 0.75f;

        while (elapsed < highlightSeconds)
        {
            elapsed += Time.unscaledDeltaTime;

            float phase = Mathf.Sin(elapsed / pulsePeriod * Mathf.PI * 2f) * 0.5f + 0.5f;

            for (int i = 0; i < targets.Count; i++)
            {
                IssueMarkerDot d = targets[i];
                if (d == null) continue;

                float s = d.baseScale * Mathf.Lerp(1f, 1.9f, phase);
                d.transform.localScale = Vector3.one * s;

                if (d.cachedRenderer != null && d.cachedRenderer.material != null)
                {
                    Color c = Color.Lerp(d.baseColor, highlightColor, phase * 0.85f);
                    ApplyColor(d.cachedRenderer.material, c);
                }
            }

            yield return null;
        }

        for (int i = 0; i < targets.Count; i++) RestoreDot(targets[i]);

        highlightRoutine = null;
        highlighted = null;
    }

    static void RestoreDot(IssueMarkerDot dot)
    {
        if (dot == null) return;

        dot.transform.localScale = Vector3.one * dot.baseScale;

        if (dot.cachedRenderer != null && dot.cachedRenderer.material != null)
            ApplyColor(dot.cachedRenderer.material, dot.baseColor);
    }

    // ------------------------------------------------------------------
    //  外观工具
    // ------------------------------------------------------------------

    /// <summary>
    /// 优先级 → 标记尺寸倍率。
    /// 为什么不用颜色区分优先级：颜色已经被状态占用了（待处理/处理中/已解决），
    /// 两个维度挤在同一个通道上，用户根本分不清「红色」到底是「待处理」还是「高优先级」。
    /// 所以状态 = 颜色，优先级 = 大小，各占一个视觉通道。
    /// </summary>
    public float ScaleForPriority(string priority)
    {
        switch ((priority ?? "").Trim().ToLowerInvariant())
        {
            case "high": return highScale;
            case "medium": return mediumScale;
            case "low": return lowScale;
            default: return unknownPriorityScale;
        }
    }

    public Color ColorForStatus(string status)
    {
        switch ((status ?? "").Trim().ToLowerInvariant())
        {
            case "open": return openColor;
            case "in_progress": return progressColor;
            case "resolved": return resolvedColor;
            default: return unknownColor;
        }
    }

    /// <summary>
    /// URP 的颜色属性是 _BaseColor，内置管线是 _Color，两个都写一遍。
    /// 只写 _Color 会静默失效 —— 这是「提交成功但标记没变色」的老坑。
    /// </summary>
    public static void ApplyColor(Material material, Color color)
    {
        if (material == null) return;

        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
    }

    static Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");

        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");

        if (shader == null)
        {
            Debug.LogWarning("[巡检] 找不到可用的标记 Shader，标记会是默认粉色。");
            return null;
        }

        Material m = new Material(shader);
        ApplyColor(m, color);
        return m;
    }

    static string ShortId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "unknown";
        return id.Length <= 12 ? id : id.Substring(id.Length - 6);
    }
}
