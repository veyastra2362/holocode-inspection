using System;
using UnityEngine;

namespace Inspection
{
    public enum PointerButton
    {
        None,
        Left,
        Right
    }

    /// <summary>
    /// 集中封装鼠标/键盘输入。
    ///
    /// 设计要点（也是本作最容易踩坑的地方）：
    ///   - 使用旧版 Input Manager（Project 里 Active Input Handling = Input Manager (Old)），
    ///     这样不需要额外的 Input System 包也能在桌面端稳定运行；
    ///   - 用一个“本次按下后累计移动了多少像素”的阈值来区分「点击」和「拖拽转视角」。
    ///     否则用户想转视角时按住左键一拖，松手那一下就会被当成点击并放一个标记；
    ///   - 同一帧内只允许一次“按下/抬起”语义，避免 UI 与场景同时响应。
    /// </summary>
    public class InspectionInput : MonoBehaviour
    {
        [Tooltip("按下后移动超过该像素数则视为拖拽（不会触发点击放置标记）")]
        public float DragThresholdPixels = 6f;

        [Tooltip("最长的点击判定时长（秒）。按住不放不算点击。")]
        public float MaxClickDuration = 0.6f;

        [Tooltip("拖拽/键盘移动的灵敏度")]
        public float LookSensitivity = 2.4f;
        public float MoveSpeed = 4.5f;
        public float MoveSpeedFastMultiplier = 2.5f;
        public float PitchMin = -75f;
        public float PitchMax = 80f;

        /// <summary>本帧是否发生了“按下”（只报告一次）。</summary>
        public bool PressedThisFrame { get; private set; }
        /// <summary>按下时的屏幕位置。</summary>
        public Vector2 PressPosition { get; private set; }
        /// <summary>按下时按下的键。</summary>
        public PointerButton PressedButton { get; private set; }

        /// <summary>本帧是否发生了“完整点击”（按下在阈值内抬起、时间够短、同一按键）。</summary>
        public bool ClickedThisFrame { get; private set; }
        public Vector2 ClickScreenPosition { get; private set; }
        public PointerButton ClickedButton { get; private set; }

        /// <summary>本帧是否处于拖拽中（用于屏蔽点击）。</summary>
        public bool IsDragging { get; private set; }

        /// <summary>
        /// 外部（UI）可以设置该标志，使指针位于 UI 上时不处理场景输入。
        /// 由 InspectionHUD 在每帧根据 EventSystem 的结果写回。
        /// </summary>
        public bool PointerOverUI { get; set; }

        /// <summary>当用户输入被 UI 拦截时，方便在调试面板里说明原因。</summary>
        public string LastBlockReason { get; private set; } = string.Empty;

        private bool _tracking;
        private PointerButton _trackingButton;
        private Vector2 _pressPos;
        private Vector2 _lastPos;
        private float _pressTime;
        private float _accumulatedDrag;
        private float _yaw;
        private float _pitch;
        private bool _lookInitialized;

        public float Yaw => _yaw;
        public float Pitch => _pitch;

        /// <summary>用相机当前朝向初始化 yaw/pitch，避免第一帧跳变。</summary>
        public void InitializeLook(Transform pivot)
        {
            if (pivot == null || _lookInitialized)
            {
                return;
            }
            var e = pivot.eulerAngles;
            _pitch = Mathf.DeltaAngle(0f, e.x);
            _yaw = e.y;
            _lookInitialized = true;
        }

        private void Update()
        {
            PressedThisFrame = false;
            ClickedThisFrame = false;
            PressedButton = PointerButton.None;
            ClickedButton = PointerButton.None;
            IsDragging = false;
            LastBlockReason = string.Empty;

            // ---- 1. 视角：右键或中键拖拽 ----
            UpdateDragLook();

            // ---- 2. 键盘移动（WASD / 方向键 / Shift 加速） ----
            UpdateKeyboardMove();

            // ---- 3. 点击检测：左键按下-抬起且未被判定为拖拽 ----
            UpdateClickDetection();
        }

