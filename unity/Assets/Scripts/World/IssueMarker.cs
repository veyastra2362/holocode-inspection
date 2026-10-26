using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 场景中的问题标记。
    ///
    /// 关键点：标记是**子节点**，父节点放在射线命中的**精确位置**上，
    /// 而可见的球体朝相机方向偏移一点，避免一半埋进墙面导致看不见。
    /// 这样“上报的坐标”= 射线命中点（题目要求），而不是被偏移后的装饰球心。
    /// </summary>
    public class IssueMarker : MonoBehaviour
    {
        [Header("外观")]
        public float CoreRadius = 0.09f;

        /// <summary>标记绑定的问题数据（未提交的临时标记为 null）。</summary>
        public IssueData Issue { get; private set; }

        /// <summary>是否已成功上报到后端。</summary>
        public bool IsSaved { get; private set; }

        /// <summary>射线命中的精确世界坐标（= 本对象位置，未被装饰偏移）。</summary>
        public Vector3 HitPoint => transform.position;

        private Renderer _core;
        private Renderer _pointer;
        private MaterialPropertyBlock _mpb;
        private bool _pulsing;
        private float _pulseTime;
        private Vector3 _normal = Vector3.up;

        /// <summary>
        /// 构建标记外观。用基础几何体（球 + 柱）保证任何平台都能显示，不依赖美术资源。
        /// </summary>
        public void Build(bool isTemp)
        {
            _mpb = new MaterialPropertyBlock();

            // 核心球：自发光、亮色，便于在灰暗厂房里被一眼发现
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Core";
            sphere.transform.SetParent(transform, false);
            sphere.transform.localScale = Vector3.one * CoreRadius * 2f;
            Object.Destroy(sphere.GetComponent<Collider>()); // 标记不参与射线命中，避免遮挡设备
            _core = sphere.GetComponent<Renderer>();
            _core.sharedMaterial = CreateMarkerMaterial();

            // 一根短“指针”，沿表面法线朝外延伸，用来强调“问题就在这个位置”
            var pointer = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pointer.name = "Pointer";
            pointer.transform.SetParent(transform, false);
            pointer.transform.localScale = new Vector3(0.012f, 0.10f, 0.012f);
            pointer.transform.localPosition = _normal * 0.10f;
            // Cylinder 默认沿 Y 轴；转到法线方向
            pointer.transform.localRotation = Quaternion.FromToRotation(Vector3.up, _normal);
            Object.Destroy(pointer.GetComponent<Collider>());
            _pointer = pointer.GetComponent<Renderer>();
            _pointer.sharedMaterial = CreateMarkerMaterial();

            // 核心球沿法线轻轻抬起，避免一半埋进墙面/地面
            _core.transform.localPosition = _normal * (CoreRadius * 0.85f);

            SetColor(isTemp ? new Color(1f, 0.85f, 0.2f) : new Color(0.2f, 0.9f, 1f));

            _pulsing = isTemp;
            name = isTemp ? "TempMarker" : "IssueMarker";
        }

        /// <summary>设置标记所贴表面的法线，需在 Build 之前调用。</summary>
        public void SetSurfaceNormal(Vector3 normal)
        {
            _normal = normal.sqrMagnitude < 1e-6f ? Vector3.up : normal.normalized;
        }

        private static Material CreateMarkerMaterial()
        {
            // Unlit/Color 在 Built-in 与 URP 下都可用，且不受场景光照影响。
            var shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            return new Material(shader) { color = Color.white };
        }

        public void SetColor(Color c)
        {
            if (_mpb == null)
            {
                _mpb = new MaterialPropertyBlock();
            }
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_Color", c);
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>按优先级/状态刷新颜色：已解决=绿，处理中=黄，待处理=按优先级。</summary>
        public void ApplyIssueStyle()
        {
            if (Issue == null)
            {
                return;
            }

            if (Issue.status == IssueEnums.ToWire(IssueStatus.Resolved))
            {
                SetColor(new Color(0.30f, 0.95f, 0.40f));
                _pulsing = false;
                return;
            }

            if (Issue.status == IssueEnums.ToWire(IssueStatus.InProgress))
            {
                SetColor(new Color(1f, 0.75f, 0.15f));
                _pulsing = false;
                return;
            }

            switch (Issue.priority)
            {
                case "high":
                    SetColor(new Color(1f, 0.25f, 0.25f));
                    _pulsing = true; // 高优先级闪烁，现场容易注意到
                    break;
                case "low":
                    SetColor(new Color(0.55f, 0.75f, 1f));
                    _pulsing = false;
                    break;
                default:
                    SetColor(new Color(1f, 0.55f, 0.15f));
                    _pulsing = false;
                    break;
            }
        }

        public void Bind(IssueData issue, bool saved)
        {
            Issue = issue;
            IsSaved = saved;
            _pulsing = false;
            ApplyIssueStyle();
        }

        private void Update()
        {
            if (!_pulsing || _core == null)
            {
                return;
            }
            _pulseTime += Time.deltaTime;
            var s = 1f + 0.18f * Mathf.Sin(_pulseTime * 5f);
            _core.transform.localScale = Vector3.one * CoreRadius * 2f * s;
        }

        /// <summary>当前显示的颜色（供测试断言样式随状态/优先级变化）。</summary>
        public Color CurrentColor
        {
            get
            {
                if (_core == null)
                {
                    return Color.clear;
                }
                var block = new MaterialPropertyBlock();
                _core.GetPropertyBlock(block);
                return block.GetColor("_Color");
            }
        }
    }
}
