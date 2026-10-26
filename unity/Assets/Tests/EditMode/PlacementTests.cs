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

        [Test]
        public void SuccessfulSubmitKeepsMarkerAndSendsExactPosition()
        {
            var point = new Vector3(1.25f, 0.8f, 2.4f);
            PlaceAt(point);

            ApiResult<IssueData> result = null;
            _controller.SubmitFinished += (r, _) => result = r;
            _controller.Submit("  1 号机柜温度异常  ", " 右侧偏高 ", IssuePriority.High);

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

        [Test]
        public void FailedSubmitKeepsMarkerAndReportsError()
        {
            _api.FailCreate = true;
            _api.FailMessage = "无法连接后端 http://localhost:8080";
            PlaceAt(new Vector3(0, 0, 0));

            ApiResult<IssueData> result = null;
            _controller.SubmitFinished += (r, _) => result = r;
            _controller.Submit("标题", "", IssuePriority.Low);

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

            var restored = _controller.RestoreMarkers(issues, out var skipped);

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
            _controller.RestoreMarkers(issues, out _);
            _controller.RestoreMarkers(issues, out _);

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
    }
}

