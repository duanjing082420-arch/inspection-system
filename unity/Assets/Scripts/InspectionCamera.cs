using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 巡检视角控制器：Play 模式下 WASD 平移、Q/E 升降、按住鼠标右键拖动转视角。
/// 表单打开时自动锁定，避免填表时镜头乱跑。
/// </summary>
[RequireComponent(typeof(Camera))]
public class InspectionCamera : MonoBehaviour
{
    [Header("平移")]
    public float moveSpeed = 4f;
    public float sprintMultiplier = 2.5f;
    public float verticalSpeed = 2f;

    [Header("转视角")]
    public float mouseSensitivity = 3f;
    public float minPitch = -60f;
    public float maxPitch = 75f;

    [Header("活动范围（防止走出厂房）")]
    public bool clampArea = true;
    public Vector3 areaMin = new Vector3(-9.5f, 0.6f, -6.5f);
    public Vector3 areaMax = new Vector3(9.5f, 5.0f, 6.5f);

    [Header("引用（留空自动查找）")]
    public InspectionRaycaster raycaster;

    [Header("自动归位（仅当相机贴在地面时生效）")]
    public bool autoPlaceIfOnGround = true;
    public Vector3 fallbackPosition = new Vector3(0f, 1.8f, -6.2f);
    public float groundThreshold = 0.5f;

    [Header("聚焦（列表点记录 / 点设备时飞到目标）")]
    [Tooltip("默认飞行距离（米）")]
    public float focusDistance = 3.4f;
    [Tooltip("飞行时长（秒）")]
    public float focusDuration = 0.45f;

    private float yaw;
    private float pitch;

    // 正在飞行：这段时间屏蔽 WASD / 右键，否则用户手一动就和补间打架，镜头会抖。
    private bool focusing;
    private Coroutine focusRoutine;

    private void Awake()
    {
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = NormalizeAngle(e.x);

        if (raycaster == null)
            raycaster = GetComponent<InspectionRaycaster>();
    }

    private void Start()
    {
        // 相机若停在 y≈0（和地面同高），Play 后视野里只有地板，机柜全被地面挡住，
        // 表现出来就像「按什么都没反应」。这里在运行时自动抬到人眼高度并摆正，
        // 只在确实贴地时才动，用户自己摆好的机位不会被覆盖。
        if (autoPlaceIfOnGround && transform.position.y < groundThreshold)
        {
            Debug.Log("[巡检] 相机贴地（y=" + transform.position.y.ToString("F2") +
                      "），已自动归位到 " + fallbackPosition + "。");
            transform.position = fallbackPosition;
            transform.rotation = Quaternion.identity;
            yaw = 0f;
            pitch = 0f;
        }
    }

    private void Update()
    {
        // 表单打开时锁死视角
        if (raycaster != null && raycaster.IsFormOpen)
            return;

        // 正在飞向目标：这一小段时间接管镜头，别和补间抢
        if (focusing)
            return;

        HandleLook();
        HandleMove();
    }

    /// <summary>
    /// 飞到某个世界坐标看它。
    ///
    /// screenOffsetX 是「希望目标落在屏幕横向什么位置」，-1=最左、0=正中、+1=最右。
    /// 这个参数不是花活：巡检记录面板占了屏幕中间一大块，目标若居中就正好被面板挡住，
    /// 用户点了列表只会听到"飞过去了"但什么都看不见。传 0.6~0.7 把目标送到面板右侧空白区。
    ///
    /// 实现要点：要让目标偏离画面中心，必须**让相机不再正对目标**（转一个角度），
    /// 而不是把相机往旁边挪 —— 挪了位置仍然 LookAt，目标还是居中。
    /// </summary>
    public void FocusOn(Vector3 worldTarget, float distance = -1f, float screenOffsetX = 0f)
    {
        if (distance <= 0f) distance = focusDistance;

        Camera cam = GetComponent<Camera>();
        if (cam == null) return;

        if (focusRoutine != null) StopCoroutine(focusRoutine);

        // 站到目标斜上方：保持当前的水平朝向，把俯角压平一些，避免贴着地面看
        Vector3 dir = transform.position - worldTarget;
        dir.y *= 0.35f;

        if (dir.sqrMagnitude < 0.05f)
            dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;

        dir.Normalize();

        Vector3 targetPos = worldTarget + dir * distance;

        if (clampArea)
        {
            targetPos.x = Mathf.Clamp(targetPos.x, areaMin.x, areaMax.x);
            targetPos.y = Mathf.Clamp(targetPos.y, areaMin.y, areaMax.y);
            targetPos.z = Mathf.Clamp(targetPos.z, areaMin.z, areaMax.z);
        }

        Vector3 toTarget = worldTarget - targetPos;
        if (toTarget.sqrMagnitude < 0.0001f) return;

        Quaternion baseRot = Quaternion.LookRotation(toTarget, Vector3.up);

        // 把「屏幕偏移比例」换算成相机需要偏转的角度：
        //   tan(θ) = 偏移比例 × tan(fov/2) × 宽高比
        // 正偏移（目标要在右边）= 镜头往左转 = 绕本地 Y 轴负方向转。
        if (!Mathf.Approximately(screenOffsetX, 0f))
        {
            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float tanTheta = Mathf.Abs(screenOffsetX) * Mathf.Tan(halfFov) * Mathf.Max(cam.aspect, 0.1f);
            float theta = Mathf.Atan(tanTheta) * Mathf.Rad2Deg;

            baseRot = baseRot * Quaternion.Euler(0f, screenOffsetX > 0f ? -theta : theta, 0f);
        }

        focusRoutine = StartCoroutine(FocusRoutine(targetPos, baseRot, focusDuration));
    }

