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



