using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 第一人称“虚拟巡检员”：WASD 平移 + 右键拖拽转视角 + 滚轮推拉。
    ///
    /// 层级结构：
    ///   PlayerController (本脚本，位置=脚底)
    ///     └── CameraPivot (yaw 旋转 + 眼高)
    ///           └── Main Camera (pitch 旋转)
    ///
    /// 这里不继承 CharacterController / Rigidbody：巡检只是观察，不需要碰撞推挤，
    /// 用 transform 位移 + 可选的地面约束最省心，也更容易在没装物理模块时运行。
    /// </summary>
    [RequireComponent(typeof(InspectionInput))]
    public class PlayerController : MonoBehaviour
    {
        [Tooltip("眼高（米）")]
        public float EyeHeight = 1.65f;

        [Tooltip("活动范围（x/z 半边长），避免走出场景")]
        public float Bounds = 24f;

        [Tooltip("是否保持在地平面以上")]
        public bool ClampToGround = true;

        public Transform CameraPivot { get; private set; }
        public Camera ViewCamera { get; private set; }

        private InspectionInput _input;

        public void Configure(Transform pivot, Camera cam)
        {
            CameraPivot = pivot;
            ViewCamera = cam;
        }

        private void Awake()
        {
            _input = GetComponent<InspectionInput>();
            if (CameraPivot == null)
            {
                CameraPivot = transform.Find("CameraPivot");
            }
            if (ViewCamera == null && CameraPivot != null)
            {
                ViewCamera = CameraPivot.GetComponentInChildren<Camera>();
            }
        }

        private void Start()
        {
            _input.InitializeLook(CameraPivot);
        }

        private void Update()
        {
            if (_input == null || CameraPivot == null)
            {
                return;
            }

            ApplyLook();
            ApplyMove();
        }

        private void ApplyLook()
        {
            // yaw 作用于 pivot，pitch 作用于相机自身，避免万向锁与翻滚
            CameraPivot.localRotation = Quaternion.Euler(0f, _input.Yaw, 0f);
            if (ViewCamera != null)
            {
                ViewCamera.transform.localRotation = Quaternion.Euler(_input.Pitch, 0f, 0f);
            }
        }

        private void ApplyMove()
        {
            var wish = _input.ConsumeMoveWish();
            var zoom = _input.ConsumeZoom();

            var speed = _input.MoveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                speed *= _input.MoveSpeedFastMultiplier;
            }

            var delta = Vector3.zero;
            if (wish.sqrMagnitude > 0.0001f)
            {
                // 前后沿视线水平方向，左右沿右方向
                var forward = Quaternion.Euler(0f, _input.Yaw, 0f) * Vector3.forward;
                var right = Quaternion.Euler(0f, _input.Yaw, 0f) * Vector3.right;
                delta = (forward * wish.z + right * wish.x).normalized * speed * Time.deltaTime;
            }

            if (Mathf.Abs(zoom) > 0.0001f)
            {
                var forward = Quaternion.Euler(0f, _input.Yaw, 0f) * Vector3.forward;
                delta += forward * (zoom * speed * 4f);
            }

            if (delta == Vector3.zero)
            {
                return;
            }

            var next = transform.position + delta;
            next.x = Mathf.Clamp(next.x, -Bounds, Bounds);
            next.z = Mathf.Clamp(next.z, -Bounds, Bounds);
            if (ClampToGround)
            {
                next.y = 0f;
            }
            transform.position = next;
        }

        /// <summary>把巡检员放到指定位置与朝向（切换区域/回到起始点时使用）。</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            transform.position = position;
            if (_input != null)
            {
                _input.InitializeLook(CameraPivot);
            }
            CameraPivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }
}
