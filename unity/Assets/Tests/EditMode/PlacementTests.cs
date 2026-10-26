// 放置控制器的 EditMode 测试。
//
// 这些测试用假 API（FakeIssueApi）驱动“上报流程”，覆盖题目最看重的几件事：
//   - 一次点击只创建一个待提交标记
//   - 取消后未保存标记被移除
//   - 提交成功后标记保留并变成已保存
//   - 提交失败时标记与内容保留（可重试），并给出明确错误
//   - 空标题不会打到后端
//
// 注意：这些测试会真实创建 GameObject，因此归类为 EditMode 但需要场景里没有物理对象
// （避免射线命中意外物体）。射线相关的真实交互在 PlayMode 中人工验证。
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Inspection.Tests
{
    /// <summary>可编程的假后端：记录调用、按需返回成功/失败。</summary>
    public class FakeIssueApi : IIssueApi
    {
        public string BaseUrl => "http://fake.local";
        public readonly List<CreateIssueRequest> CreateCalls = new List<CreateIssueRequest>();
        public readonly List<(string, IssueStatus)> StatusCalls = new List<(string, IssueStatus)>();

        public bool FailCreate;
        public string FailMessage = "无法连接后端";
        public List<IssueData> ExistingIssues = new List<IssueData>();

        public IEnumerator CreateIssue(CreateIssueRequest request, System.Action<ApiResult<IssueData>> onDone)
        {
            CreateCalls.Add(request);
            if (FailCreate)
            {
                onDone?.Invoke(ApiResult<IssueData>.Fail(FailMessage, 0, "NETWORK"));
                yield break;
            }
            var issue = new IssueData
            {
                id = "iss_" + CreateCalls.Count,
                title = request.title,
                description = request.description,
                priority = request.priority,
                status = "open",
                position = request.position,
                createdAt = "2026-10-26T00:00:00Z",
                updatedAt = "2026-10-26T00:00:00Z"
            };
            onDone?.Invoke(ApiResult<IssueData>.Ok(issue));
        }

        public IEnumerator FetchIssues(System.Action<ApiResult<List<IssueData>>> onDone)
        {
            onDone?.Invoke(ApiResult<List<IssueData>>.Ok(new List<IssueData>(ExistingIssues)));
            yield break;
        }

        public IEnumerator UpdateStatus(string id, IssueStatus status, System.Action<ApiResult<IssueData>> onDone)
        {
            StatusCalls.Add((id, status));
            onDone?.Invoke(ApiResult<IssueData>.Fail("not implemented in fake", 501));
            yield break;
        }
    }

    public class PlacementControllerTests
    {
        private GameObject _root;
        private IssuePlacementController _controller;
        private FakeIssueApi _api;
        private readonly List<GameObject> _created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("TestRoot");
            _controller = _root.AddComponent<IssuePlacementController>();
            _api = new FakeIssueApi();
            _controller.Configure(_api);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _created.Clear();
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        private PendingMarker PlaceAt(Vector3 point, Vector3 normal = default)
        {
            var n = normal == default ? Vector3.up : normal;
            var p = _controller.PlaceAt(point, n, null);
            if (p != null && p.Root != null)
            {
                _created.Add(p.Root);
            }
            return p;
        }

        [Test]
        public void FirstClickCreatesOnePendingMarker()
        {
            var p = PlaceAt(new Vector3(1.25f, 0.8f, 2.4f), Vector3.up);
            Assert.IsNotNull(p);
            Assert.IsNotNull(_controller.Pending);

            // 标记父节点的位置必须等于射线命中点（题目：在实际命中位置放置标记）
            Assert.AreEqual(1.25f, p.Root.transform.position.x, 1e-4f);
            Assert.AreEqual(0.8f, p.Root.transform.position.y, 1e-4f);
            Assert.AreEqual(2.4f, p.Root.transform.position.z, 1e-4f);
        }

        [Test]
        public void SecondClickDoesNotCreateAnotherPendingMarker()
        {
            var first = PlaceAt(new Vector3(1, 0, 0));
            var rejected = new List<string>();
            _controller.ClickRejected += rejected.Add;

            var second = PlaceAt(new Vector3(2, 0, 0));

            Assert.IsNull(second, "已有待提交标记时不应再创建第二个");
            Assert.AreSame(first, _controller.Pending);
            Assert.AreEqual(1, rejected.Count);
            StringAssert.Contains("待提交", rejected[0]);
        }

        [Test]
        public void CancelRemovesPendingMarker()
        {
            var p = PlaceAt(new Vector3(1, 0, 0));
            var cleared = 0;
            _controller.PendingCleared += () => cleared++;

            _controller.CancelPending();

            Assert.AreEqual(1, cleared);
            Assert.IsNull(_controller.Pending);
            // Destroy 在 EditMode 下是延迟的，这里只断言对象被标记销毁或已不存在
            Assert.IsTrue(p.Root == null || p.Root.name == null, "未保存的标记应被移除");
        }

        [Test]
        public void SubmitWithEmptyTitleIsRejectedBeforeCallingBackend()
        {
            PlaceAt(new Vector3(1, 0, 0));
            ApiResult<IssueData> result = null;
            _controller.SubmitFinished += (r, _) => result = r;

            _controller.Submit("   ", "desc", IssuePriority.High);

            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, _api.CreateCalls.Count, "空标题不应请求后端");
            Assert.IsNotNull(_controller.Pending, "失败后标记应保留以便重试");
        }

        // 注意：Submit 内部会 StartCoroutine 发起网络请求，而 EditMode 下没有帧循环，
        // 协程不会推进，必须用 [UnityTest] + yield return null 让它跑完。
        // 这正是 Unity Test Runner 暴露出来的问题（见 AI_LOG P-013）。
        [UnityTest]
        public IEnumerator SuccessfulSubmitKeepsMarkerAndSendsExactPosition()
        {
            var point = new Vector3(1.25f, 0.8f, 2.4f);
            PlaceAt(point);

            ApiResult<IssueData> result = null;
            _controller.SubmitFinished += (r, _) => result = r;
            _controller.Submit("  1 号机柜温度异常  ", " 右侧偏高 ", IssuePriority.High);
            yield return null;

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, _api.CreateCalls.Count);

            var sent = _api.CreateCalls[0];
            Assert.AreEqual("1 号机柜温度异常", sent.title, "标题应去掉首尾空格");
            Assert.AreEqual("右侧偏高", sent.description);
            Assert.AreEqual("high", sent.priority);
            Assert.AreEqual(1.25f, sent.position.x, 1e-4f);
            Assert.AreEqual(0.8f, sent.position.y, 1e-4f);
            Assert.AreEqual(2.4f, sent.position.z, 1e-4f);

            Assert.IsNull(_controller.Pending, "提交成功后应清空待提交状态");
            Assert.AreEqual(1, _controller.SavedMarkers.Count);
            Assert.IsTrue(_controller.SavedMarkers[0].IsSaved);
            Assert.AreEqual("iss_1", _controller.SavedMarkers[0].Issue.id);
        }

        [UnityTest]
        public IEnumerator FailedSubmitKeepsMarkerAndReportsError()
        {
            _api.FailCreate = true;
            _api.FailMessage = "无法连接后端 http://localhost:8080";
            PlaceAt(new Vector3(0, 0, 0));

            ApiResult<IssueData> result = null;
            _controller.SubmitFinished += (r, _) => result = r;
            _controller.Submit("标题", "", IssuePriority.Low);
            yield return null;

            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("无法连接后端", result.ErrorMessage);
            Assert.IsNotNull(_controller.Pending, "失败时应保留标记");
            Assert.AreEqual(0, _controller.SavedMarkers.Count);
        }

        [Test]
        public void RestoreMarkersSkipsOutOfSceneCoordinates()
        {
            var issues = new List<IssueData>
            {
                new IssueData { id = "in", title = "室内", priority = "high", status = "open", position = new PositionData(new Vector3(1, 1, 2)) },
                new IssueData { id = "out", title = "AR 真实世界坐标", priority = "low", status = "open", position = new PositionData(new Vector3(3245000f, 12f, 1400000f)) }
            };

            var restored = _controller.RestoreMarkers(issues, out var skipped, out _);

            Assert.AreEqual(1, restored);
            Assert.AreEqual(1, skipped);
            Assert.AreEqual("in", _controller.SavedMarkers[0].Issue.id);
        }

        [Test]
        public void RestoreMarkersIsIdempotent()
        {
            var issues = new List<IssueData>
            {
                new IssueData { id = "x", title = "t", priority = "medium", status = "open", position = new PositionData(new Vector3(0, 1, 0)) }
            };
            _controller.RestoreMarkers(issues, out _, out _);
            _controller.RestoreMarkers(issues, out _, out _);

            Assert.AreEqual(1, _controller.SavedMarkers.Count, "同一问题不应重复生成标记");
        }

        [Test]
        public void PriorityAffectsMarkerColor()
        {
            var issue = new IssueData
            {
                id = "h", title = "高优先级", priority = "high", status = "open",
                position = new PositionData(Vector3.zero)
            };
            var go = new GameObject("M");
            _created.Add(go);
            var marker = go.AddComponent<IssueMarker>();
            marker.Build(false);
            marker.Bind(issue, true);

            var c = marker.CurrentColor;
            Assert.Greater(c.r, c.b, "高优先级应偏红");

            issue.status = "resolved";
            marker.ApplyIssueStyle();
            var c2 = marker.CurrentColor;
            Assert.Greater(c2.g, c2.r, "已解决应偏绿");
        }
    }

    public class SceneComposerTests
    {
        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        [Test]
        public void SceneHasAtLeastTwoClickableSurfaces()
        {
            _go = new GameObject("Site");
            var composer = _go.AddComponent<SceneComposer>();
            composer.Build();

            Assert.GreaterOrEqual(composer.Surfaces.Count, 2, "题目要求至少两个可观察/可点击对象");

            // 注意：不要用 surfaces.Any(...)。IReadOnlyList<T> 上的 LINQ 扩展方法
            // 仅在 .NET Core 3.0+ 提供，Unity 2022.3 (Mono/IL2CPP, .NET Standard 2.1) 下可能编译不过。
            var hasCabinet = false;
            var hasFloor = false;
            for (var i = 0; i < composer.Surfaces.Count; i++)
            {
                var s = composer.Surfaces[i];
                if (s.displayName != null && s.displayName.Contains("机柜")) hasCabinet = true;
                if (s.deviceId == "FLOOR") hasFloor = true;
            }
            Assert.IsTrue(hasCabinet, "应包含机柜");
            Assert.IsTrue(hasFloor, "应包含地面");
        }

        [Test]
        public void EverySurfaceHasCollider()
        {
            _go = new GameObject("Site");
            var composer = _go.AddComponent<SceneComposer>();
            composer.Build();

            foreach (var s in composer.Surfaces)
            {
                Assert.IsNotNull(s.transform, "SurfaceInfo 缺少 transform");
                Assert.IsNotNull(s.transform.GetComponent<Collider>(),
                    $"{s.displayName} 没有 Collider，射线无法命中");
            }
        }

        // ---------- HUD 表单布局 ----------
        // 回归保护：曾经出现过 SetRect 的 offsetMax 传成正数，
        // 导致标题输入框高度变负（被压扁）、坐标文字与“提交/取消”按钮行重叠，
        // 用户根本看不到也点不到输入框。这里直接用真实布局矩形做断言。

        [UnityTest]
        public IEnumerator FormLayoutHasNoOverlapsAndPositiveHeights()
        {
            var hudGo = new GameObject("HUD");
            var hud = hudGo.AddComponent<InspectionHUD>();
            hud.Configure(null, null, null, "http://localhost:8080");
            // EditMode 下 MonoBehaviour.Start 不会自动执行，这里显式构建 UI
            hud.BuildUIPublic();
            yield return null;

            var panel = FindChild(hudGo.transform, "FormPanel");
            Assert.IsNotNull(panel, "找不到 FormPanel");

            var titleField = FindChild(panel.transform, "TitleField");
            var descField = FindChild(panel.transform, "DescField");
            var coord = FindChild(panel.transform, "Coord");
            var actions = FindChild(panel.transform, "Actions");

            Assert.IsNotNull(titleField, "找不到标题输入框");
            Assert.IsNotNull(descField, "找不到描述输入框");
            Assert.IsNotNull(coord, "找不到坐标文字");
            Assert.IsNotNull(actions, "找不到按钮行");

            // 注意：RectTransform 是拉伸锚点，(0,0)-(1,1)，所以 rect.y 是“相对自身锚点”的，
            // 不能直接比较。统一换算成“距面板底边”，才是可比较的坐标系。
            var panelH = panel.GetComponent<RectTransform>().rect.height;
            var title = ToPanelCoords(titleField.GetComponent<RectTransform>(), panelH);
            var desc = ToPanelCoords(descField.GetComponent<RectTransform>(), panelH);
            var coordBox = ToPanelCoords(coord.GetComponent<RectTransform>(), panelH);
            var actionBox = ToPanelCoords(actions.GetComponent<RectTransform>(), panelH);

            // 1) 输入框必须有正的高度，并且足够高能看清文字
            Assert.Greater(title.height, 20f,
                $"标题输入框高度异常（{title.height}），会被压扁到看不见");
            Assert.Greater(desc.height, 40f,
                $"描述输入框高度异常（{desc.height}）");

            // 2) 标题框与描述框不得重叠（标题在描述上方，故标题底部应高于描述顶部）
            Assert.GreaterOrEqual(title.bottom, desc.top - 0.01f,
                $"标题输入框(底={title.bottom}) 与描述输入框(顶={desc.top}) 重叠");

            // 3) 坐标文字不得与底部按钮行重叠 —— 本轮修掉的真实缺陷
            Assert.Greater(coordBox.bottom, actionBox.top,
                $"坐标文字(底={coordBox.bottom}) 与按钮行(顶={actionBox.top}) 重叠");

            // 4) 按钮行必须是正常高度，不能横跨整个面板
            Assert.LessOrEqual(actionBox.height, 60f,
                $"按钮行高度异常（{actionBox.height}），应当只有一行按钮那么高");

            // 5) 所有控件都要落在面板内
            foreach (var (name, box) in new[]
                     {
                         ("TitleField", title), ("DescField", desc), ("Coord", coordBox), ("Actions", actionBox)
                     })
            {
                Assert.GreaterOrEqual(box.bottom, -0.01f, $"{name} 超出面板底部");
                Assert.LessOrEqual(box.top, panelH + 0.01f, $"{name} 超出面板顶部");
            }

            Object.DestroyImmediate(hudGo);
        }

        /// <summary>把拉伸锚点控件的 rect 换算成“距面板底边”的区间。</summary>
        private static (float bottom, float top, float height) ToPanelCoords(RectTransform rt, float panelHeight)
        {
            // offsetMin.y 就是距底距离；offsetMax.y 为负，表示距顶距离
            var bottom = rt.offsetMin.y;
            var top = panelHeight + rt.offsetMax.y;
            return (bottom, top, top - bottom);
        }

        private static GameObject FindChild(Transform root, string name)
        {
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == name) return child.gameObject;
                var deep = FindChild(child, name);
                if (deep != null) return deep;
            }
            return null;
        }

        // ---------- 状态同步 ----------
        // 回归保护：管理端把问题改成「已解决」后，按 F5 刷新，Unity 里的标记必须变色。
        // 以前 RestoreMarkers 遇到已存在的 id 直接 continue，导致：
        //   1) 标记从不刷新，状态改了也看不出来；
        //   2) 返回值恒为 0，HUD 误报“后端暂无历史问题”。

        private class CoroutineHost : MonoBehaviour { }

        [Test]
        public void RestoringExistingIssueRefreshesStatusColorInsteadOfSkipping()
        {
            _go = new GameObject("Placement");
            var placement = _go.AddComponent<IssuePlacementController>();
            var host = new GameObject("Host");
            placement.ConfigureDependencies(null, null, null, host.AddComponent<CoroutineHost>());

            var issue = new IssueData
            {
                id = "iss_same",
                title = "机柜温度异常",
                priority = "high",
                status = "open",
                position = new PositionData { x = 0f, y = 1f, z = 0f }
            };

            // 第一次恢复：新建标记
            var first = placement.RestoreMarkers(new[] { issue }, out var skipped1, out _);
            Assert.AreEqual(1, first, "首次恢复应创建 1 个标记");
            Assert.AreEqual(0, skipped1);
            Assert.AreEqual(1, placement.SavedMarkers.Count);

            var marker = placement.SavedMarkers[0];
            var openColor = marker.CurrentColor;

            // 管理端把状态改成 resolved，再刷新一次
            issue.status = "resolved";
            var second = placement.RestoreMarkers(new[] { issue }, out var skipped2, out _);

            Assert.AreEqual(1, second,
                "已存在的标记被刷新时也要计入返回值，否则 HUD 会误报“后端暂无历史问题”");
            Assert.AreEqual(0, skipped2);
            Assert.AreEqual(1, placement.SavedMarkers.Count, "不应产生重复标记");

            Assert.AreNotEqual(openColor, marker.CurrentColor,
                "状态从 open 改成 resolved 后，标记颜色必须变化");
            Assert.AreEqual("resolved", marker.Issue.status, "标记上的问题数据应被更新");

            Object.DestroyImmediate(host);
            Object.DestroyImmediate(_go);
            _go = null;
        }

        // ---------- 删除同步 ----------
        // 回归保护：管理端删除问题后按 F5，Unity 里的标记必须消失。
        // 以前 RestoreMarkers 只做「新增 + 刷新」，从不删除，
        // 已删除问题的标记会一直留在场景里（球不消失）。

        [Test]
        public void RestoringAfterBackendDeletionRemovesOrphanedMarker()
        {
            _go = new GameObject("Placement");
            var placement = _go.AddComponent<IssuePlacementController>();
            var host = new GameObject("Host");
            placement.ConfigureDependencies(null, null, null, host.AddComponent<CoroutineHost>());

            var a = new IssueData
            {
                id = "iss_a", title = "机柜温度异常", priority = "high", status = "open",
                position = new PositionData { x = 0f, y = 1f, z = 0f }
            };
            var b = new IssueData
            {
                id = "iss_b", title = "地面有积水", priority = "low", status = "open",
                position = new PositionData { x = 2f, y = 0.1f, z = 1f }
            };

            placement.RestoreMarkers(new[] { a, b }, out _, out var removed0);
            Assert.AreEqual(2, placement.SavedMarkers.Count);
            Assert.AreEqual(0, removed0, "首次同步不应移除任何标记");

            // 管理端删掉了 iss_a，后端只剩 iss_b
            var count = placement.RestoreMarkers(new[] { b }, out _, out var removed1);

            Assert.AreEqual(1, removed1, "已删除问题的标记应被移除");
            Assert.AreEqual(1, placement.SavedMarkers.Count, "场景里只应剩下未删除的那一个标记");
            Assert.AreEqual("iss_b", placement.SavedMarkers[0].Issue.id);
            Assert.AreEqual(1, count, "仍存在的问题应计入同步数量");

            Object.DestroyImmediate(host);
            Object.DestroyImmediate(_go);
            _go = null;
        }
    }
}
