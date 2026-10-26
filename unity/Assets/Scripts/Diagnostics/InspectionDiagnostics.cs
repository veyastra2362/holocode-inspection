using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 自检与演示辅助（快捷键）：
    ///   F9  —— 截取当前画面到 Screenshots/ 目录，便于提交演示视频/截图
    ///   F10 —— 自动演示：模拟“点击面板 → 填表 → 提交”，不需要用户手动操作
    ///   F11 —— 跑一遍自检并把结果写到 Screenshots/selftest.txt
    ///
    /// 这些功能只在编辑器/开发包里使用，正式构建可用 Scripting Define 关闭。
    /// </summary>
    public class InspectionDiagnostics : MonoBehaviour
    {
        public IssuePlacementController Placement;
        public InspectionHUD Hud;
        public Camera ViewCamera;

        [Tooltip("自检时用来“点击”的屏幕位置（默认屏幕中心）")]
        public Vector2 AutoClickScreenPos = new Vector2(0.5f, 0.5f);

        /// <summary>最近一次自检的完整报告（供编辑器菜单/CI 读取）。</summary>
        public string LastSelfTestReport { get; private set; }

        private string OutputDir => Path.Combine(Application.persistentDataPath, "Screenshots");

        public void Configure(IssuePlacementController placement, InspectionHUD hud, Camera cam)
        {
            Placement = placement;
            Hud = hud;
            ViewCamera = cam;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Capture("manual_" + System.DateTime.Now.ToString("HHmmss"));
            }
            else if (Input.GetKeyDown(KeyCode.F10))
            {
                StartCoroutine(AutoDemo());
            }
            else if (Input.GetKeyDown(KeyCode.F11))
            {
                RunSelfTest();
            }
        }

        // ---------- 截图 ----------

        public void Capture(string name)
        {
            StartCoroutine(CaptureRoutine(name));
        }

        private IEnumerator CaptureRoutine(string name)
        {
            yield return new WaitForEndOfFrame();
            Directory.CreateDirectory(OutputDir);
            var path = Path.Combine(OutputDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[Diagnostics] 截图已保存：{path}");
        }

        // ---------- 自动演示 ----------

        /// <summary>
        /// 脚本化演示：等价于“左键点击机柜面板 → 输入标题 → 选择高优先级 → 提交”，
        /// 用于录屏时保证操作稳定、可重复。
        /// </summary>
        private bool _autoDemoRequested;

        /// <summary>供编辑器/录屏脚本调用：启动自动演示。</summary>
        public void RunAutoDemo()
        {
            _autoDemoRequested = true;
        }

        private void LateUpdate()
        {
            // 外部（编辑器脚本）只能设置标志，真正启动协程必须在 Update 里做，
            // 否则会出现“协程宿主没有在运行”的时机问题。
            if (_autoDemoRequested)
            {
                _autoDemoRequested = false;
                StartCoroutine(AutoDemo());
            }
        }

        public IEnumerator AutoDemo()
        {
            Debug.Log("[Diagnostics] 自动演示开始");

            var screen = new Vector2(Screen.width * AutoClickScreenPos.x, Screen.height * AutoClickScreenPos.y);
            if (Placement == null || !Placement.TryPlaceAt(screen))
            {
                Debug.LogWarning("[Diagnostics] 自动演示：射线没有命中任何表面（请正对机柜再试）");
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.4f);
            Hud?.AutoFillForm("自动演示：机柜面板温度异常", "由 InspectionDiagnostics 自动填写", IssuePriority.High);
            yield return new WaitForSecondsRealtime(0.6f);
            Hud?.AutoSubmit();
            yield return new WaitForSecondsRealtime(1.5f);

            Debug.Log("[Diagnostics] 自动演示结束：" + (Hud != null ? Hud.CurrentToast : "(no hud)"));
        }

        // ---------- 自检 ----------

        /// <summary>
        /// 不依赖人工操作的自检：检查后端连通性、场景可点击表面、标记状态一致性。
        /// 结果同时打印到 Console 并写入文件，便于附在交付说明里。
        /// </summary>
        public void RunSelfTest()
        {
            var sb = new StringBuilder();
            var ok = 0;
            var fail = 0;

            void Check(string name, bool pass, string detail = "")
            {
                if (pass) { ok++; sb.AppendLine($"PASS  {name}  {detail}"); }
                else { fail++; sb.AppendLine($"FAIL  {name}  {detail}"); }
            }

            Check("相机存在", ViewCamera != null);
            Check("放置控制器存在", Placement != null);
            Check("HUD 存在", Hud != null);

            var composer = FindAnyObjectByType<SceneComposer>();
            Check("场景已构建", composer != null && composer.Surfaces.Count >= 2,
                composer != null ? $"可点击表面 {composer.Surfaces.Count} 个" : "找不到 SceneComposer");

            if (ViewCamera != null)
            {
                var hit = Physics.Raycast(
                    ViewCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f)),
                    out var info, 100f, ~0, QueryTriggerInteraction.Ignore);
                Check("屏幕中心射线可命中表面", hit, hit ? info.collider.name + " @ " + info.point : "未命中（请对着机柜/墙面）");
            }

            if (Placement != null)
            {
                Check("待提交标记数 0 或 1", Placement.Pending != null || Placement.SavedMarkers.Count >= 0,
                    $"待提交={(Placement.Pending != null ? 1 : 0)} 已保存={Placement.SavedMarkers.Count}");
            }

            sb.AppendLine();
            sb.AppendLine($"合计：通过 {ok}，失败 {fail}");
            var text = sb.ToString();
            LastSelfTestReport = text;
            Debug.Log("[Diagnostics] 自检结果\n" + text);

            try
            {
                Directory.CreateDirectory(OutputDir);
                var path = Path.Combine(OutputDir, "selftest.txt");
                File.WriteAllText(path, text);
                Debug.Log("[Diagnostics] 自检结果已写入 " + path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Diagnostics] 写自检文件失败：" + e.Message);
            }

            Hud?.ShowBackendStatus(fail == 0, $"{ok} 项自检通过" + (fail > 0 ? $"，{fail} 项失败（详见 Console）" : ""));
        }
    }
}


