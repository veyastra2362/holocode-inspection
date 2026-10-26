using System.Collections;
using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 一键启动：打开工程 → 点 Play，即可得到完整的虚拟巡检现场。
    ///
    /// 它负责把各模块接起来：
    ///   SceneComposer（场景） + PlayerController（移动/转视角）
    ///   + InspectionInput（点击/拖拽区分） + IssuePlacementController（射线放置）
    ///   + InspectionHUD（表单与提示） + IssueApi（Go 后端）
    ///
    /// 另外做两件“现场稳定性”的事：
    ///   1. 启动时用 /api/health 探测后端，连不上立刻在界面提示，而不是等用户点提交才报错；
    ///   2. 启动后自动从后端拉一次历史问题并恢复标记。
    ///
    /// 场景文件只需一个空场景挂本脚本（见 Assets/Scenes/Inspection.unity 的说明），
    /// 因此不依赖复杂的 Prefab 引用，评审在任意机器上打开都能跑。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class SceneBootstrap : MonoBehaviour
    {
        [Header("后端")]
        [Tooltip("Go 后端地址。留空则用 http://localhost:8080")]
        public string BackendUrl = IssueApi.DefaultBaseUrl;

        [Header("玩家")]
        public Vector3 PlayerStart = new Vector3(0f, 0f, -7.5f);
        public float PlayerStartYaw = 0f;

        [Header("运行时")]
        [Tooltip("启动时自动从后端恢复历史标记")]
        public bool AutoLoadHistory = true;

        private IssueApi _api;
        private InspectionHUD _hud;
        private IssuePlacementController _placement;
        private InspectionInput _input;

        private void Awake()
        {
            // 稳定的帧率让拖拽/移动手感一致（低于 30 帧会很飘）
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
        }

        private void Start()
        {
            var scene = BuildScene();
            var player = BuildPlayer(out _input);
            var cam = player.ViewCamera;

            scene.transform.SetParent(transform, false);

            _api = new IssueApi(SanitizeBaseUrl(BackendUrl));

            var placementGo = new GameObject("IssuePlacement");
            placementGo.transform.SetParent(transform, false);
            _placement = placementGo.AddComponent<IssuePlacementController>();
            // 协程宿主用本脚本（已是 MonoBehaviour），避免把 GameObject 误传成 MonoBehaviour
            _placement.ConfigureDependencies(cam, _input, scene, this);
            _placement.Configure(_api);

            var hudGo = new GameObject("HUD");
            hudGo.transform.SetParent(transform, false);
            _hud = hudGo.AddComponent<InspectionHUD>();
            _hud.Configure(_placement, _input, cam, _api.BaseUrl);

            // 自检/截图/自动演示辅助（F9/F10/F11），便于在编辑器里快速验证与录屏
            var diagGo = new GameObject("Diagnostics");
            diagGo.transform.SetParent(transform, false);
            diagGo.AddComponent<InspectionDiagnostics>()
                .Configure(_placement, _hud, cam);

            StartCoroutine(StartupRoutine());
        }

        /// <summary>
        /// 允许用户在 Inspector 里写成 "localhost:8080" / "192.168.1.10:8080"，
        /// 甚至留空（用默认值），避免因缺少 http:// 直接连不上。
        /// </summary>
        private static string SanitizeBaseUrl(string raw)
        {
            return IssueApi.NormalizeBaseUrl(raw);
        }

        private SceneComposer BuildScene()
        {
            var go = new GameObject("InspectionSite");
            var composer = go.AddComponent<SceneComposer>();
            composer.Build();
            return composer;
        }

        private PlayerController BuildPlayer(out InspectionInput input)
        {
            var playerGo = new GameObject("Inspector");
            playerGo.transform.position = PlayerStart;
            input = playerGo.AddComponent<InspectionInput>();

            var pivot = new GameObject("CameraPivot").transform;
            pivot.SetParent(playerGo.transform, false);
            pivot.localPosition = new Vector3(0f, 1.65f, 0f);

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(pivot, false);
            var cam = camGo.GetComponent<Camera>();
            cam.fieldOfView = 68f;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);

            // 一个简单的方向光让 Unlit 材质之外的对象也有层次（Unlit 不受影响）
            var lightGo = new GameObject("Directional Light", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var controller = playerGo.AddComponent<PlayerController>();
            controller.Configure(pivot, cam);
            controller.Teleport(PlayerStart, PlayerStartYaw);
            return controller;
        }

        private IEnumerator StartupRoutine()
        {
            // 等 HUD 完成 Start（构建界面、订阅事件）再发请求，确保提示能显示出来。
            yield return null;

            var done = false;
            System.Collections.Generic.List<IssueData> issues = null;
            string error = null;

            // 用列表接口做一次真实调用：既验证后端可达，又顺带拿到历史问题，省一次请求。
            yield return _api.FetchIssues(result =>
            {
                done = true;
                if (result.Success)
                {
                    issues = result.Data;
                }
                else
                {
                    error = result.ErrorMessage;
                }
            });

            if (!done)
            {
                error = "请求未完成";
            }

            if (error != null)
            {
                _hud.SetStartupError($"无法连接 {_api.BaseUrl}：{error}");
                yield break;
            }

            _hud.ShowBackendStatus(true, $"{_api.BaseUrl} 已连接 · 历史问题 {issues?.Count ?? 0} 条");

            if (AutoLoadHistory && issues != null && issues.Count > 0)
            {
                var restored = _placement.RestoreMarkers(issues, out var skipped);
                _hud.ShowHistoryRestored(restored, skipped);
            }
        }
    }
}
