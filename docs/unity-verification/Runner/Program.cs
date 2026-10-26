// 离线行为验证（不是交付物）：在 .NET 上真正运行 Unity 业务逻辑与 UI，断言题目关键要求。
// 运行：dotnet run --project .verify/UnityRuntimeTest
using System;
using System.Collections.Generic;
using System.Linq;
using Inspection;
using UnityEngine;
using UnityEngine.Networking;

namespace RuntimeCheck
{
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> Failures = new List<string>();

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                _fail++;
                Failures.Add(name + (detail != null ? " :: " + detail : ""));
                Console.WriteLine($"  FAIL  {name}" + (detail != null ? "  -> " + detail : ""));
            }
        }

        private static int Main()
        {
            Console.WriteLine("== 1. JSON 扫描 / 信封解析 ==");
            TestJsonScan();
            Console.WriteLine("== 2. 枚举与 URL 规范化 ==");
            TestEnums();
            Console.WriteLine("== 3. IssueApi 对真实 Go 风格响应的解析与错误处理 ==");
            TestIssueApi();
            Console.WriteLine("== 4. 放置流程：一次点击一个标记 / 取消移除 / 提交 ==");
            TestPlacement();
            Console.WriteLine("== 5. 场景构建（Collider 覆盖率） ==");
            TestScene();
            Console.WriteLine("== 6. HUD 与真实后端闭环（用真实 Go 服务器响应） ==");
            TestHudFlow();

            Console.WriteLine("== 7. 与真实 Go 后端联调（若已启动） ==");
            TestRealBackend();

            Console.WriteLine();
            Console.WriteLine($"通过 {_pass} 项，失败 {_fail} 项");
            foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
            return _fail == 0 ? 0 : 1;
        }

        // ---------------- 1 ----------------
        private static void TestJsonScan()
        {
            Check("取字符串值", JsonScan.ValueOf("{\"a\":\"hello\"}", "a") == "\"hello\"");
            Check("取对象值", JsonScan.ValueOf("{\"ok\":true,\"data\":{\"x\":1}}", "data") == "{\"x\":1}");
            Check("取数组值", JsonScan.ValueOf("{\"data\":[{\"id\":\"1\"},{\"id\":\"2\"}],\"t\":2}", "data") == "[{\"id\":\"1\"},{\"id\":\"2\"}]");
            Check("不误取嵌套同名 key",
                JsonScan.ValueOf("{\"ok\":true,\"data\":{\"data\":\"inner\",\"id\":\"abc\"}}", "data") == "{\"data\":\"inner\",\"id\":\"abc\"}");
            var tricky = "{\"ok\":true,\"data\":{\"title\":\"机柜 {A3} \\\"异常\\\"\"},\"total\":1}";
            Check("字符串内花括号/转义不破坏结构", JsonScan.ValueOf(tricky, "data") == "{\"title\":\"机柜 {A3} \\\"异常\\\"\"}",
                JsonScan.ValueOf(tricky, "data"));
            Check("缺 key 返回 null", JsonScan.ValueOf("{\"ok\":true}", "data") == null);
            Check("冒号前后空白", JsonScan.ValueOf("{\"data\"  :   1.5 }", "data") == "1.5");
            Check("未闭合返回 null", JsonScan.ValueOf("{\"data\":[1,2", "data") == null);

            var env = Envelope.Parse("{\"ok\":false,\"code\":\"BAD_REQUEST\",\"error\":\"title is required\"}");
            Check("错误信封解析", env != null && !env.Ok && env.Code == "BAD_REQUEST");
            var f = env.ToFailure<IssueData>(400);
            Check("错误转可读文案", !f.Success && f.ErrorMessage == "title is required" && f.HttpStatus == 400);
            Check("HTML 响应不当作 JSON", Envelope.Parse("<html>502</html>") == null);
        }

        // ---------------- 2 ----------------
        private static void TestEnums()
        {
            Check("优先级线上格式", IssueEnums.ToWire(IssuePriority.High) == "high");
            Check("状态线上格式", IssueEnums.ToWire(IssueStatus.InProgress) == "in_progress");
            Check("大小写不敏感解析", IssueEnums.TryParsePriority(" HIGH ", out var p) && p == IssuePriority.High);
            Check("非法优先级被拒", !IssueEnums.TryParsePriority("urgent", out _));
            Check("URL 规范化 localhost:8080", IssueApi.NormalizeBaseUrl("localhost:8080") == "http://localhost:8080");
            Check("URL 去尾斜杠", IssueApi.NormalizeBaseUrl("http://localhost:8080/") == "http://localhost:8080");
            Check("URL 空值用默认", IssueApi.NormalizeBaseUrl("  ") == "http://localhost:8080");
        }

        // ---------------- 3 ----------------
        private static void TestIssueApi()
        {
            var okBody = "{\"ok\":true,\"data\":{\"id\":\"iss_1\",\"title\":\"温度异常\",\"description\":\"偏高\",\"priority\":\"high\",\"status\":\"open\",\"position\":{\"x\":1.25,\"y\":0.8,\"z\":2.4},\"createdAt\":\"2026-10-26T07:11:03Z\",\"updatedAt\":\"2026-10-26T07:11:03Z\"}}";
            var listBody = "{\"ok\":true,\"data\":[" + okBody.Substring(okBody.IndexOf("\"data\":") + 7).TrimEnd('}') + "],\"total\":1}";

            // 成功创建
            UnityWebRequest.Server = (url, method, body) => (201, okBody, UnityWebRequest.Result.Success);
            var api = new IssueApi("localhost:8080");
            ApiResult<IssueData> created = null;
            var routine = api.CreateIssue(new CreateIssueRequest
            {
                title = "x",
                priority = "high",
                position = new PositionData(new Vector3(1.25f, 0.8f, 2.4f))
            }, r => created = r);
            Drive(routine);
            Check("创建成功解析 data", created != null && created.Success && created.Data.id == "iss_1",
                created?.ErrorMessage);
            Check("坐标解析正确", created != null && Math.Abs(created.Data.position.x - 1.25f) < 1e-4 && Math.Abs(created.Data.position.z - 2.4f) < 1e-4);
            Check("BaseUrl 被规范化", api.BaseUrl == "http://localhost:8080");

            // 400 参数错误
            UnityWebRequest.Server = (url, method, body) =>
                (400, "{\"ok\":false,\"code\":\"BAD_REQUEST\",\"error\":\"title is required\",\"fields\":{\"title\":\"title is required\"}}",
                 UnityWebRequest.Result.ProtocolError);
            ApiResult<IssueData> bad = null;
            var r2 = api.CreateIssue(new CreateIssueRequest(), r => bad = r);
            Drive(r2);
            Check("400 时展示后端 error 文案", bad != null && !bad.Success && bad.ErrorMessage == "title is required", bad?.ErrorMessage);
            Check("400 时保留状态码", bad != null && bad.HttpStatus == 400);

            // 连接失败
            UnityWebRequest.Server = (url, method, body) => (0, null, UnityWebRequest.Result.ConnectionError);
            ApiResult<IssueData> conn = null;
            var r3 = api.CreateIssue(new CreateIssueRequest { title = "x", position = new PositionData() }, r => conn = r);
            Drive(r3);
            Check("连接失败提示包含后端地址", conn != null && !conn.Success && conn.ErrorMessage.Contains("无法连接后端"), conn?.ErrorMessage);

            // 500 HTML
            UnityWebRequest.Server = (url, method, body) => (502, "<html>bad gateway</html>", UnityWebRequest.Result.Success);
            ApiResult<IssueData> html = null;
            var r4 = api.CreateIssue(new CreateIssueRequest { title = "x", position = new PositionData() }, r => html = r);
            Drive(r4);
            Check("非 JSON 响应给出可读错误", html != null && !html.Success && html.ErrorMessage.Contains("无法解析 JSON"), html?.ErrorMessage);

            // 列表解析
            UnityWebRequest.Server = (url, method, body) => (200, listBody, UnityWebRequest.Result.Success);
            ApiResult<List<IssueData>> list = null;
            var r5 = api.FetchIssues(r => list = r);
            Drive(r5);
            Check("列表解析（顶层数组）", list != null && list.Success && list.Data.Count == 1 && list.Data[0].id == "iss_1",
                list?.ErrorMessage);

            Console.WriteLine("    请求体示例: " + JsonUtility.ToJson(new CreateIssueRequest
            {
                title = "1 号机柜温度异常", description = "右侧偏高", priority = "high",
                position = new PositionData(new Vector3(1.25f, 0.8f, 2.4f))
            }));
        }

        // ---------------- 4 ----------------
        private class FakeApi : IIssueApi
        {
            public string BaseUrl => "http://fake";
            public readonly List<CreateIssueRequest> Calls = new List<CreateIssueRequest>();
            public bool Fail;
            public IEnumerator<object> Dummy;

            public System.Collections.IEnumerator CreateIssue(CreateIssueRequest request, Action<ApiResult<IssueData>> onDone)
            {
                Calls.Add(request);
                if (Fail)
                {
                    onDone(ApiResult<IssueData>.Fail("无法连接后端", 0, "NETWORK"));
                    yield break;
                }
                onDone(ApiResult<IssueData>.Ok(new IssueData
                {
                    id = "iss_" + Calls.Count, title = request.title, description = request.description,
                    priority = request.priority, status = "open", position = request.position,
                    createdAt = "t", updatedAt = "t"
                }));
                yield break;
            }

            public System.Collections.IEnumerator FetchIssues(Action<ApiResult<List<IssueData>>> onDone)
            {
                onDone(ApiResult<List<IssueData>>.Ok(new List<IssueData>()));
                yield break;
            }

            public System.Collections.IEnumerator UpdateStatus(string id, IssueStatus status, Action<ApiResult<IssueData>> onDone)
            {
                onDone(ApiResult<IssueData>.Fail("n/a"));
                yield break;
            }
        }

        private static void TestPlacement()
        {
            var host = new GameObject("Host");
            var controller = host.AddComponent<IssuePlacementController>();
            var fake = new FakeApi();
            controller.Configure(fake);

            // 第一次点击
            var p1 = controller.PlaceAt(new Vector3(1.25f, 0.8f, 2.4f), Vector3.up, null);
            Check("第一次点击创建待提交标记", p1 != null && controller.Pending != null);
            Check("标记父节点 = 精确命中点",
                p1 != null && p1.Root.transform.position == new Vector3(1.25f, 0.8f, 2.4f),
                p1?.Root.transform.position.ToString());

            // 第二次点击
            var rejected = new List<string>();
            controller.ClickRejected += rejected.Add;
            var p2 = controller.PlaceAt(new Vector3(5, 0, 5), Vector3.up, null);
            Check("第二次点击不再创建标记", p2 == null && ReferenceEquals(controller.Pending, p1));
            Check("第二次点击给出提示", rejected.Count == 1);

            // 空标题
            ApiResult<IssueData> r0 = null;
            controller.SubmitFinished += (r, _) => r0 = r;
            controller.Submit("   ", null, IssuePriority.High);
            Pump(host);
            Check("空标题被前端拦下且不请求后端", r0 != null && !r0.Success && fake.Calls.Count == 0);
            Check("失败后标记保留", controller.Pending != null);

            // 正常提交
            ApiResult<IssueData> r1 = null;
            controller.SubmitFinished += (r, _) => r1 = r;
            controller.Submit("  1 号机柜温度异常 ", " 右侧偏高 ", IssuePriority.High);
            Pump(host);
            Check("提交成功", r1 != null && r1.Success, r1?.ErrorMessage);
            Check("请求体字段正确", fake.Calls.Count == 1
                && fake.Calls[0].title == "1 号机柜温度异常"
                && fake.Calls[0].description == "右侧偏高"
                && fake.Calls[0].priority == "high");
            Check("请求体坐标 = 命中点", fake.Calls.Count == 1
                && Math.Abs(fake.Calls[0].position.x - 1.25f) < 1e-4
                && Math.Abs(fake.Calls[0].position.y - 0.8f) < 1e-4
                && Math.Abs(fake.Calls[0].position.z - 2.4f) < 1e-4);
            Check("成功后待提交状态清空", controller.Pending == null);
            Check("成功后标记保留为已保存", controller.SavedMarkers.Count == 1 && controller.SavedMarkers[0].IsSaved);

            // 取消
            var p3 = controller.PlaceAt(new Vector3(0, 0, -2), Vector3.up, null);
            var cleared = 0;
            controller.PendingCleared += () => cleared++;
            controller.CancelPending();
            Check("取消后移除未保存标记", controller.Pending == null && cleared == 1 && (p3.Root == null || p3.Root.Destroyed));

            // 失败保留
            fake.Fail = true;
            controller.PlaceAt(new Vector3(2, 0, 2), Vector3.up, null);
            ApiResult<IssueData> r2 = null;
            controller.SubmitFinished += (r, _) => r2 = r;
            controller.Submit("失败用例", "", IssuePriority.Low);
            Pump(host);
            Check("提交失败给出错误", r2 != null && !r2.Success && r2.ErrorMessage.Contains("无法连接"));
            Check("提交失败保留标记以便重试", controller.Pending != null);
            Check("提交失败不进入已保存列表", controller.SavedMarkers.Count == 1);

            // 恢复历史
            var restored = controller.RestoreMarkers(new List<IssueData>
            {
                new IssueData { id = "in", title = "室内", priority = "medium", status = "open", position = new PositionData(new Vector3(1, 1, 1)) },
                new IssueData { id = "ar", title = "AR 坐标", priority = "low", status = "open", position = new PositionData(new Vector3(3245000, 12, 1400000)) },
            }, out var skipped);
            Check("历史恢复只接受场景内坐标", restored == 1 && skipped == 1, $"restored={restored} skipped={skipped}");

            // 标记颜色随状态变化
            var m = controller.SavedMarkers[0];
            var highOpen = m.CurrentColor;
            Check("高优先级待处理标记偏红", highOpen.r > highOpen.b, highOpen.ToString());
            m.Issue.status = "resolved";
            m.ApplyIssueStyle();
            var resolved = m.CurrentColor;
            Check("已解决标记变绿", resolved.g > resolved.r, resolved.ToString());
        }

        /// <summary>
        /// 模拟 Unity 的协程驱动：yield return 一个 IEnumerator 时应“进入”该子协程，
        /// 而不是把它当成“等一帧”。上面的验证第一次就是因为这个语义差异而误报。
        /// </summary>
        private static void Drive(System.Collections.IEnumerator root)
        {
            var stack = new Stack<System.Collections.IEnumerator>();
            stack.Push(root);
            var guard = 0;
            while (stack.Count > 0 && guard++ < 1000)
            {
                var top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception e) { Console.WriteLine("    协程异常: " + e.Message); return; }
                if (!moved)
                {
                    stack.Pop();
                    continue;
                }
                if (top.Current is System.Collections.IEnumerator child)
                {
                    stack.Push(child);
                }
            }
        }

        /// <summary>反复推进指定对象上的协程，直到不再有新协程（模拟多帧）。</summary>
        private static void Pump(GameObject host)
        {
            for (var i = 0; i < 20; i++)
            {
                foreach (var mb in AllMono(host))
                {
                    mb.RunPendingPublic();
                }
            }
        }

        private static IEnumerable<MonoBehaviour> AllMono(GameObject go)
        {
            yield return go.GetComponent<MonoBehaviour>();
            yield return go.GetComponent<IssuePlacementController>();
        }

        // ---------------- 5 ----------------
        private static void TestScene()
        {
            var go = new GameObject("Site");
            var composer = go.AddComponent<SceneComposer>();
            composer.Build();

            Check("可点击表面 >= 2", composer.Surfaces.Count >= 2, composer.Surfaces.Count.ToString());
            var missing = composer.Surfaces.Where(s => s.transform == null || s.transform.GetComponent<Collider>() == null).ToList();
            Check("所有表面都有 Collider", missing.Count == 0, string.Join(",", missing.Select(m => m.displayName)));
            Check("包含机柜", composer.Surfaces.Any(s => s.displayName.Contains("机柜")));
            Check("包含地面", composer.Surfaces.Any(s => s.deviceId == "FLOOR"));
            Check("机柜带编号与区域", composer.Surfaces.Any(s => s.deviceId.StartsWith("CAB-") && !string.IsNullOrEmpty(s.zone)));
            Console.WriteLine($"    表面 {composer.Surfaces.Count} 个，示例：" +
                              string.Join(" | ", composer.Surfaces.Take(6).Select(s => $"{s.zone}/{s.displayName}({s.deviceId})")));
        }

        // ---------------- 6 ----------------
        private static void TestHudFlow()
        {
            // 模拟一个真实的 Go 后端响应（成功），验证“点击→表单→提交→提示”整条链路
            UnityWebRequest.Server = (url, method, body) =>
            {
                if (method == "GET")
                {
                    return (200, "{\"ok\":true,\"data\":[],\"total\":0}", UnityWebRequest.Result.Success);
                }
                return (201,
                    "{\"ok\":true,\"data\":{\"id\":\"iss_1792\",\"title\":\"3 号机柜温度异常\",\"description\":\"\",\"priority\":\"high\",\"status\":\"open\",\"position\":{\"x\":0.5,\"y\":1.1,\"z\":2.0},\"createdAt\":\"2026-10-26T07:11:03Z\",\"updatedAt\":\"2026-10-26T07:11:03Z\"}}",
                    UnityWebRequest.Result.Success);
            };

            var root = new GameObject("Root");
            var player = new GameObject("Player");
            var input = player.AddComponent<InspectionInput>();
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.RayFactory = pos => new Ray(new Vector3(0, 1.6f, 0), new Vector3(0, 0, 1));

            var site = new GameObject("Site");
            var composer = site.AddComponent<SceneComposer>();
            composer.Build();
            // 用机柜面板作为射线命中目标
            var target = composer.Surfaces.First(s => s.displayName.Contains("机柜面板"));
            var hit = new RaycastHit
            {
                point = new Vector3(0.5f, 1.1f, 2.0f),
                normal = new Vector3(0, 0, -1),
                distance = 2f,
                collider = target.transform.GetComponent<Collider>()
            };
            Physics.Scene.Add(hit);

            var placement = root.AddComponent<IssuePlacementController>();
            placement.ConfigureDependencies(cam, input, composer, placement);
            var api = new IssueApi("localhost:8080");
            placement.Configure(api);

            var hudGo = new GameObject("HUD");
            var hud = hudGo.AddComponent<InspectionHUD>();
            hud.Configure(placement, input, cam, api.BaseUrl);

            // 调用 HUD 的 Start（模拟 Unity 生命周期）
            InvokeLifecycle(hud, "Start");

            // 从相机中心发射线并放置
            var placed = placement.TryPlaceAt(new Vector2(960, 540));
            Check("射线命中机柜面板并放置标记", placed && placement.Pending != null);
            Check("表单自动打开", hud.IsFormOpen);
            PumpAll(new[] { root, hudGo });

            // 找到表单里的输入框与提交按钮并点击
            var canvas = hudGo.transform.Find("HUDCanvas");
            var form = canvas?.Find("FormPanel");
            Check("HUD 画布与表单已构建", canvas != null && form != null && form.gameObject.activeSelf);

            var titleField = findTitleField(hud);
            Check("找到标题输入框", titleField != null);

            if (titleField != null)
            {
                titleField.text = "3 号机柜温度异常";
                var submit = findByLabel(form, "提交");
                Check("找到提交按钮", submit != null);
                submit?.Click();
                PumpAll(new[] { root, hudGo });

                Check("提交成功后提示包含“上报成功”", hud.CurrentToast.Contains("上报成功"), hud.CurrentToast);
                Check("提交成功后表单关闭", !hud.IsFormOpen);
                Check("提交成功后标记进入已保存列表", placement.SavedMarkers.Count == 1);
            }

            // 失败路径：后端返回 500
            UnityWebRequest.Server = (url, method, body) =>
                method == "GET"
                    ? (200, "{\"ok\":true,\"data\":[]}", UnityWebRequest.Result.Success)
                    : (500, "{\"ok\":false,\"code\":\"INTERNAL\",\"error\":\"failed to save issue\"}", UnityWebRequest.Result.ProtocolError);

            placement.TryPlaceAt(new Vector2(960, 540));
            var f2 = findTitleField(hud);
            f2.text = "失败用例";
            findByLabel(canvas?.Find("FormPanel"), "提交")?.Click();
            PumpAll(new[] { root, hudGo });
            Check("失败时提示包含“上报失败”与原因", hud.CurrentToast.Contains("上报失败") && hud.CurrentToast.Contains("failed to save issue"), hud.CurrentToast);
            Check("失败时保留待提交标记", placement.Pending != null);
            Check("失败时表单保持打开", hud.IsFormOpen);

            // UI 上的点击不放置标记
            var before = placement.Pending;
            var rejectReasons = new List<string>();
            placement.ClickRejected += rejectReasons.Add;
            input.PointerOverUI = true;
            input.SimulateClick(new Vector2(960, 540));
            InvokeLifecycle(placement, "Update");
            Check("指针在 UI 上时点击不会放置/替换标记", ReferenceEquals(before, placement.Pending));
            Check("UI 点击被拒绝并给出原因", rejectReasons.Count > 0, string.Join(",", rejectReasons));

            // 后端不可用时的启动提示
            UnityWebRequest.Server = (url, method, body) => (0, null, UnityWebRequest.Result.ConnectionError);
            var hud2Go = new GameObject("HUD2");
            var hud2 = hud2Go.AddComponent<InspectionHUD>();
            var placement2 = new GameObject("P2").AddComponent<IssuePlacementController>();
            placement2.Configure(new IssueApi("localhost:8080"));
            hud2.Configure(placement2, input, cam, "localhost:8080");
            InvokeLifecycle(hud2, "Start");
            hud2.ShowBackendStatus(false, "http://localhost:8080 不可用：无法连接后端");
            // Toast 是协程驱动的，需要推进一帧才会写入文字
            PumpAll(new[] { hud2Go });
            Check("后端不可用时给出明确提示", hud2.CurrentToast.Contains("后端不可用"), hud2.CurrentToast);
        }

        /// <summary>
        /// 真机联调：不走 Server 桩，让 UnityWebRequest 直接访问 localhost:8080 的真实 Go 服务。
        /// 需要先启动后端；未启动时该项标记为跳过（不视为失败）。
        /// </summary>
        private static void TestRealBackend()
        {
            UnityWebRequest.Server = null; // 恢复“真实网络”
            var realApi = new IssueApi("localhost:8080");

            ApiResult<List<IssueData>> list = null;
            DriveAndWait(realApi.FetchIssues(r => list = r));
            if (list == null || !list.Success)
            {
                Console.WriteLine("  SKIP  后端未启动（" + (list?.ErrorMessage ?? "timeout") + "）");
                return;
            }

            var marker = "unity-e2e-" + DateTime.UtcNow.Ticks;
            ApiResult<IssueData> created = null;
            DriveAndWait(realApi.CreateIssue(new CreateIssueRequest
            {
                title = "联调用例 " + marker,
                description = "由 Unity 客户端代码直接上报",
                priority = "high",
                position = new PositionData(new Vector3(-1.8f, 1.1f, 0.5f))
            }, r => created = r));

            Check("真实后端：创建问题成功", created != null && created.Success, created?.ErrorMessage);
            if (created == null || !created.Success) return;

            Check("真实后端：返回 open 状态", created.Data.status == "open", created.Data.status);
            Check("真实后端：坐标往返一致",
                Math.Abs(created.Data.position.x + 1.8f) < 1e-4 && Math.Abs(created.Data.position.z - 0.5f) < 1e-4,
                created.Data.position.ToString());

            // 管理端把状态改成进行中，Unity 重新拉取应看到变化（三端同一份数据）
            ApiResult<IssueData> patched = null;
            DriveAndWait(realApi.UpdateStatus(created.Data.id, IssueStatus.InProgress, r => patched = r));
            Check("真实后端：修改状态成功", patched != null && patched.Success, patched?.ErrorMessage);
            Check("真实后端：状态变为 in_progress", patched != null && patched.Data.status == "in_progress", patched?.Data.status);

            ApiResult<List<IssueData>> again = null;
            DriveAndWait(realApi.FetchIssues(r => again = r));
            var found = again?.Data?.FirstOrDefault(i => i.id == created.Data.id);
            Check("真实后端：列表里能查回该问题且状态已更新",
                found != null && found.status == "in_progress", found?.status ?? "not found");
        }

        /// <summary>驱动一个异步协程直到完成（异步操作需要真实等待）。</summary>
        private static void DriveAndWait(System.Collections.IEnumerator root)
        {
            var stack = new Stack<System.Collections.IEnumerator>();
            stack.Push(root);
            var guard = 0;
            while (stack.Count > 0 && guard++ < 100000)
            {
                var top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception e) { Console.WriteLine("    异常: " + e.Message); return; }
                if (!moved) { stack.Pop(); continue; }
                if (top.Current is System.Collections.IEnumerator child)
                {
                    if (child.GetType().Name == "UnityWebRequestAsyncOperation" || child is AsyncOperation)
                    {
                        // 真实 AsyncOperation：使用 .NET 的同步等待，避免阻塞式轮询
                        WaitForAsync(child);
                        continue;
                    }
                    stack.Push(child);
                }
            }
        }

        private static void WaitForAsync(System.Collections.IEnumerator op)
        {
            var waited = 0;
            while (op.MoveNext() && waited++ < 2000)
            {
                System.Threading.Thread.Sleep(5);
            }
        }

        private static UnityEngine.UI.InputField findTitleField(InspectionHUD hud)
        {
            // 按对象名查找，而不是“第一个 InputField”（占位文本其实也是 Text，不是 InputField，
            // 但按名字查找更稳，也顺带验证 UI 层级命名是否符合预期）
            return FindAll<UnityEngine.UI.InputField>(hud.gameObject)
                .FirstOrDefault(f => f.gameObject.name == "TitleField");
        }

        private static UnityEngine.UI.Button findByLabel(Transform panel, string label)
        {
            if (panel == null) return null;
            var buttons = FindAll<UnityEngine.UI.Button>(panel.gameObject);
            foreach (var b in buttons)
            {
                var t = b.GetComponentInChildren<UnityEngine.UI.Text>();
                if (t != null && t.text.StartsWith(label)) return b;
            }
            return null;
        }

        private static List<T> FindAll<T>(GameObject root) where T : Component
        {
            var result = new List<T>();
            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                var c = g.GetComponent<T>();
                if (c != null) result.Add(c);
                for (var i = 0; i < g.transform.childCount; i++)
                {
                    var field = typeof(Transform).GetField("_children",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var list = (List<Transform>)field.GetValue(g.transform);
                    stack.Push(list[i].gameObject);
                }
            }
            return result;
        }

        private static T FindComponent<T>(GameObject root) where T : Component
        {
            var all = FindAll<T>(root);
            return all.FirstOrDefault();
        }

        private static void InvokeLifecycle(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            m?.Invoke(target, null);
        }

        private static void PumpAll(IEnumerable<GameObject> roots)
        {
            for (var i = 0; i < 30; i++)
            {
                foreach (var root in roots)
                {
                    foreach (var mb in FindAll<MonoBehaviour>(root))
                    {
                        mb.RunPendingPublic();
                    }
                }
            }
        }
    }
}