    private System.Collections.IEnumerator FocusRoutine(Vector3 targetPos, Quaternion targetRot, float duration)
    {
        focusing = true;

        Vector3 startPos = transform.position;
        Quaternion startRot = transform.rotation;

        float t = 0f;

        while (t < duration)
        {
            // 用 unscaled 时间：表单/面板可能把 timescale 改过，聚焦不该被拖慢
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / duration);

            // SmoothStep：起步和收尾都减速，比线性插值自然得多
            float s = k * k * (3f - 2f * k);

            transform.position = Vector3.Lerp(startPos, targetPos, s);
            transform.rotation = Quaternion.Slerp(startRot, targetRot, s);

            yield return null;
        }

        transform.position = targetPos;
        transform.rotation = targetRot;

        // 把最终朝向拆回 yaw/pitch：否则飞行结束后一按右键，镜头会瞬间跳回旧角度
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = NormalizeAngle(e.x);

        focusing = false;
        focusRoutine = null;
    }

    /// <summary>正在进行聚焦飞行吗（外部可据此决定要不要暂停别的输入）。</summary>
    public bool IsFocusing { get { return focusing; } }

    /// <summary>鼠标停在 UI（输入框 / 按钮）上时返回 true —— 此时不该动镜头。</summary>
    static bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private void HandleLook()
    {
        if (!Input.GetMouseButton(1))
            return;

        // 在 UI 上拖拽（比如下拉框、滚动条）时不要同时转视角
        if (PointerOverUI())
            return;

        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void HandleMove()
    {
        // 焦点在输入框里时不移动，否则一边打字一边镜头乱跑
        if (PointerOverUI())
            return;

        float h = 0f;
        float v = 0f;
        float up = 0f;

        if (Input.GetKey(KeyCode.A)) h -= 1f;
        if (Input.GetKey(KeyCode.D)) h += 1f;
        if (Input.GetKey(KeyCode.S)) v -= 1f;
        if (Input.GetKey(KeyCode.W)) v += 1f;
        if (Input.GetKey(KeyCode.Q)) up -= 1f;
        if (Input.GetKey(KeyCode.E)) up += 1f;

        if (h == 0f && v == 0f && up == 0f)
            return;

        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);

        Vector3 flatForward = transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
        else flatForward.Normalize();

        Vector3 flatRight = transform.right;
        flatRight.y = 0f;
        if (flatRight.sqrMagnitude < 0.0001f) flatRight = Vector3.right;
        else flatRight.Normalize();

        Vector3 delta = flatRight * h + flatForward * v;
        if (delta.sqrMagnitude > 1f) delta.Normalize();

        Vector3 p = transform.position
                    + delta * (speed * Time.deltaTime)
                    + Vector3.up * (up * verticalSpeed * Time.deltaTime);

        if (clampArea)
        {
            p.x = Mathf.Clamp(p.x, areaMin.x, areaMax.x);
            p.y = Mathf.Clamp(p.y, areaMin.y, areaMax.y);
            p.z = Mathf.Clamp(p.z, areaMin.z, areaMax.z);
        }

        transform.position = p;
    }

    private static float NormalizeAngle(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        return a;
    }
}
