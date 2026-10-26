using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Inspection
{
    /// <summary>待提交的标记（射线命中后、用户点“提交”之前的状态）。</summary>
    public class PendingMarker
    {
        public GameObject Root;
        public IssueMarker Marker;
        public Vector3 HitPoint;
        public Vector3 HitNormal;
        public SurfaceInfo Surface;
    }

    /// <summary>
    /// 放置标记 + 上报问题的核心逻辑。题目 3.1 必做项 3~8 全部落在这里。
    ///
    /// 状态机：
    ///   Idle ──左键点击(未拖拽/不在UI上)──► Placing(有 1 个待提交标记)
    ///   Placing ──提交成功──► Idle(标记绑定问题数据，持久显示)
    ///   Placing ──取消 / 提交失败后放弃──► Idle(移除未保存标记)
    ///   Placing ──再次点击──► 被忽略并提示“请先提交或取消”（保证一次有效点击最多一个待提交标记）
    ///
    /// UI 与控制器通过事件解耦：控制器只负责“发生了什么”，由 HUD 决定怎么显示。
    /// </summary>
    public class IssuePlacementController : MonoBehaviour
    {
        [Header("射线")]
        [Tooltip("射线最大距离（米）")]
        public float RayDistance = 60f;

        [Tooltip("这些 Layer 上的对象可以被点击（含 Default）")]
        public LayerMask HitMask = ~0;

        [Header("依赖")]
        public Camera ViewCamera;
        public InspectionInput InputDriver;
        public SceneComposer Composer;
        public MonoBehaviour ApiBehaviour;     // 挂载 IssueApi 的协程运行者（通常就是本对象）

        [Header("外观")]
        public Material TempMarkerMaterial;
        public Material SavedMarkerMaterial;

        // ---- 事件 ----
        /// <summary>点击命中了某个表面，但还没有创建标记（用于 UI 显示“命中：A3 机柜”）。</summary>
        public event Action<RaycastHit, SurfaceInfo> HoverTargetChanged;
        /// <summary>成功放下一个待提交标记。</summary>
        public event Action<PendingMarker> MarkerPlaced;
        /// <summary>待提交标记被取消/移除。</summary>
        public event Action PendingCleared;
        /// <summary>提交开始。</summary>
        public event Action SubmitStarted;
        /// <summary>提交结束（成功或失败都会触发，success 区分）。</summary>
        public event Action<ApiResult<IssueData>, string> SubmitFinished;
        /// <summary>一次被拒绝的点击（已有待提交标记、点在 UI 上等），用于给用户提示。</summary>
        public event Action<string> ClickRejected;
        /// <summary>历史标记加载完成（从后端恢复）。</summary>
        public event Action<int> HistoryLoaded;
        /// <summary>后端已删除的问题，其标记已从场景中移除（参数为移除数量）。</summary>
        public event Action<int> MarkersRemoved;
        /// <summary>用户点击了一个已保存的标记（用于显示详情）。</summary>
        public event Action<IssueMarker> MarkerPicked;

        // ---- 状态 ----
        public PendingMarker Pending { get; private set; }
        public bool IsSubmitting { get; private set; }
        public RaycastHit? LastHoverHit { get; private set; }
        public SurfaceInfo LastHoverSurface { get; private set; }

        private readonly List<IssueMarker> _savedMarkers = new List<IssueMarker>();
        public IReadOnlyList<IssueMarker> SavedMarkers => _savedMarkers;

        /// <summary>当前场景里可点击表面的数量（0 表示场景还没构建完成）。</summary>
        public int SurfaceCount => Composer != null && Composer.Surfaces != null ? Composer.Surfaces.Count : 0;

        private IIssueApi _api;
        private Transform _markerRoot;

        /// <summary>注入 API（测试用假实现；运行时为 IssueApi）。</summary>
        public void Configure(IIssueApi api)
        {
            _api = api;
        }

        public void ConfigureDependencies(Camera cam, InspectionInput input, SceneComposer composer, MonoBehaviour coroutineHost)
        {
            ViewCamera = cam;
            InputDriver = input;
            Composer = composer;
            ApiBehaviour = coroutineHost != null ? coroutineHost : this;
        }

        private void Awake()
        {
            if (_markerRoot == null)
            {
                _markerRoot = new GameObject("Markers").transform;
                _markerRoot.SetParent(transform, false);
            }
            if (ApiBehaviour == null)
            {
                ApiBehaviour = this;
            }
        }

        private void Update()
        {
            if (ViewCamera == null || InputDriver == null)
            {
                return;
            }

            UpdateHover();
            HandleInput();
        }

        /// <summary>每帧做一次中心/鼠标位置射线，仅用于高亮与提示。</summary>
        private void UpdateHover()
        {
            var ray = ViewCamera.ScreenPointToRay(InputDriver.PointerOverUI
                ? new Vector3(Screen.width / 2f, Screen.height / 2f, 0f)
                : (Vector3)Input.mousePosition);

            if (Physics.Raycast(ray, out var hit, RayDistance, HitMask, QueryTriggerInteraction.Ignore))
            {
                LastHoverHit = hit;
                LastHoverSurface = Composer != null ? Composer.Describe(hit.collider) : null;
                HoverTargetChanged?.Invoke(hit, LastHoverSurface);
            }
            else
            {
                LastHoverHit = null;
                LastHoverSurface = null;
                HoverTargetChanged?.Invoke(default, null);
            }
        }

        /// <summary>
        /// 处理一次左键点击：优先命中已保存的标记（查看详情），
        /// 否则在命中表面放置新的待提交标记。
        /// </summary>
        private void HandleInput()
        {
            if (!InputDriver.ClickedThisFrame)
            {
                return;
            }

            // 双保险：即使输入层没拦住，UI 上的点击也绝不放置标记。
            if (InputDriver.PointerOverUI)
            {
                RejectClick("点击来自 UI，已忽略");
                return;
            }

            if (IsSubmitting)
            {
                RejectClick("正在提交，请稍候");
                return;
            }

            // 先看是否点在了已有标记上（加分项：点标记看详情）
            var picked = PickMarker(InputDriver.ClickScreenPosition);
            if (picked != null && !ReferenceEquals(picked, Pending?.Marker))
            {
                MarkerPicked?.Invoke(picked);
                return;
            }

            // “一次有效点击最多创建一个待提交标记”
            if (Pending != null)
            {
                RejectClick("已有一个待提交标记：请先提交或取消");
                return;
            }

            TryPlaceAt(InputDriver.ClickScreenPosition);
        }

        /// <summary>
        /// 在屏幕位置做射线检测，命中则在**精确命中点**创建待提交标记。
        /// 返回是否成功放置（供测试断言）。
        /// </summary>
        public bool TryPlaceAt(Vector2 screenPos)
        {
            if (ViewCamera == null)
            {
                RejectClick("相机未就绪");
                return false;
            }

            var ray = ViewCamera.ScreenPointToRay(screenPos);
            if (!Physics.Raycast(ray, out var hit, RayDistance, HitMask, QueryTriggerInteraction.Ignore))
            {
                RejectClick("此处没有可标注的表面，请对着设备、墙面或地面点击");
                return false;
            }

            PlaceAt(hit.point, hit.normal, Composer != null ? Composer.Describe(hit.collider) : null);
            return true;
        }

        /// <summary>在给定世界坐标创建待提交标记（与 TryPlaceAt 分离，方便测试与将来的 AR 复用）。</summary>
        public PendingMarker PlaceAt(Vector3 hitPoint, Vector3 hitNormal, SurfaceInfo surface)
        {
            if (Pending != null)
            {
                RejectClick("已有一个待提交标记：请先提交或取消");
                return null;
            }

            var root = new GameObject("PendingMarker");
            root.transform.SetParent(_markerRoot != null ? _markerRoot : transform, false);
            root.transform.position = hitPoint;  // 父节点 = 精确命中点
            root.transform.rotation = Quaternion.identity;
            root.layer = gameObject.layer;

            var marker = root.AddComponent<IssueMarker>();
            marker.CoreRadius = 0.09f;
            marker.SetSurfaceNormal(hitNormal);
            // 父节点 = 精确命中点；可见的球/指针由 IssueMarker 沿法线做视觉偏移
            marker.Build(true);

            var pending = new PendingMarker
            {
                Root = root,
                Marker = marker,
                HitPoint = hitPoint,
                HitNormal = hitNormal,
                Surface = surface
            };
            Pending = pending;
            MarkerPlaced?.Invoke(pending);
            return pending;
        }

        /// <summary>取消待提交标记：移除未保存的标记对象，回到 Idle。</summary>
        public void CancelPending()
        {
            if (Pending == null)
            {
                return;
            }

            if (Pending.Root != null)
            {
                ObjectUtil.SafeDestroy(Pending.Root);
            }
            Pending = null;
            PendingCleared?.Invoke();
        }

        /// <summary>
        /// 提交待提交标记。表单校验由调用方（HUD）先做，这里再兜底一次，
        /// 保证空标题绝不会打到后端。
        /// </summary>
        public void Submit(string title, string description, IssuePriority priority)
        {
            if (Pending == null)
            {
                SubmitFinished?.Invoke(ApiResult<IssueData>.Fail("没有待提交的标记"), null);
                return;
            }

            if (IsSubmitting)
            {
                SubmitFinished?.Invoke(ApiResult<IssueData>.Fail("上一次提交还未完成"), null);
                return;
            }

            var trimmed = (title ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                SubmitFinished?.Invoke(ApiResult<IssueData>.Fail("标题不能为空"), null);
                return;
            }
            if (trimmed.Length > 120)
            {
                SubmitFinished?.Invoke(ApiResult<IssueData>.Fail("标题不能超过 120 个字符"), null);
                return;
            }

            if (_api == null)
            {
                SubmitFinished?.Invoke(ApiResult<IssueData>.Fail("未配置后端地址"), null);
                return;
            }

            var request = new CreateIssueRequest
            {
                title = trimmed,
                description = (description ?? string.Empty).Trim(),
                priority = IssueEnums.ToWire(priority),
                // 使用最初命中的坐标（而不是标记当前/装饰后的位置）
                position = new PositionData(Pending.HitPoint)
            };

            IsSubmitting = true;
            SubmitStarted?.Invoke();
            var host = ApiBehaviour != null ? ApiBehaviour : this;
            host.StartCoroutine(SubmitRoutine(request, Pending));
        }

        private IEnumerator SubmitRoutine(CreateIssueRequest request, PendingMarker pendingSnapshot)
        {
            var done = false;
            ApiResult<IssueData> result = null;
            yield return _api.CreateIssue(request, r => { result = r; done = true; });

            if (!done || result == null)
            {
                result = ApiResult<IssueData>.Fail("请求未完成");
            }

            IsSubmitting = false;

            // 失败：保留标记与已填写内容，方便用户重试（不删除，避免丢失现场）
            if (!result.Success)
            {
                SubmitFinished?.Invoke(result, pendingSnapshot?.Surface?.displayName);
                yield break;
            }

            // 成功：标记升级为“已保存”，绑定后端返回的 id/状态
            if (pendingSnapshot != null && pendingSnapshot.Marker != null)
            {
                var issue = result.Data;
                // 若后端返回的坐标存在，则以命中点为准（保持一致，避免浮点显示差异）
                if (issue.position == null)
                {
                    issue.position = new PositionData(pendingSnapshot.HitPoint);
                }
                pendingSnapshot.Marker.Bind(issue, true);
                pendingSnapshot.Marker.ApplyIssueStyle();
                _savedMarkers.Add(pendingSnapshot.Marker);
                Pending = null;
            }

            SubmitFinished?.Invoke(result, pendingSnapshot?.Surface?.displayName);
        }

        /// <summary>从后端加载历史问题并在固定场景中恢复标记（加分项）。</summary>
        public void LoadHistory()
        {
            if (_api == null)
            {
                return;
            }
            var host = ApiBehaviour != null ? ApiBehaviour : this;
            host.StartCoroutine(LoadHistoryRoutine());
        }

        private IEnumerator LoadHistoryRoutine()
        {
            var done = false;
            ApiResult<List<IssueData>> result = null;
            yield return _api.FetchIssues(r => { result = r; done = true; });

            if (!done || result == null || !result.Success)
            {
                Debug.LogWarning("[Placement] 加载历史问题失败：" + (result != null ? result.ErrorMessage : "未知错误"));
                HistoryLoaded?.Invoke(-1);
                yield break;
            }

            var restored = RestoreMarkers(result.Data, out var skipped, out var removed);
            HistoryLoaded?.Invoke(restored);
            if (skipped > 0)
            {
                Debug.LogWarning($"[Placement] {skipped} 条问题的坐标不在当前场景范围内，已跳过标记恢复（数据仍保留在后端）");
            }
            if (removed > 0)
            {
                Debug.Log($"[Placement] 后端已删除 {removed} 个问题，对应标记已从场景移除");
                MarkersRemoved?.Invoke(removed);
            }
        }

        /// <summary>兼容旧签名的重载（不关心被删除的数量）。</summary>
        public int RestoreMarkers(IEnumerable<IssueData> issues, out int skipped)
        {
            return RestoreMarkers(issues, out skipped, out _);
        }

        /// <summary>
        /// 按后端返回的问题列表「同步」场景中的标记：新增、就地刷新、并移除已删除的问题。
        /// 注意：只有坐标在场景范围内才恢复，否则会出现“标记飘到房间外面”的假象。
        /// 真实 AR 中问题更大——见 README 第 5 节。
        /// </summary>
        /// <param name="issues">后端返回的完整问题列表。</param>
        /// <param name="skipped">坐标越界等原因被跳过的条数。</param>
        /// <param name="removed">本次因后端已删除而被移除的标记数。</param>
        public int RestoreMarkers(IEnumerable<IssueData> issues, out int skipped, out int removed)
        {
            skipped = 0;
            removed = 0;
            var count = 0;
            if (issues == null)
            {
                return 0;
            }

            var existing = new Dictionary<string, IssueMarker>(StringComparer.Ordinal);
            foreach (var m in _savedMarkers)
            {
                if (m != null && m.Issue != null && !string.IsNullOrEmpty(m.Issue.id))
                {
                    existing[m.Issue.id] = m;
                }
            }

            // 记录后端这一次仍然存在的问题 id，用来判定哪些标记该被删除
            var aliveIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var issue in issues)
            {
                if (issue == null || issue.position == null || string.IsNullOrEmpty(issue.id))
                {
                    skipped++;
                    continue;
                }

                aliveIds.Add(issue.id);

                // 已存在则「就地刷新」而不是跳过：
                // 管理端改了状态/标题后按 F5，标记必须重新着色并显示新状态，
                // 否则用户会以为“改了没生效”。以前这里直接 continue，
                // 导致 count 恒为 0，HUD 误报“后端暂无历史问题”。
                if (existing.TryGetValue(issue.id, out var live) && live != null)
                {
                    live.Bind(issue, true);
                    live.ApplyIssueStyle();
                    count++;
                    continue;
                }

                var pos = issue.position.ToVector3();
                if (!IsInsideScene(pos))
                {
                    skipped++;
                    continue;
                }

                var root = new GameObject("Marker_" + issue.id);
                root.transform.SetParent(_markerRoot != null ? _markerRoot : transform, false);
                root.transform.position = pos;

                var marker = root.AddComponent<IssueMarker>();
                marker.Build(false);
                marker.Bind(issue, true);
                marker.ApplyIssueStyle();
                _savedMarkers.Add(marker);
                count++;
            }

            // 后端已经不存在的问题，对应标记必须消失。
            // 以前只做「新增 + 刷新」，管理端删除记录后按 F5，球会一直留在场景里。
            for (var i = _savedMarkers.Count - 1; i >= 0; i--)
            {
                var m = _savedMarkers[i];
                if (m == null || m.Issue == null)
                {
                    _savedMarkers.RemoveAt(i);
                    continue;
                }
                if (aliveIds.Contains(m.Issue.id))
                {
                    continue;
                }

                if (ReferenceEquals(m, Pending?.Marker))
                {
                    continue; // 正在编辑的待提交标记不参与同步
                }

                _savedMarkers.RemoveAt(i);
                if (m.gameObject != null)
                {
                    ObjectUtil.SafeDestroy(m.gameObject);
                }
                removed++;
            }

            return count;
        }

        /// <summary>场景边界（略大于房间，容忍墙面上的标记）。</summary>
        public static bool IsInsideScene(Vector3 p)
        {
            const float limitXZ = 30f;
            const float limitY = 12f;
            return Mathf.Abs(p.x) <= limitXZ && Mathf.Abs(p.z) <= limitXZ && p.y >= -2f && p.y <= limitY;
        }

        /// <summary>点击/拾取场景中的已保存标记（加分项：点标记看详情）。返回命中的标记或 null。</summary>
        public IssueMarker PickMarker(Vector2 screenPos)
        {
            if (ViewCamera == null)
            {
                return null;
            }
            var ray = ViewCamera.ScreenPointToRay(screenPos);
            var hits = Physics.RaycastAll(ray, RayDistance, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0)
            {
                return null;
            }
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var marker = h.collider != null ? h.collider.GetComponentInParent<IssueMarker>() : null;
                if (marker != null)
                {
                    return marker;
                }
            }
            return null;
        }

        private void RejectClick(string reason)
        {
            ClickRejected?.Invoke(reason);
        }
    }
}

