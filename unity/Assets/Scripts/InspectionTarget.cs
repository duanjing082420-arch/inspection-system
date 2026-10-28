using UnityEngine;

/// <summary>
/// 巡检对象标识（整合版）。
///
/// 职责：把「射线打到的零件」翻译成「这条记录该挂在哪个设备上」。
/// 可以挂在设备父对象上（Pump_Unit_A / Cabinet_A），也可以挂在需要单独上报的零件上。
///
/// 整合版相对旧版的变化（旧 API 全部保留，场景引用不需要重拖）：
///   1. 新增 ResolveInfo()：一次调用同时拿到 id / name / 是否真的解析到设备，
///      调用方不再需要自己写一堆 null 判断（旧版只返回组件，字段还得二次取值，容易漏判）。
///   2. 新增 autoFillId：组件挂上或改名时自动把 TargetId 补成物体名。
///      这是后端 400「targetId 和 title 不能为空」最常见的原因 —— 组件挂了但 ID 没填。
///   3. TargetId / DisplayName 统一 Trim，避免空格导致后端校验失败。
/// </summary>
public class InspectionTarget : MonoBehaviour
{
    [Header("巡检设备信息")]
    [SerializeField] private string displayName = "";

    [SerializeField] private string targetId = "";

    [Tooltip("设备所在的物理区域，如「一号泵房」「配电区」。现场按区域分工，记录和列表都能按区域筛；留空由后端归到「未分区」")]
    [SerializeField] private string zone = "";

    [Tooltip("如果不为空，优先使用指定的设备根节点")]
    [SerializeField] private InspectionTarget owner;

    [Header("编辑器辅助")]
    [Tooltip("勾选后：添加组件或在 Inspector 里改名时，自动把 TargetId 补成物体名。" +
             "设备编号是 Unity ↔ 后端 ↔ React 关联记录的依据，漏填记录就成了孤儿，列表里筛不出来。")]
    [SerializeField] private bool autoFillId = true;

    /// <summary>显示名：优先自定义中文名，没填就用物体名。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName.Trim();

