
using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class InspectionRaycaster : MonoBehaviour
{
    [Header("射线检测")]
    public Camera targetCamera;
    public float maxDistance = 100f;

    [Header("标记设置")]
    public float markerSize = 0.14f;

    [Tooltip("点三维标记时的判定半径（屏幕像素）。标记没有碰撞体，靠「投影到屏幕后离光标多近」来拾取")]
    public float markerPickRadius = 26f;
    public Color markerColor = new Color(1f, 0.25f, 0.1f, 1f);
    // 提交成功后标记变成绿色并永久留在场景中（表示“这条记录已确认”）
    public Color submittedColor = new Color(0.13f, 0.72f, 0.35f, 1f);

    // 当前待提交标记
    private GameObject currentMarker;

    // 当前命中坐标
    private Vector3 currentMarkerPosition;

    // 表单打开时禁止继续点击场景
    public bool IsFormOpen { get; set; }

    // 最近一次命中的「设备名」。命中柜门/指示灯这类子物体时，会自动归到所属机柜。
    // 供 IssueFormController 在表单里显示（评分项：表单要显示当前巡检对象）。
    public string LastHitObjectName { get; private set; }

    // 最近一次「真正被射线打到的物体」（可能是零件，如 Motor_Body / Cabinet_Door）。
    // 只能由它去查 InspectionTarget —— 标记球是新生成的空物体，挂在它上面查不出归属设备。
    public GameObject LastHitObject { get; private set; }

    // 已知设备命名前缀。命中的物体若在这些设备之下，统一上报设备名而不是零件名。
    // 例如点 Cabinet_Door 会显示 Cabinet_A，点 Green_Light 也显示 Cabinet_A。
    static readonly string[] DevicePrefixes = { "Cabinet_", "Pump_", "Pipe_", "Valve_" };

    // 放置标记后要打开的表单控制器（在 Main Camera 的 Inspector 上把 IssueFormPanel 拖到这里）
    public IssueFormController formController;

    [Header("查看模式（单击设备 = 看这台设备的记录）")]
    [Tooltip("留空会自动查找场景里的 InspectionRecordList")]
    public InspectionRecordList recordList;

    /// <summary>
    /// 上报模式。false（默认）= 单击设备只查看该设备的记录；true = 单击设备放置问题标记并弹表单。
    ///
    /// 为什么要有这个开关：查看和上报抢同一个鼠标左键。真做起来只能二选一，
    /// 选「先选工具、再点场景」—— 想上报先点「＋ 新增问题」，此时才允许放标记。
    /// 这样单击设备永远是安全的查看动作，演示时也不会误建一堆标记。
    /// </summary>
    public bool reportMode = false;

    public bool HasCurrentMarker
    {
        get { return currentMarker != null; }
    }

    private void Start()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            Debug.LogError("InspectionRaycaster：没有找到摄像机。");
            enabled = false;
            return;
        }

        // Inspector 里 Form Controller 为空时自动兜底：按类型找场景里的表单控制器。
        // 这样即使忘了手动拖拽、或面板被重新生成导致引用丢失，点场景也能正常弹出表单。
        if (formController == null)
        {
            formController = FindObjectOfType<IssueFormController>(true);
            if (formController == null)
            {
                Debug.LogWarning("InspectionRaycaster：场景里没有 IssueFormController。" +
                    "请先执行 Tools ▸ 巡检 ▸ 创建上报面板 IssueFormPanel。");
            }
            else
            {
                Debug.Log("巡检：已自动绑定 IssueFormController（Inspector 中为空也能工作）。");
            }
        }

        // 视角控制器兜底：Main Camera 上如果没挂 InspectionCamera，这里运行时补一个。
        // 目的是就算忘了跑菜单、或者菜单那次没存进场景，Play 之后 WASD / 右键拖动也一定能用。
        // （运行时 AddComponent 不会写进场景文件，退出 Play 自动消失，不会留下脏组件。）
        if (GetComponent<InspectionCamera>() == null)
        {
            InspectionCamera ic = gameObject.AddComponent<InspectionCamera>();
            ic.raycaster = this;
            Debug.Log("巡检：已自动挂载 InspectionCamera（WASD 平移 / QE 升降 / 右键拖动转视角）。");
        }

        // 记录面板兜底：查看模式要靠它显示设备记录。
        if (recordList == null)
        {
            recordList = FindObjectOfType<InspectionRecordList>(true);

            if (recordList != null)
            {
                Debug.Log("巡检：已自动绑定 InspectionRecordList（点三维设备 → 看该设备记录）。");
            }
            else
            {
                Debug.LogWarning("巡检：场景里没有 InspectionRecordList，单击设备只会打日志、" +
                                 "不会弹出记录面板。请先执行 Tools ▸ 巡检 ▸ 创建巡检记录面板 RecordPanel。");
            }
        }

        // 反向接线：记录面板也要能拿到本脚本（它点列表时要控制相机/标记）。
        if (recordList != null && recordList.raycaster == null)
            recordList.raycaster = this;

        Debug.Log("巡检射线检测系统已启动（模式：" + (reportMode ? "上报" : "查看") + "）。");
    }

    private void Update()
    {
        if (IsFormOpen)
            return;

        // 查看模式下按 Esc 退出上报模式（误点「新增问题」时不用去找按钮）
        if (reportMode && Input.GetKeyDown(KeyCode.Escape))
        {
            if (recordList != null)
            {
                recordList.ExitReportMode();
                return;
            }
        }

        if (!Input.GetMouseButtonDown(0))
            return;

        // 点击 UI 时不检测场景，防止误放标记
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject())
        {
            return;
        }

        if (reportMode)
            DetectSceneObject();
        else
            DetectDeviceForBrowse();
    }

    /// <summary>
    /// 查看模式：单击设备 → 让记录面板显示这台设备的全部记录。
    ///
    /// 注意关联键是 targetId（PUMP-A），不是显示名：
    ///   设备显示名随时可能被改，编号是稳定的，用它才能保证「改名后记录还挂在这台设备上」。
    /// 一台设备有多条问题时，面板会把它们全部列出来（不是只显示最新一条）。
    /// </summary>
    private void DetectDeviceForBrowse()
    {
        Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance))
        {
            // 点到空处：再看一眼光标附近有没有问题标记（设备没命中不代表用户没点东西）
            if (TryPickMarker()) return;

            Debug.Log("[巡检] 查看模式：射线未命中任何物体，附近也没有问题标记。");
            return;
        }

        // 从真正被命中的物体往上找归属设备（点柜门/指示灯也会归到机柜）
        ResolvedTarget info = InspectionTarget.ResolveInfo(hit.collider.gameObject);

        LastHitObjectName = ResolveDeviceName(hit.collider.gameObject);
        LastHitObject = hit.collider.gameObject;

        // 点到地面/墙面这类没挂 InspectionTarget 的物体：不弹面板。
        // 否则点一下地板就跳出「Ground 暂无巡检记录」，反而像出错了。
        if (!info.Resolved)
        {
            // 地面、墙面这类没挂标识的物体不弹面板，但标记可能就浮在它们前面
            if (TryPickMarker()) return;

            Debug.Log("[巡检] 查看模式：命中的 " + hit.collider.gameObject.name +
                      " 没有挂 InspectionTarget，不是巡检设备，附近也没有问题标记，忽略。");
            return;
        }

        Debug.Log("[巡检] 查看模式：命中 " + hit.collider.gameObject.name +
                  " → 设备「" + info.Name + "」（编号 " + info.Id + "）");

        if (recordList == null)
        {
            Debug.LogWarning("[巡检] 没有 InspectionRecordList，无法显示设备记录。");
            return;
        }

        recordList.ShowDeviceRecords(info.Id, info.Name);
    }

    /// <summary>
    /// 没点到设备时，看光标附近有没有问题标记：有就打开那条记录的详情。
    ///
    /// 顺序是「设备优先、标记兜底」，不能反过来：
    /// 标记都贴在设备表面，反过来的话点设备会先被标记吃掉，
    /// 想看「这台设备的全部记录」的用户会被单条问题打断。
    /// 标记没有碰撞体，靠屏幕投影距离判定，所以这个兜底不会干扰任何物理求交。
    /// </summary>
    bool TryPickMarker()
    {
        IssueMarkerLayer layer = IssueMarkerLayer.Ensure();
        if (layer == null) return false;

        IssueMarkerDot dot = layer.PickByScreenPoint(Input.mousePosition, targetCamera, markerPickRadius);
        if (dot == null) return false;

        Debug.Log("[巡检] 命中三维标记 → 记录 " + dot.recordId +
                  "（设备 " + dot.targetId + "，状态 " + dot.status + "，优先级 " + dot.priority + "）");

        if (recordList == null)
        {
            Debug.LogWarning("[巡检] 命中问题标记，但场景里没有 InspectionRecordList，无法显示详情。");
            return false;
        }

        // SelectById 内部会打开面板 + 切详情 + 飞相机 + 高亮标记，
        // 和「列表里点一条」走的是同一条路径，行为不会分叉。
        if (!recordList.SelectById(dot.recordId)) return false;

        layer.Highlight(dot.recordId);
        return true;
    }

    private void DetectSceneObject()
    {
        Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance))
        {
            // 记录设备名，交给表单显示（点柜门/指示灯时归到所属机柜）
            LastHitObjectName = ResolveDeviceName(hit.collider.gameObject);

            // 记录真正命中的物体：InspectionTarget 只能沿它往上找归属设备。
            // 标记位置仍然用 hit.point（点击处的表面点），二者不要混用。
            LastHitObject = hit.collider.gameObject;

            // 如果场景中没有碰撞体，射线不会命中。
            // 命中物体后，将标记放在实际命中点附近。
            Debug.Log(
                $"命中物体：{hit.collider.gameObject.name}（设备：{LastHitObjectName}），位置：{hit.point}"
            );

            PlaceMarker(hit.point, hit.normal);
        }
        else
        {
            Debug.Log("射线未命中任何带有 Collider 的物体。");
        }
    }

    /// <summary>
    /// 由命中物体反推「所属设备名」：从自身逐级向上找，命中已知设备前缀就返回该设备。
    /// 这样点柜门(Cabinet_Door)、指示灯(Green_Light)时表单显示的都是 Cabinet_A，
    /// 而不是零件名 —— 巡检记录要落在设备上，不是落在螺丝上。
    /// </summary>
    static string ResolveDeviceName(GameObject hit)
    {
        Transform t = hit.transform.parent;
        while (t != null)
        {
            string n = t.name;
            foreach (string prefix in DevicePrefixes)
            {
                if (n.StartsWith(prefix)) return n;
            }
            t = t.parent;
        }
        return hit.name; // 没找到所属设备，就用命中的物体本身（如 Ground / wall）
    }

    private void PlaceMarker(Vector3 hitPoint, Vector3 hitNormal)
    {
        // 保证同一时刻只有一个待提交标记
        if (currentMarker != null)
        {
            Destroy(currentMarker);
        }

        currentMarkerPosition = hitPoint;

        currentMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        currentMarker.name = "PendingIssueMarker";

        // 沿表面法线稍微抬起，避免与表面重叠
        currentMarker.transform.position =
            hitPoint + hitNormal * markerSize * 0.6f;

        currentMarker.transform.localScale =
            Vector3.one * markerSize;

        // 标记不参与后续射线检测
        Collider markerCollider = currentMarker.GetComponent<Collider>();
        if (markerCollider != null)
        {
            Destroy(markerCollider);
        }

        Renderer markerRenderer =
            currentMarker.GetComponent<Renderer>();

        Shader markerShader =
            Shader.Find("Universal Render Pipeline/Unlit");

        if (markerShader == null)
        {
            markerShader =
                Shader.Find("Universal Render Pipeline/Lit");
        }

        if (markerShader == null)
        {
            markerShader = Shader.Find("Standard");
        }

        if (markerShader != null)
        {
            Material markerMaterial = new Material(markerShader);
            ApplyColor(markerMaterial, markerColor);
            markerRenderer.material = markerMaterial;
        }
        else
        {
            Debug.LogWarning("没有找到可用的标记 Shader。");
        }

        Debug.Log(
            $"已创建待提交标记，坐标：{currentMarkerPosition}"
        );

        // 通知表单显示坐标并打开（评分项 3.1：点击平面放标记后弹出填写界面）
        IsFormOpen = true;
        if (formController != null)
        {
            // 第二个参数必须是「命中物体」而不是标记球：
            // InspectionTarget.Resolve() 要从它往上找设备，标记球是新建的孤立物体，查不出归属。
            formController.OpenWithMarker(currentMarkerPosition, LastHitObject, this);
        }
        else
        {
            Debug.LogWarning("InspectionRaycaster：未绑定 IssueFormController，放置标记后无法打开表单。" +
                "请在 Main Camera 的 InspectionRaycaster 组件把 IssueFormPanel 拖到 Form Controller 字段。");
        }
    }

    public Vector3 GetCurrentMarkerPosition()
    {
        return currentMarkerPosition;
    }

    public void CancelCurrentMarker()
    {
        if (currentMarker != null)
        {
            string name = currentMarker.name;
            Destroy(currentMarker);
            currentMarker = null;
            Debug.Log("[巡检] 已销毁待提交标记：" + name + "（取消或重新放置）");
        }
        else
        {
            Debug.Log("[巡检] CancelCurrentMarker：当前没有待提交标记，无需销毁。");
        }

        currentMarkerPosition = Vector3.zero;
    }

    /// <summary>
    /// 提交成功：确认记录，标记保留在场景中。
    ///
    /// 关键变化：标记现在必须**记住这条记录的 id**。
    /// 以前只是把球改名 + 涂绿，球和记录之间没有任何关系 —— 列表点某条记录时，
    /// 代码根本不知道哪个球是它，也就没法高亮。
    ///
    /// 拿到 id 就交给 IssueMarkerLayer 收编（球从此属于「记录的可视化」，
    /// 不再是本次会话的临时产物）；颜色按记录状态来，新提交的记录是 open → 红色，
    /// 改状态后 Sync 会把它刷成橙/绿。拿不到 id（老调用）就退回原来的"变绿留在场景里"。
    /// </summary>
    public void ConfirmCurrentMarker()
    {
        ConfirmCurrentMarker(null, null, null);
    }

    public void ConfirmCurrentMarker(string recordId, string targetId, string status)
    {
        ConfirmCurrentMarker(recordId, targetId, status, "");
    }

    public void ConfirmCurrentMarker(string recordId, string targetId, string status, string priority)
    {
        // 走不到这里 = 表单持有的射线脚本引用为空，标记既不会变绿也不会被确认，
        // 下次点击场景时还会把它当成「旧标记」销毁 —— 表现就是「标记消失了」。
        if (currentMarker == null)
        {
            Debug.LogWarning("[巡检] ConfirmCurrentMarker 被调用，但当前没有待提交标记。" +
                             "说明 IssueFormController 拿到的 raycaster 引用为空，标记不会被确认。");
            return;
        }

        GameObject marker = currentMarker;
        Renderer markerRenderer = marker.GetComponent<Renderer>();
        Material m = markerRenderer != null ? markerRenderer.material : null;

        // 先把手上的引用清掉：从这一刻起它不再是「待提交标记」，
        // 后续点击新位置不会把它当旧标记销毁。
        currentMarker = null;
        currentMarkerPosition = Vector3.zero;

        if (!string.IsNullOrEmpty(recordId) && m != null)
        {
            string st = string.IsNullOrEmpty(status) ? "open" : status;

            marker.name = "IssueMarker_" + (recordId.Length <= 8 ? recordId : recordId.Substring(recordId.Length - 8));

            // 尺寸不再这里写死 1.25 倍：交给 Adopt 按优先级算，和 Sync 生成的球口径一致
            IssueMarkerLayer layer = IssueMarkerLayer.Ensure();
            IssueMarkerDot dot = layer.Adopt(marker, recordId, targetId, st, priority);

            // 刚提交的那条立刻闪一下：场景里点一下就有反应，比只改颜色更容易被注意到
            if (dot != null) layer.Highlight(recordId);

            Debug.Log("[巡检] 已确认巡检记录，标记已绑定记录 id=" + recordId +
                      "、设备=" + targetId + "、状态=" + st +
                      "（颜色 " + (dot != null ? dot.baseColor.ToString() : "无") + "）");
        }
        else
        {
            // 老路径：拿不到 id 时至少把球涂绿留在场景里，保证「提交有可见结果」
            marker.name = "SubmittedIssueMarker";

            if (m != null)
            {
                ApplyColor(m, submittedColor);
                Debug.Log("[巡检] 标记上色（未拿到记录 id）：目标=" + submittedColor
                          + " / shader=" + m.shader.name
                          + " / 回读 _BaseColor=" + m.GetColor("_BaseColor"));
            }
            else
            {
                Debug.LogWarning("[巡检] 标记上没有材质，无法变绿。");
            }

            marker.transform.localScale = Vector3.one * markerSize * 1.35f;

            Debug.LogWarning("[巡检] 提交成功但没拿到记录 id，标记无法与记录关联（列表点选不会高亮它）。");
        }

        // 通知列表面板：刷新数据 + 同步标记 + 退出上报模式
        if (recordList != null)
        {
            recordList.OnIssueSubmitted(recordId);
        }
    }

    /// <summary>
    /// 给标记材质上色。
    ///
    /// 坑：URP 的 shader（Universal Render Pipeline/Lit 和 /Unlit）颜色属性叫 _BaseColor，
    /// 而 Material.color 写的是 _Color —— 在 URP 下用 material.color 赋值会静默失效，
    /// 表现就是「后端提交成功了，但标记没变绿」。所以这里两个名字都写一遍：
    /// 不存在的属性 Unity 会忽略，不会报错。
    /// </summary>
    static void ApplyColor(Material material, Color color)
    {
        if (material == null) return;

        material.SetColor("_BaseColor", color);   // URP：Lit / Unlit
        material.SetColor("_Color", color);       // 内置 Standard / Unlit 等
    }

    /// <summary>兼容旧调用：行为等同确认（保留标记），不做删除。</summary>
    public void CompleteCurrentMarker()
    {
        ConfirmCurrentMarker();
    }
}