        private void UpdateDragLook()
        {
            var lookHeld = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (!lookHeld)
            {
                return;
            }

            if (PointerOverUI && !_tracking)
            {
                LastBlockReason = "指针在 UI 上，忽略视角拖拽";
                return;
            }

            var dx = Input.GetAxisRaw("Mouse X");
            var dy = Input.GetAxisRaw("Mouse Y");
            if (Mathf.Abs(dx) > 0.0001f || Mathf.Abs(dy) > 0.0001f)
            {
                _yaw += dx * LookSensitivity;
                _pitch = Mathf.Clamp(_pitch - dy * LookSensitivity, PitchMin, PitchMax);
            }

            // 用滚轮微调视野距离（相当于前后走）
            var scroll = Input.GetAxisRaw("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                // 交给 PlayerController 处理（它持有 head 节点）
                PendingZoom += scroll;
            }
        }

        /// <summary>滚轮累计值，由 PlayerController 每帧取走一次。</summary>
        public float PendingZoom { get; private set; }

        public float ConsumeZoom()
        {
            var z = PendingZoom;
            PendingZoom = 0f;
            return z;
        }

        private Vector3 _moveWish;

        public Vector3 ConsumeMoveWish()
        {
            var w = _moveWish;
            _moveWish = Vector3.zero;
            return w;
        }

        private void UpdateKeyboardMove()
        {
            // 焦点在输入框里时不移动，避免打字时角色乱走
            if (PointerOverUI == false && GuiFocusGuard.IsTyping)
            {
                return;
            }

            var x = 0f;
            var z = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            _moveWish = new Vector3(x, 0f, z);
        }

        private void UpdateClickDetection()
        {
            var leftDown = Input.GetMouseButtonDown(0);
            var leftUp = Input.GetMouseButtonUp(0);

            if (leftDown)
            {
                // 指针在 UI 上：既不转视角，也不能当作场景点击
                if (PointerOverUI)
                {
                    LastBlockReason = "指针在 UI 上，忽略点击";
                    _tracking = false;
                    return;
                }

                _tracking = true;
                _trackingButton = PointerButton.Left;
                _pressPos = Input.mousePosition;
                _lastPos = _pressPos;
                _pressTime = Time.unscaledTime;
                _accumulatedDrag = 0f;

                PressedThisFrame = true;
                PressedButton = PointerButton.Left;
                PressPosition = _pressPos;
                return;
            }

            if (!_tracking)
            {
                return;
            }

            var pos = (Vector2)Input.mousePosition;
            _accumulatedDrag += (pos - _lastPos).magnitude;
            _lastPos = pos;
            if (_accumulatedDrag > DragThresholdPixels)
            {
                IsDragging = true;
            }

            if (leftUp)
            {
                var held = Time.unscaledTime - _pressTime;
                // 三个条件同时满足才算一次有效点击：位移小、时间短、不是拖拽
                if (!IsDragging && held <= MaxClickDuration && PointerOverUI == false)
                {
                    ClickedThisFrame = true;
                    ClickedButton = _trackingButton;
                    ClickScreenPosition = pos;
                }
                else if (IsDragging)
                {
                    LastBlockReason = "拖拽结束，不放置标记";
                }
                _tracking = false;
            }
        }

        /// <summary>
        /// 测试/自动化用：直接注入一次“点击”，跳过真实鼠标。
        /// </summary>
        public void SimulateClick(Vector2 screenPos)
        {
            ClickedThisFrame = true;
            ClickedButton = PointerButton.Left;
            ClickScreenPosition = screenPos;
        }
    }

    /// <summary>
    /// 记录当前是否有 uGUI 输入框获得焦点。
    /// 在 Update 中读取键盘时（例如 WASD 移动）用它避免“打字时摄像机乱飞”。
    /// </summary>
    public static class GuiFocusGuard
    {
        public static bool IsTyping { get; set; }
    }
}
