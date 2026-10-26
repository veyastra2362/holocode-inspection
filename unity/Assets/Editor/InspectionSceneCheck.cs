#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inspection.EditorTools
{
    /// <summary>
    /// 编辑器菜单：一键自检（不需要手动点 Play）。
    ///
    /// 用法：菜单栏 <b>Inspection → 运行场景自检</b>
    /// 它会打开 Inspection 场景、进入播放模式、等场景装配完成后执行
    /// <see cref="InspectionDiagnostics.RunSelfTest"/>，把结果写到
    /// <c>&lt;project&gt;/Logs/selftest.txt</c> 并打印到 Console。
    ///
    /// 这样即使没有人在机器前操作，也能确认“场景真的建起来了、射线真的能命中、
    /// 后端真的连得上”。
    /// </summary>
    public static class InspectionSceneCheck
    {
        private const string ScenePath = "Assets/Scenes/Inspection.unity";

        [MenuItem("Inspection/运行场景自检 %#t")]
        public static void RunSelfTestFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
            EditorApplication.update -= WaitForSceneThenTest;
            EditorApplication.update += WaitForSceneThenTest;
        }

        private static void WaitForSceneThenTest()
        {
            var diag = Object.FindAnyObjectByType<InspectionDiagnostics>();
            var placement = Object.FindAnyObjectByType<IssuePlacementController>();

            // 场景由 SceneBootstrap 在 Start 里生成，等它装配完成再检查
            if (diag == null || placement == null || placement.SurfaceCount == 0)
            {
                return;
            }

            EditorApplication.update -= WaitForSceneThenTest;
            diag.RunSelfTest();

            var path = Path.Combine(ProjectRoot, "Logs", "selftest.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Logs");
            var sb = new StringBuilder();
            sb.AppendLine($"# 场景自检 {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Unity {Application.unityVersion}");
            sb.AppendLine();
            sb.AppendLine(diag.LastSelfTestReport ?? "(无报告)");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[Inspection] 自检报告已写入 " + path);
        }

        private static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        /// <summary>
        /// 批处理入口（供命令行无人值守自检）：
        ///   Unity.exe -batchmode -projectPath unity -executeMethod
        ///     Inspection.EditorTools.InspectionSceneCheck.RunPlayModeSelfTest
        ///
        /// 说明：批处理 + -nographics 下进入“播放模式”容易卡在编辑器启动流程，
        /// 因此这里直接在**编辑模式**同步构建场景并做检查——对“场景是否真的建对、
        /// 射线是否真的能命中、后端是否连得上”这些关注点已经足够，
        /// 不依赖帧循环与渲染。
        /// </summary>
        public static void RunPlayModeSelfTest()
        {
            var sb = new StringBuilder();
            var ok = 0;
            var fail = 0;

            void Check(string name, bool pass, string detail = "")
            {
                if (pass) { ok++; sb.AppendLine($"PASS  {name}  {detail}"); }
                else { fail++; sb.AppendLine($"FAIL  {name}  {detail}"); }
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            try
            {
                var bootstrap = new GameObject("InspectionBootstrap");
                var scene = bootstrap.AddComponent<SceneComposer>();
                scene.Build();
                Check("场景构建完成", scene.Surfaces.Count > 0, $"可点击表面 {scene.Surfaces.Count} 个");

                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(new Vector3(0f, 1.65f, -4f), Quaternion.identity);
                Check("相机创建成功", cam != null);

                var inputGo = new GameObject("Input");
                var input = inputGo.AddComponent<InspectionInput>();
                var placementGo = new GameObject("Placement");
                var placement = placementGo.AddComponent<IssuePlacementController>();
                placement.ConfigureDependencies(cam, input, scene, placement);

                // 正对机柜：从相机正前方做一次真实物理射线
                var target = new Vector3(0f, 1.4f, -2.6f);
                cam.transform.LookAt(target);
                var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
                var hit = Physics.Raycast(ray, out var info, 100f, ~0, QueryTriggerInteraction.Ignore);
                Check("屏幕中心射线命中表面", hit, hit ? info.collider.name + " @ " + info.point : "未命中");

                if (hit)
                {
                    var pending = placement.PlaceAt(info.point, info.normal, scene.Describe(info.collider));
                    Check("放置标记成功", pending != null);
                    Check("标记父节点坐标 = 精确命中点",
                        pending != null && (pending.Root.transform.position - info.point).sqrMagnitude < 1e-8f,
                        pending != null ? pending.Root.transform.position.ToString("F3") : "-");
                    Check("待提交状态已记录", placement.Pending != null);
                }

                // 取消后应移除
                placement.CancelPending();
                Check("取消后移除未保存标记", placement.Pending == null);

                // 后端连通性（真实 HTTP）。
                // 注意：UnityWebRequest 的 AsyncOperation 要跨帧才完成，在同步循环里
                // MoveNext 会得到“请求未完成”。这里用同步 HTTP 旁路验证后端可达；
                // UnityWebRequest 路径由 EditMode 单测（FakeIssueApi）覆盖。
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                        IssueApi.DefaultBaseUrl.TrimEnd('/') + "/api/health");
                    req.Timeout = 5000;
                    using (var res = (System.Net.HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(res.GetResponseStream()))
                    {
                        var body = reader.ReadToEnd();
                        Check("后端健康（/api/health 返回 ok:true）", body.Contains("\"ok\":true"), body.Trim());
                    }
                }
                catch (System.Exception e)
                {
                    Check("后端健康（/api/health 返回 ok:true）", false,
                        "无法连接 " + IssueApi.DefaultBaseUrl + "：" + e.Message);
                }
            }
            catch (System.Exception e)
            {
                fail++;
                sb.AppendLine("FAIL  自检过程抛出异常：" + e);
            }

            sb.AppendLine();
            sb.AppendLine($"合计：通过 {ok}，失败 {fail}");
            var report = sb.ToString();
            LastReport = report;

            var path = Path.Combine(ProjectRoot, "Logs", "selftest.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Logs");
            File.WriteAllText(path,
                $"# 场景自检（编辑模式同步执行）\n# Unity {Application.unityVersion}   {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n# 屏幕 {Screen.width}x{Screen.height}\n\n{report}");
            Debug.Log("[Inspection] 自检结果\n" + report);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        /// <summary>最近一次报告（便于编辑器菜单读取）。</summary>
        public static string LastReport { get; private set; }
        /// <summary>
        /// 图形界面下用：打开场景、进播放模式，并在 1.5 秒后真实放置一个标记（走完整射线流程），
        /// 让“上报问题”表单直接出现在屏幕上，便于人工确认输入框是否可见、是否被按钮遮挡。
        /// </summary>
        public static void OpenFormForInspection()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
            _inspectFrames = 0;
            EditorApplication.update -= DriveOpenForm;
            EditorApplication.update += DriveOpenForm;
        }

        private static int _inspectFrames;

        private static void DriveOpenForm()
        {
            _inspectFrames++;
            var placement = Object.FindAnyObjectByType<IssuePlacementController>();
            if (placement == null || placement.SurfaceCount == 0) return;
            if (_inspectFrames < 90) return;

            EditorApplication.update -= DriveOpenForm;

            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[Inspection] 找不到主相机"); return; }

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out var hit, 50f))
            {
                var composer = Object.FindAnyObjectByType<SceneComposer>();
                var info = composer != null ? composer.Describe(hit.collider) : null;
                if (info == null)
                {
                    info = new SurfaceInfo
                    {
                        deviceId = hit.collider.name,
                        zone = "未分区",
                        displayName = hit.collider.name,
                        transform = hit.collider.transform
                    };
                }
                placement.PlaceAt(hit.point, hit.normal, info);
                Debug.Log($"[Inspection] 已放置标记并打开表单，命中 {hit.collider.name} @ {hit.point}");

                var hud = Object.FindAnyObjectByType<InspectionHUD>();
                if (hud != null) hud.DumpFormLayout();
            }
            else
            {
                Debug.LogWarning("[Inspection] 视线中心未命中任何物体");
            }
        }
        /// <summary>
        /// 渲染一张“表单已打开”的截图，用来人工确认输入框/按钮的排布没有重叠。
        /// 输出到 Logs/form-preview.png。
        /// </summary>
        public static void CaptureFormPreview()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机 + 光
            var camGo = new GameObject("Main Camera", typeof(Camera));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.transform.position = new Vector3(0.68f, 1.65f, -4.4f);
            cam.transform.LookAt(new Vector3(0.68f, 1.2f, -3.07f));
            cam.fieldOfView = 68f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            cam.nearClipPlane = 0.03f;

            var lightGo = new GameObject("Light", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var scene = new GameObject("Scene").AddComponent<SceneComposer>();
            scene.Build();
            Physics.SyncTransforms();

            // HUD 并强制打开表单
            var hudGo = new GameObject("HUD");
            var hud = hudGo.AddComponent<InspectionHUD>();
            hud.Configure(null, null, cam, "http://localhost:8080");
            hud.BuildUIPublic();
            hud.ForceOpenFormForPreview();

            Canvas.ForceUpdateCanvases();

            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;

            var dir = Path.Combine(ProjectRoot, "Logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "form-preview.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[Inspection] 表单截图已保存: " + path);
            EditorApplication.Exit(0);
        }
        /// <summary>诊断：从玩家出生点朝前方做多条射线，看看默认视角能看到什么。</summary>

        /// <summary>诊断 2：直接列出场景里所有带 Collider 的物体及其世界坐标包围盒，
        /// 再从出生点向每个机柜面板做定向射线，确认到底能不能点中。</summary>
        public static void DiagnoseColliders()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = new GameObject("Scene").AddComponent<SceneComposer>();
            scene.Build();

            // batchmode/编辑模式下 BoxCollider 的世界包围盒需要物理同步后才准确
            Physics.SyncTransforms();
            Canvas.ForceUpdateCanvases();

            var sb = new StringBuilder();
            sb.AppendLine("=== 场景中所有 Collider（已 SyncTransforms）===");
            foreach (var col in Object.FindObjectsByType<Collider>())
            {
                var b = col.bounds;
                sb.AppendLine($"  {col.name,-22} center=({b.center.x,7:F2},{b.center.y,6:F2},{b.center.z,7:F2}) " +
                              $"size=({b.size.x,5:F2},{b.size.y,5:F2},{b.size.z,5:F2})");
            }

            var spawn = new Vector3(0.68f, 1.65f, -4.4f);
            sb.AppendLine();
            sb.AppendLine($"=== 从出生点 {spawn} 向 A 排柜门(z=-3.07) 做定向射线 ===");
            for (var x = -2.0f; x <= 2.01f; x += 0.5f)
            {
                var target = new Vector3(x, 1.2f, -2.6f); // A 排柜门中心附近
                var dir = (target - spawn).normalized;
                if (Physics.Raycast(spawn, dir, out var hit, 30f))
                    sb.AppendLine($"  x={x,5:F1}  ->  {hit.distance,5:F2}m  {hit.collider.name}");
                else
                    sb.AppendLine($"  x={x,5:F1}  ->  (未命中)");
            }

            sb.AppendLine();
            sb.AppendLine("=== 全方向扫描（每 30° yaw，pitch 0）===");
            for (var yaw = 0; yaw < 360; yaw += 30)
            {
                var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                if (Physics.Raycast(spawn, dir, out var hit, 30f))
                    sb.AppendLine($"  yaw={yaw,3}°  ->  {hit.distance,5:F2}m  {hit.collider.name}");
                else
                    sb.AppendLine($"  yaw={yaw,3}°  ->  (未命中)");
            }

            var path = Path.Combine(ProjectRoot, "Logs", "colliders.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Logs");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[Inspection] collider 诊断完成");
            EditorApplication.Exit(0);
        }        public static void DiagnoseSpawnView()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = new GameObject("Scene").AddComponent<SceneComposer>();
            scene.Build();

            var spawn = new Vector3(0.68f, 1.65f, -4.4f);
            var report = new StringBuilder();
            report.AppendLine($"出生点 (world): {spawn}");
            report.AppendLine("从出生点朝 +Z（默认朝向前方）的射线命中：");

            // 模拟相机 68° FOV 在 16:9 下的多角度采样
            for (var i = -2; i <= 2; i++)
            {
                for (var j = -1; j <= 1; j++)
                {
                    // yaw=180 表示面向 -Z（正对 A 排柜门）
                    var dir = Quaternion.Euler(j * 20f, 180f + i * 15f, 0f) * Vector3.forward;
                    if (Physics.Raycast(spawn, dir, out var hit, 60f))
                    {
                        var info = scene.Describe(hit.collider);
                        report.AppendLine($"  yaw={i * 15,4}° pitch={j * 20,4}°  ->  {hit.distance,6:F2}m  " +
                                          $"{hit.collider.name}  [{info?.zone}/{info?.displayName}]");
                    }
                    else
                    {
                        report.AppendLine($"  yaw={i * 15,4}° pitch={j * 20,4}°  ->  (未命中)");
                    }
                }
            }

            var path = Path.Combine(ProjectRoot, "Logs", "spawn-view.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Logs");
            File.WriteAllText(path, report.ToString());
            Debug.Log("[Inspection] 出生点诊断\n" + report);
            EditorApplication.Exit(0);
        }
        /// <summary>
        /// 录制用：进入播放模式后自动跑一遍“点击 → 填表 → 提交”，
        /// 等价于人手按 F10，但不需要真实的按键事件（无人值守录屏时用）。
        ///   Unity.exe -projectPath unity -executeMethod Inspection.EditorTools.InspectionSceneCheck.AutoDemoForRecording
        /// </summary>
        public static void AutoDemoForRecording()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
            _demoFrames = 0;
            EditorApplication.update -= DriveAutoDemo;
            EditorApplication.update += DriveAutoDemo;
        }

        private static int _demoFrames;

        private static void DriveAutoDemo()
        {
            _demoFrames++;
            var diag = Object.FindAnyObjectByType<InspectionDiagnostics>();
            var placement = Object.FindAnyObjectByType<IssuePlacementController>();

            // 等场景装配完成后开始（约 2 秒），再给录制留出时间
            if (diag == null || placement == null || placement.SurfaceCount == 0) return;
            if (_demoFrames < 90) return;

            EditorApplication.update -= DriveAutoDemo;
            Debug.Log("[Inspection] 触发自动演示");
            diag.RunAutoDemo();

            // 结束录制由外部控制，这里不退出播放模式，方便人工继续操作
        }
        /// <summary>命令行/菜单入口：打开 Inspection 场景并停在编辑器里，方便人工检查与录制。</summary>
        public static void OpenSceneForReview()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
            Debug.Log("[Inspection] 已打开场景并进入播放模式");
        }

        /// <summary>
        /// 批处理入口：构建场景并渲染一张预览图到 Logs/scene-preview.png。
        /// 需要带图形设备运行（不要加 -nographics）。
        /// </summary>
        public static void CapturePreview()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var scene = new GameObject("Scene").AddComponent<SceneComposer>();
            scene.Build();

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            cam.transform.position = new Vector3(0f, 1.65f, -8.2f);
            cam.transform.LookAt(new Vector3(0f, 1.4f, -2.0f));

            var lightGo = new GameObject("Dir Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.52f, 0.55f);

            // 在正前方柜体上放一个高优先级标记，直观展示“标注”
            var hitRay = new Ray(new Vector3(0f, 1.4f, -8.2f), Vector3.forward);
            if (Physics.Raycast(hitRay, out var hit, 100f))
            {
                var markerGo = new GameObject("PreviewMarker");
                markerGo.transform.position = hit.point;
                var marker = markerGo.AddComponent<IssueMarker>();
                marker.SetSurfaceNormal(hit.normal);
                marker.Build(false);
                marker.Bind(new IssueData
                {
                    id = "preview",
                    title = "1 号机柜温度异常",
                    description = "预览用",
                    priority = "high",
                    status = "open",
                    position = new PositionData(hit.point)
                }, true);
            }

            const int w = 1280, h = 720;
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            var dir = Path.Combine(ProjectRoot, "Logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "scene-preview.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[Inspection] 预览图已保存: " + path);

            EditorApplication.Exit(0);
        }

    }
}
#endif

















