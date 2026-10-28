
using UnityEngine;
using UnityEngine.EventSystems;

public class InspectionCameraController : MonoBehaviour
{
    [Header("移动设置")]
    public float moveSpeed = 4f;
    public float sprintMultiplier = 2.5f;
    public float verticalSpeed = 3f;

    [Header("视角设置")]
    public float mouseSensitivity = 2f;
    public float minPitch = -80f;
    public float maxPitch = 80f;

    private float yaw;
    private float pitch;

    private void Start()
    {
        Vector3 angles = transform.eulerAngles;

        yaw = angles.y;
        pitch = angles.x;

        if (pitch > 180f)
            pitch -= 360f;

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        Debug.Log("摄像机控制脚本已启动。");
        Debug.Log("WASD移动，Q/E升降，右键拖动转视角，Shift加速。");
    }

    private void Update()
    {
        HandleLook();
        HandleMovement();
    }

    private void HandleLook()
    {
        // 只有按住鼠标右键时才转动视角
        if (!Input.GetMouseButton(1))
            return;

        // 鼠标位于UI上时不转动视角
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject())
            return;

        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void HandleMovement()
    {
        // 点击输入框或按钮时，不移动摄像机
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject())
            return;

        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");

        Vector3 forward = transform.forward;
        Vector3 right = transform.right;

        // 保持WASD在水平地面上移动，不随抬头低头改变高度
        forward.y = 0f;
        right.y = 0f;

        forward.Normalize();
        right.Normalize();

        Vector3 move =
            forward * vertical +
            right * horizontal;

        if (move.sqrMagnitude > 1f)
            move.Normalize();

        float speed = moveSpeed;

        if (Input.GetKey(KeyCode.LeftShift) ||
            Input.GetKey(KeyCode.RightShift))
        {
            speed *= sprintMultiplier;
        }

        transform.position += move * speed * Time.deltaTime;

        // Q下降，E上升
        if (Input.GetKey(KeyCode.Q))
        {
            transform.position +=
                Vector3.down * verticalSpeed * Time.deltaTime;
        }

        if (Input.GetKey(KeyCode.E))
        {
            transform.position +=
                Vector3.up * verticalSpeed * Time.deltaTime;
        }
    }
}