            return gameObject != null ? gameObject.name : "";
        }
    }

    /// <summary>设备标识：优先自定义 ID（PUMP-A 这类稳定编号），没填就用物体名。</summary>
    public string TargetId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(targetId))
                return targetId.Trim();

            return gameObject != null ? gameObject.name : "";
        }
    }

    /// <summary>
    /// 区域：没填就向上找父级设备的区域（设备挂在区域父物体下时不用逐台填），
    /// 都为空返回空串 —— 后端会兜成「未分区」，这里不擅自编一个名字。
    /// </summary>
    public string Zone
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(zone))
                return zone.Trim();

            InspectionTarget parent = GetOwner();
            if (parent != null && parent != this)
            {
                // GetOwner 会沿父链找最外层，这里直接读它自己填的区域
                string parentZone = parent.OwnZone;
                if (!string.IsNullOrWhiteSpace(parentZone)) return parentZone;
            }

            return "";
        }
    }

    /// <summary>
    /// 运行时/编辑器脚本设置区域（场景生成脚本按设备类型分区时用）。
    /// 走方法而不是直接改字段：SerializeField 私有字段外部改不到，
    /// 而且这里统一做一次 Trim，避免"一号泵房 "和"一号泵房"变成两个区域。
    /// </summary>
    public void SetZone(string value)
    {
        zone = value == null ? "" : value.Trim();
    }

    /// <summary>只返回自己填的区域，不做父级回溯（给 Zone 属性内部用，避免递归）。</summary>
    string OwnZone
    {
        get { return zone == null ? "" : zone.Trim(); }
    }

    /// <summary>是否填了自定义 ID（用于区分「PUMP-A」和「物体名兜底」两种来源）。</summary>
    public bool HasCustomId
    {
        get { return !string.IsNullOrWhiteSpace(targetId); }
    }

    /// <summary>是否填了自定义显示名。</summary>
    public bool HasCustomName
    {
        get { return !string.IsNullOrWhiteSpace(displayName); }
    }

    /// <summary>
    /// 返回实际归属的设备标识。
    /// 指定了 owner 就走 owner；否则沿 Transform 往上找最外层的 InspectionTarget。
    /// </summary>
    public InspectionTarget GetOwner()
    {
        if (owner != null && owner != this)
            return owner.GetOwner();

        InspectionTarget parentTarget = GetComponentInParent<InspectionTarget>();

        if (parentTarget != null && parentTarget != this)
            return parentTarget.GetOwner();

        return this;
    }

    /// <summary>沿父级链找归属设备组件，找不到返回 null（旧接口，保留兼容）。</summary>
    public static InspectionTarget Resolve(GameObject hitObject)
    {
        if (hitObject == null)
            return null;

        InspectionTarget target = hitObject.GetComponentInParent<InspectionTarget>();

        return target != null ? target.GetOwner() : null;
    }

    /// <summary>
    /// 整合入口：一次解析出提交需要的全部信息。
    ///
    /// 关键：必须从「真正被射线打到的物体」往上找（如 Motor_Body / Cabinet_Door），
    /// 标记球是新生成的孤立物体，拿它解析永远得到 null —— 这是旧版表单显示「未知设备」的根因。
    /// </summary>
    public static ResolvedTarget ResolveInfo(GameObject hitObject)
    {
        if (hitObject == null)
            return ResolvedTarget.Unresolved("");

        InspectionTarget found = hitObject.GetComponentInParent<InspectionTarget>();

        if (found == null)
            return ResolvedTarget.Unresolved(hitObject.name);

        return ResolvedTarget.From(found.GetOwner(), hitObject.name);
    }

    // ------------------------------------------------------------------
    //  编辑器：ID 自动补齐
    // ------------------------------------------------------------------

    private void Reset()
    {
        // 组件第一次挂到物体上时触发：直接把名字填进去，省一步手工操作。
        displayName = gameObject != null ? gameObject.name : "";
        targetId = gameObject != null ? gameObject.name : "";
    }

    private void OnValidate()
    {
        if (!autoFillId) return;
        if (string.IsNullOrWhiteSpace(targetId) && gameObject != null)
            targetId = gameObject.name;
    }

    private void Awake()
    {
        // 运行时再兜一次：场景里可能存在早期挂的组件，ID 为空，
        // 这里补齐后提交就不会被后端以「targetId 不能为空」拒掉。
        if (gameObject != null && string.IsNullOrWhiteSpace(targetId))
            targetId = gameObject.name;
    }
}

/// <summary>一次解析的结果。Resolved=false 表示这个物体没挂 InspectionTarget，用的是物体名兜底。</summary>
public sealed class ResolvedTarget
{
    /// <summary>归属设备组件（未解析到时为 null）。</summary>
    public InspectionTarget Target { get; private set; }

    /// <summary>提交用的设备标识（必填，不会为空——未解析时回落到命中物体名）。</summary>
    public string Id { get; private set; }

    /// <summary>提交用的设备显示名。</summary>
    public string Name { get; private set; }

    /// <summary>设备所属区域；没配置时为空串，由后端兜底成「未分区」。</summary>
    public string Zone { get; private set; }

    /// <summary>是否真的解析到了 InspectionTarget。</summary>
    public bool Resolved { get; private set; }

    /// <summary>射线实际命中的物体名（零件名，用于日志排查，不参与提交）。</summary>
    public string HitObjectName { get; private set; }

    private ResolvedTarget() { }

    public static ResolvedTarget From(InspectionTarget target, string hitObjectName)
    {
        if (target == null)
            return Unresolved(hitObjectName);

        return new ResolvedTarget
        {
            Target = target,
            Id = target.TargetId,
            Name = target.DisplayName,
            Zone = target.Zone,
            Resolved = true,
            HitObjectName = hitObjectName
        };
    }

    public static ResolvedTarget Unresolved(string hitObjectName)
    {
        // 没挂组件时仍然要能提交：用命中物体名顶上，
        // 否则点管道、墙面这类没挂标识的物体会直接被后端拒掉。
        return new ResolvedTarget
        {
            Target = null,
            Id = string.IsNullOrEmpty(hitObjectName) ? "UNKNOWN" : hitObjectName,
            Name = string.IsNullOrEmpty(hitObjectName) ? "未知对象" : hitObjectName,
            Zone = "", // 没挂 InspectionTarget 就没有区域归属，交给后端兜底
            Resolved = false,
            HitObjectName = hitObjectName
        };
    }
}
