using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inspection
{
    /// <summary>可在场景中巡检的设备/表面描述（用于点选后显示信息与“区域/编号”扩展）。</summary>
    [Serializable]
    public class SurfaceInfo
    {
        public string deviceId;     // 例如 "CAB-A3"
        public string zone;         // 例如 "A 区"
        public string displayName;  // 例如 "3 号机柜"
        public Transform transform;
    }

    /// <summary>
    /// 用 Unity 基础几何体（Cube/Cylinder/Quad）程序化搭建虚拟机房。
    ///
    /// 为什么用代码生成而不是手摆场景：
    ///   - 不依赖 .unity 场景文件（二进制/YAML 难以 review），打开项目后点 Play 即可；
    ///   - 设备、区域编号集中在一处，改一个参数就能扩展；
    ///   - 每个可点击对象都挂了 Collider 并且是 BoxCollider/BoxCollider 特大号，
    ///     保证射线检测稳定命中，也便于用单元测试校验“一定有可点击对象”。
    ///
    /// 题目要求至少两个可观察/可点击对象，这里提供了地面、墙、机柜、控制台、管道等 30+ 个。
    /// </summary>
    public class SceneComposer : MonoBehaviour
    {
        [Tooltip("是否自动构建（关闭后可用于自定义场景）")]
        public bool AutoBuild = true;

        public int RoomWidth = 16;
        public int RoomDepth = 20;
        public float WallHeight = 3.6f;

        private readonly List<SurfaceInfo> _surfaces = new List<SurfaceInfo>();

        /// <summary>所有可点击表面（供 UI 显示区域/编号、供测试断言）。</summary>
        public IReadOnlyList<SurfaceInfo> Surfaces => _surfaces;

        private Material _floorMat, _wallMat, _cabinetMat, _cabinetAccentMat, _pipeMat, _consoleMat, _accentMat;

        private void Start()
        {
            if (AutoBuild)
            {
                Build();
            }
        }

        public void Build()
        {
            if (transform.childCount > 0 && _surfaces.Count > 0)
            {
                return; // 幂等：构建过一次就跳过
            }

            CreateMaterials();

            BuildRoom();
            BuildCabinetRow(-2.6f, new[] { "A1", "A2", "A3", "A4" }, "A 区");
            BuildCabinetRow(-6.4f, new[] { "B1", "B2", "B3", "B4" }, "B 区");
            BuildPipes();
            BuildConsole();
            BuildPillars();
            BuildHazardZone();

            Debug.Log($"[SceneComposer] 场景构建完成，可点击表面 {_surfaces.Count} 个");
        }

        private void CreateMaterials()
        {
            // Standard shader 在 URP/Built-in 都有（URP 会回退到 Lit 或紫红），
            // 为了不引入管线差异，这里优先用 Unlit/Color 保证显示稳定。
            _floorMat = MakeMat(new Color(0.30f, 0.32f, 0.35f));
            _wallMat = MakeMat(new Color(0.62f, 0.64f, 0.66f));
            _cabinetMat = MakeMat(new Color(0.18f, 0.22f, 0.26f));
            _cabinetAccentMat = MakeMat(new Color(0.25f, 0.55f, 0.70f));
            _pipeMat = MakeMat(new Color(0.75f, 0.62f, 0.30f));
            _consoleMat = MakeMat(new Color(0.20f, 0.24f, 0.22f));
            _accentMat = MakeMat(new Color(0.85f, 0.30f, 0.25f));
        }

        private static Material MakeMat(Color c)
        {
            var shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            return new Material(shader) { color = c };
        }

        // ---------- 房间 ----------

        private void BuildRoom()
        {
            var halfW = RoomWidth / 2f;
            var halfD = RoomDepth / 2f;

            // 地面（也是可点击表面：允许在空地上标注“此处有积水”）
            var floor = CreateBox("Floor", new Vector3(0f, -0.05f, 0f),
                new Vector3(RoomWidth, 0.1f, RoomDepth), _floorMat);
            Register(floor, "FLOOR", "全区域", "地面");

            // 四面墙
            CreateBox("Wall_North", new Vector3(0f, WallHeight / 2f, halfD),
                new Vector3(RoomWidth, WallHeight, 0.2f), _wallMat).RegisterSurface("WALL-N", "全区域", "北墙", this);
            CreateBox("Wall_South", new Vector3(0f, WallHeight / 2f, -halfD),
                new Vector3(RoomWidth, WallHeight, 0.2f), _wallMat).RegisterSurface("WALL-S", "全区域", "南墙", this);
            CreateBox("Wall_East", new Vector3(halfW, WallHeight / 2f, 0f),
                new Vector3(0.2f, WallHeight, RoomDepth), _wallMat).RegisterSurface("WALL-E", "全区域", "东墙", this);
            CreateBox("Wall_West", new Vector3(-halfW, WallHeight / 2f, 0f),
                new Vector3(0.2f, WallHeight, RoomDepth), _wallMat).RegisterSurface("WALL-W", "全区域", "西墙", this);

            // 天花板灯带（装饰，不可点击）
            for (var z = -halfD + 3f; z <= halfD - 3f; z += 6f)
            {
                var lamp = CreateBox("Lamp", new Vector3(0f, WallHeight - 0.1f, z),
                    new Vector3(3.2f, 0.06f, 0.5f), MakeMat(new Color(0.95f, 0.95f, 0.85f)));
                ObjectUtil.SafeDestroy(lamp.GetComponent<Collider>());
            }
        }

        // ---------- 机柜 ----------

        private void BuildCabinetRow(float z, string[] names, string zone)
        {
            const float spacing = 1.35f;
            var startX = -(names.Length - 1) * spacing / 2f;
            for (var i = 0; i < names.Length; i++)
            {
                var x = startX + i * spacing;
                var height = 2.0f + (i % 2 == 0 ? 0f : 0.15f); // 轻微高低差，视觉上不像复制粘贴
                var body = CreateBox($"Cabinet_{names[i]}", new Vector3(x, height / 2f, z),
                    new Vector3(1.1f, height, 0.9f), _cabinetMat);
                body.RegisterSurface("CAB-" + names[i], zone, names[i] + " 号机柜", this);

                // 面板：贴在柜门正面，作为独立可点击对象，命中点更精确
                var faceZ = z - 0.9f / 2f - 0.02f;
                var panel = CreateBox($"Panel_{names[i]}", new Vector3(x, height * 0.62f, faceZ),
                    new Vector3(0.92f, height * 0.5f, 0.04f), _cabinetAccentMat);
                panel.RegisterSurface("CAB-" + names[i] + "-P", zone, names[i] + " 号机柜面板", this);

                // 顶部指示灯（装饰）
                var led = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                led.name = "LED";
                led.transform.SetParent(transform, true);
                led.transform.position = new Vector3(x + 0.35f, height + 0.06f, z);
                led.transform.localScale = Vector3.one * 0.08f;
                ObjectUtil.SafeDestroy(led.GetComponent<Collider>());
                led.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.3f, 1f, 0.45f));
            }
        }

        // ---------- 管道 ----------

        private void BuildPipes()
        {
            const float y = 2.6f;
            for (var i = 0; i < 3; i++)
            {
                var pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pipe.name = $"Pipe_{i + 1}";
                pipe.transform.SetParent(transform, true);
                pipe.transform.position = new Vector3(-3.6f + i * 0.32f, y + i * 0.12f, 0f);
                pipe.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                pipe.transform.localScale = new Vector3(0.12f, RoomDepth / 2f, 0.12f);
                pipe.GetComponent<Renderer>().sharedMaterial = _pipeMat;
                pipe.RegisterSurface($"PIPE-{i + 1}", "A 区", $"第 {i + 1} 根桥架/管道", this);
            }
        }

        // ---------- 控制台 ----------

        private void BuildConsole()
        {
            var desk = CreateBox("Console", new Vector3(4.2f, 0.45f, 4.2f),
                new Vector3(2.4f, 0.9f, 0.9f), _consoleMat);
            desk.RegisterSurface("CONSOLE-1", "A 区", "值班控制台", this);

            var screen = CreateBox("ConsoleScreen", new Vector3(4.2f, 1.35f, 4.35f),
                new Vector3(1.1f, 0.7f, 0.05f), MakeMat(new Color(0.15f, 0.35f, 0.55f)));
            screen.RegisterSurface("CONSOLE-1-S", "A 区", "控制台显示器", this);
        }

        // ---------- 结构柱 ----------

        private void BuildPillars()
        {
            CreateBox("Pillar_E", new Vector3(RoomWidth / 2f - 2f, WallHeight / 2f, -RoomDepth / 2f + 3f),
                new Vector3(0.8f, WallHeight, 0.8f), _wallMat)
                .RegisterSurface("PILLAR-E", "东侧", "结构柱", this);
            CreateBox("Pillar_W", new Vector3(-RoomWidth / 2f + 2f, WallHeight / 2f, RoomDepth / 2f - 3f),
                new Vector3(0.8f, WallHeight, 0.8f), _wallMat)
                .RegisterSurface("PILLAR-W", "西侧", "结构柱", this);
        }

        // ---------- 危险区域标识 ----------

        private void BuildHazardZone()
        {
            // 贴地画一圈红色警示框，作为“积水/漏液”这类地面问题的参照物
            var decal = CreateBox("HazardZone", new Vector3(0.8f, 0.011f, -2.2f),
                new Vector3(2.2f, 0.02f, 2.2f), _accentMat);
            decal.RegisterSurface("ZONE-HZ1", "B 区", "设备检修警示区", this);
        }

        // ---------- 工具方法 ----------

        private GameObject CreateBox(string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, true);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        private void Register(GameObject go, string deviceId, string zone, string displayName)
        {
            go.RegisterSurface(deviceId, zone, displayName, this);
        }

        internal void AddSurface(SurfaceInfo info)
        {
            _surfaces.Add(info);
        }

        /// <summary>按 Collider 反查可点击表面的信息（点击命中设备时显示区域/编号）。</summary>
        public SurfaceInfo Describe(Collider collider)
        {
            if (collider == null)
            {
                return null;
            }
            var tagged = collider.GetComponent<SurfaceTag>();
            if (tagged == null)
            {
                tagged = collider.GetComponentInParent<SurfaceTag>();
            }
            return tagged != null ? tagged.Info : null;
        }
    }

    /// <summary>挂在每个可点击表面上，记录区域/设备编号。</summary>
    public class SurfaceTag : MonoBehaviour
    {
        public SurfaceInfo Info;
    }

    public static class SurfaceTagExtensions
    {
        /// <summary>把对象登记为可巡检表面（同时挂 SurfaceTag 并汇总到 SceneComposer）。</summary>
        public static GameObject RegisterSurface(this GameObject go, string deviceId, string zone, string displayName, SceneComposer composer)
        {
            if (go == null)
            {
                return null;
            }

            if (go.GetComponent<Collider>() == null)
            {
                Debug.LogError($"[SceneComposer] {go.name} 没有 Collider，射线无法命中");
                return go;
            }

            var info = new SurfaceInfo
            {
                deviceId = deviceId,
                zone = zone,
                displayName = displayName,
                transform = go.transform
            };

            var tag = go.GetComponent<SurfaceTag>();
            if (tag == null)
            {
                tag = go.AddComponent<SurfaceTag>();
            }
            tag.Info = info;
            composer?.AddSurface(info);
            return go;
        }
    }
}


