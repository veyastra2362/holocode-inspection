using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Inspection
{
    /// <summary>
    /// 运行时用 uGUI 代码构建整个界面（不依赖手摆 Prefab，打开工程即可用）。
    ///
    /// 布局：
    ///   左下  操作说明 + 后端状态（含“重试连接”）
    ///   下方  准星 + 当前命中目标提示
    ///   右侧  提交表单（标题* / 描述 / 优先级 / 坐标 / 提交 / 取消）
    ///   左上  已标注问题列表（点击可查看详情）
    ///   顶部  全局提示条（成功/失败都用明确文案，颜色区分）
    ///
    /// 与题目第 7 条直接相关：所有按钮/输入框都是 uGUI，指针在 UI 上时
    /// InspectionInput 不会产生点击，因此不会误放标记。
    /// </summary>
    public class InspectionHUD : MonoBehaviour
    {
        [Header("依赖")]
        public IssuePlacementController Placement;
        public InspectionInput InputDriver;
        public Camera ViewCamera;
        public string BackendUrl = IssueApi.DefaultBaseUrl;

        [Header("外观")]
        public string Title = "虚拟现场巡检 · 问题标注";

        private Canvas _canvas;
        private Text _toastText;
        private Image _toastBg;
        private Coroutine _toastRoutine;

        private GameObject _formPanel;
        private InputField _titleField;
        private InputField _descField;
        private Text _coordinateText;
        private Text _formTargetText;
        private Button _submitButton;
        private Text _submitLabel;
        private Text _priorityLabel;
        private IssuePriority _priority = IssuePriority.Medium;

        private Text _hintText;
        private Text _backendText;
        private Text _historyText;
        private GameObject _historyPanel;
        private Text _detailText;

        private GameObject _crosshair;
        private bool _uiPausedInput;

        private readonly List<string> _historyLines = new List<string>();

        private static readonly Color Bg = new Color(0.08f, 0.09f, 0.10f, 0.86f);
        private static readonly Color PanelBg = new Color(0.10f, 0.11f, 0.13f, 0.92f);
        private static readonly Color Accent = new Color(0.24f, 0.55f, 0.95f, 1f);
        private static readonly Color OkColor = new Color(0.20f, 0.70f, 0.36f, 0.96f);
        private static readonly Color ErrColor = new Color(0.82f, 0.24f, 0.22f, 0.96f);
        private static readonly Color WarnColor = new Color(0.92f, 0.62f, 0.10f, 0.96f);

        public void Configure(IssuePlacementController placement, InspectionInput input, Camera cam, string backendUrl)
        {
            Placement = placement;
            InputDriver = input;
            ViewCamera = cam;
            BackendUrl = IssueApi.NormalizeBaseUrl(backendUrl);
        }

        private void Start()
        {
            EnsureEventSystem();
            BuildUI();
            HookPlacement();

            ShowToast("已进入虚拟现场：左键点击设备/墙面/地面放置标记，右键拖拽转视角，WASD 移动", WarnColor, 6f);
            RefreshHint(string.Empty);
        }

        private void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(transform, false);
        }

        private void HookPlacement()
        {
            if (Placement == null)
            {
                return;
            }
            Placement.MarkerPlaced += OnMarkerPlaced;
            Placement.PendingCleared += OnPendingCleared;
            Placement.SubmitStarted += OnSubmitStarted;
            Placement.SubmitFinished += OnSubmitFinished;
            Placement.ClickRejected += OnClickRejected;
            Placement.HistoryLoaded += OnHistoryLoaded;
            Placement.MarkerPicked += ShowMarkerDetail;
            Placement.HoverTargetChanged += (hit, surface) => RefreshHint(DescribeHover(hit, surface));
        }

        private void OnDestroy()
        {
            if (Placement == null)
            {
                return;
            }
            Placement.MarkerPlaced -= OnMarkerPlaced;
            Placement.PendingCleared -= OnPendingCleared;
            Placement.SubmitStarted -= OnSubmitStarted;
            Placement.SubmitFinished -= OnSubmitFinished;
            Placement.ClickRejected -= OnClickRejected;
            Placement.HistoryLoaded -= OnHistoryLoaded;
            Placement.MarkerPicked -= ShowMarkerDetail;
        }

        private void Update()
        {
            UpdatePointerOverUI();

            // 输入框获得焦点时，屏蔽 WASD 移动，避免打字带着巡检员跑
            var typing = _titleField != null && (_titleField.isFocused || (_descField != null && _descField.isFocused));
            GuiFocusGuard.IsTyping = typing;

            // ESC 取消当前待提交标记；Enter 在表单有焦点时提交
            if (Input.GetKeyDown(KeyCode.Escape) && Placement != null && Placement.Pending != null && !Placement.IsSubmitting)
            {
                Placement.CancelPending();
            }
            if (Input.GetKeyDown(KeyCode.F5))
            {
                Placement?.LoadHistory();
                ShowToast("正在从后端刷新问题列表…", WarnColor, 2f);
            }
        }

        private void UpdatePointerOverUI()
        {
            if (InputDriver == null)
            {
                return;
            }
            var es = EventSystem.current;
            InputDriver.PointerOverUI = es != null && es.IsPointerOverGameObject();
        }

        // ================= 事件处理 =================

        private void OnMarkerPlaced(PendingMarker pending)
        {
            OpenForm(pending);
            ShowToast("已放置标记，请填写问题后提交", WarnColor, 3f);
        }

        private void OnPendingCleared()
        {
            CloseForm();
            ClearHoverDetail();
            ShowToast("已取消，未保存的标记已被移除", WarnColor, 3f);
        }

        private void OnSubmitStarted()
        {
            if (_submitButton != null)
            {
                _submitButton.interactable = false;
            }
            if (_submitLabel != null)
            {
                _submitLabel.text = "提交中…";
            }
        }

        private void OnSubmitFinished(ApiResult<IssueData> result, string surfaceName)
        {
            if (_submitButton != null)
            {
                _submitButton.interactable = true;
            }
            if (_submitLabel != null)
            {
                _submitLabel.text = "提交";
            }

            if (result != null && result.Success && result.Data != null)
            {
                var d = result.Data;
                ShowToast($"✔ 上报成功：{d.title}（{PriorityCn(d.priority)}，状态 {StatusCn(d.status)}，ID {ShortId(d.id)}）", OkColor, 5f);
                AddHistoryLine(d);
                CloseForm();
                ClearHoverDetail();
            }
            else
            {
                var msg = result != null ? result.ErrorMessage : "未知错误";
                // 失败时保留表单与标记，用户可以修正后重试
                ShowToast($"✖ 上报失败：{msg}\n（标记与内容已保留，可修改后重试，或点“取消”移除标记）", ErrColor, 7f);
                if (result != null && result.HttpStatus == 400)
                {
                    ShowToast($"✖ 上报失败：{msg}\n请检查标题是否为空、优先级是否正确", ErrColor, 7f);
                }
            }
        }

        private void OnClickRejected(string reason)
        {
            ShowToast("· " + reason, WarnColor, 2.5f);
        }

        private void OnHistoryLoaded(int count)
        {
            if (count < 0)
            {
                ShowToast($"历史标记加载失败：{(_backendText != null ? "请检查后端" : "请检查后端")}", ErrColor, 5f);
                return;
            }
            if (count == 0)
            {
                ShowToast("后端暂无历史问题", WarnColor, 3f);
                return;
            }
            ShowToast($"已从后端恢复 {count} 个历史标记", OkColor, 4f);
        }

        // ================= 表单 =================

        private void OpenForm(PendingMarker pending)
        {
            if (_formPanel == null)
            {
                return;
            }
            _formPanel.SetActive(true);
            _priority = IssuePriority.Medium;
            RefreshPriorityButtons();

            _titleField.text = string.Empty;
            _descField.text = string.Empty;
            if (_coordinateText != null)
            {
                _coordinateText.text = new PositionData(pending.HitPoint).ToString();
            }
            if (_formTargetText != null)
            {
                var s = pending.Surface;
                _formTargetText.text = s != null
                    ? $"命中：{s.displayName}（{s.deviceId} / {s.zone}）"
                    : "命中：场景表面";
            }
            _titleField.ActivateInputField();
        }

        private void CloseForm()
        {
            if (_formPanel != null)
            {
                _formPanel.SetActive(false);
            }
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
            GuiFocusGuard.IsTyping = false;
        }

        private void OnSubmitClicked()
        {
            if (Placement == null)
            {
                return;
            }
            var title = _titleField != null ? _titleField.text : string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                ShowToast("请填写标题（必填）", ErrColor, 3f);
                _titleField?.ActivateInputField();
                return;
            }
            var desc = _descField != null ? _descField.text : string.Empty;
            Placement.Submit(title, desc, _priority);
        }

        private void OnCancelClicked()
        {
            Placement?.CancelPending();
        }

        private void RefreshPriorityButtons()
        {
            foreach (var (btn, pr) in _priorityButtons)
            {
                var img = btn.targetGraphic as Image;
                if (img != null)
                {
                    img.color = pr == _priority ? Accent : new Color(0.22f, 0.24f, 0.27f, 1f);
                }
            }
            if (_priorityLabel != null)
            {
                _priorityLabel.text = "优先级：" + PriorityCn(IssueEnums.ToWire(_priority));
            }
        }

        private readonly List<(Button, IssuePriority)> _priorityButtons = new List<(Button, IssuePriority)>();

        private void SetPriority(IssuePriority p)
        {
            _priority = p;
            RefreshPriorityButtons();
        }

        // ================= 提示 =================

        private void RefreshHint(string hoverText)
        {
            if (_hintText == null)
            {
                return;
            }
            if (Placement != null && Placement.Pending != null)
            {
                _hintText.text = "待提交标记已放置 —— 请在右侧表单填写并提交，或按 ESC 取消";
                return;
            }
            _hintText.text = string.IsNullOrEmpty(hoverText)
                ? "左键点击设备或表面放置问题标记"
                : "左键点击放置标记 · " + hoverText;
        }

        private string DescribeHover(RaycastHit hit, SurfaceInfo surface)
        {
            if (surface != null)
            {
                return $"{surface.zone} / {surface.displayName}（{surface.deviceId}）";
            }
            return hit.collider != null ? $"命中 {hit.collider.gameObject.name}" : string.Empty;
        }

        private void ClearHoverDetail()
        {
            if (_detailText != null)
            {
                _detailText.text = string.Empty;
                _detailText.gameObject.SetActive(false);
            }
        }

        private void ShowToast(string message, Color bg, float seconds)
        {
            if (_toastText == null)
            {
                Debug.Log("[HUD] " + message.Replace("\n", " | "));
                return;
            }
            if (_toastRoutine != null)
            {
                StopCoroutine(_toastRoutine);
            }
            _toastRoutine = StartCoroutine(ToastRoutine(message, bg, seconds));
        }

        private IEnumerator ToastRoutine(string message, Color bg, float seconds)
        {
            _toastText.text = message;
            _toastBg.color = bg;
            _toastText.transform.parent.gameObject.SetActive(true);
            yield return new WaitForSecondsRealtime(seconds);
            // 提交成功/失败的提示需要用户看清，不自动消失过快的处理：由时长控制
            _toastText.transform.parent.gameObject.SetActive(false);
            _toastRoutine = null;
        }

        private void AddHistoryLine(IssueData issue)
        {
            if (_historyText == null || issue == null)
            {
                return;
            }
            var time = IsoTime.ToLocalDisplay(issue.createdAt);
            _historyLines.Insert(0, $"{ShortId(issue.id)}  [{PriorityCn(issue.priority)}/{StatusCn(issue.status)}]  {issue.title}  ·{time}");
            if (_historyLines.Count > 7)
            {
                _historyLines.RemoveAt(_historyLines.Count - 1);
            }
            var sb = new StringBuilder();
            foreach (var l in _historyLines)
            {
                sb.AppendLine(l);
            }
            _historyText.text = sb.ToString();
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "—";
            }
            return id.Length <= 10 ? id : id.Substring(id.Length - 8);
        }

        private static string PriorityCn(string wire)
        {
            switch (wire)
            {
                case "low": return "低";
                case "medium": return "中";
                case "high": return "高";
                default: return wire ?? "—";
            }
        }

        private static string StatusCn(string wire)
        {
            switch (wire)
            {
                case "open": return "待处理";
                case "in_progress": return "处理中";
                case "resolved": return "已解决";
                default: return wire ?? "—";
            }
        }

        // ================= 构建界面 =================

        private void BuildUI()
        {
            var canvasGo = new GameObject("HUDCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = 0.5f;

            BuildTopBar();
            BuildHintBar();
            BuildCrosshair();
            BuildHelpPanel();
            BuildHistoryPanel();
            BuildFormPanel();
        }

        private Font DefaultFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return f;
        }

        private RectTransform NewPanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 anchoredPos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return rt;
        }

        private Text NewText(string name, Transform parent, string content, int size, Color color,
            TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = DefaultFont();
            t.fontSize = size;
            t.color = color;
            t.text = content;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 10);
            rt.offsetMax = new Vector2(-12, -10);
            return t;
        }

        private Button NewButton(string name, Transform parent, string label, Color color, int fontSize, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            btn.onClick.AddListener(onClick);
            var text = NewText("Label", go.transform, label, fontSize, Color.white, TextAnchor.MiddleCenter);
            text.raycastTarget = false;
            return btn;
        }

        private void BuildTopBar()
        {
            var rt = NewPanel("TopBar", _canvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0, -12), new Vector2(760, 56), new Color(0, 0, 0, 0));
            _toastBg = rt.gameObject.GetComponent<Image>();
            _toastBg.color = WarnColor;
            _toastText = NewText("Toast", rt, "", 16, Color.white, TextAnchor.MiddleCenter);
            rt.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -70);
            rt.gameObject.SetActive(false);

            var titleBar = NewPanel("TitleBar", _canvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0, -12), new Vector2(720, 44), Bg);
            var t = NewText("Title", titleBar, Title, 18, Color.white, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
        }

        private void BuildCrosshair()
        {
            _crosshair = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
            _crosshair.transform.SetParent(_canvas.transform, false);
            var rt = _crosshair.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(6, 6);
            var img = _crosshair.GetComponent<Image>();
            img.color = new Color(1, 1, 1, 0.6f);
            img.raycastTarget = false; // 准星绝不能挡住点击
        }

        private void BuildHintBar()
        {
            var rt = NewPanel("HintBar", _canvas.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0, 120), new Vector2(760, 42), Bg);
            _hintText = NewText("Hint", rt, "", 16, new Color(0.92f, 0.94f, 0.98f), TextAnchor.MiddleCenter);
            _hintText.raycastTarget = false;
        }

        private void BuildHelpPanel()
        {
            var rt = NewPanel("HelpPanel", _canvas.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(16, 16), new Vector2(430, 178), PanelBg);

            var body = NewText("Body", rt,
                "【操作】\n" +
                "WASD / 方向键：移动      Shift：加速\n" +
                "右键拖拽：转视角          滚轮：前后\n" +
                "左键点击：在该表面放置标记\n" +
                "F5：从后端刷新历史标记   ESC：取消待提交标记",
                14, new Color(0.85f, 0.88f, 0.92f));
            body.raycastTarget = false;

            _backendText = NewText("Backend", rt, "", 13, new Color(0.75f, 0.80f, 0.86f), TextAnchor.LowerLeft);
            _backendText.raycastTarget = false;
            _backendText.rectTransform.offsetMin = new Vector2(12, 10);
            _backendText.rectTransform.offsetMax = new Vector2(-12, -140);
        }

        private void BuildHistoryPanel()
        {
            var rt = NewPanel("HistoryPanel", _canvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(16, -70), new Vector2(380, 190), PanelBg);
            _historyPanel = rt.gameObject;
            var head = NewText("Head", rt, "本次已上报（点击标记可在场景中查看）", 13,
                new Color(0.70f, 0.76f, 0.84f), TextAnchor.UpperLeft);
            head.raycastTarget = false;
            _historyText = NewText("List", rt, "（暂无）", 13, new Color(0.88f, 0.91f, 0.95f));
            _historyText.raycastTarget = false;
            _historyText.rectTransform.offsetMax = new Vector2(-12, -34);
            _historyText.supportRichText = false;
        }

        private void BuildFormPanel()
        {
            var rt = NewPanel("FormPanel", _canvas.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-16, 0), new Vector2(420, 430), PanelBg);
            _formPanel = rt.gameObject;

            var title = NewText("FormTitle", rt, "上报问题", 18, Color.white);
            title.rectTransform.offsetMax = new Vector2(-12, -12);
            title.rectTransform.offsetMin = new Vector2(12, 390);

            _formTargetText = NewText("Target", rt, "", 13, new Color(0.70f, 0.82f, 0.95f));
            _formTargetText.rectTransform.offsetMax = new Vector2(-12, -54);
            _formTargetText.rectTransform.offsetMin = new Vector2(12, 352);

            // 标题输入
            var titleLabel = NewText("TitleLabel", rt, "标题（必填）", 14, new Color(0.80f, 0.84f, 0.90f));
            titleLabel.rectTransform.offsetMax = new Vector2(-12, -84);
            titleLabel.rectTransform.offsetMin = new Vector2(12, 330);

            _titleField = NewInputField("TitleField", rt, 120, "例如：3 号机柜温度异常", false);
            SetRect(_titleField.GetComponent<RectTransform>(), new Vector2(12, 296), new Vector2(-12, 330));

            var descLabel = NewText("DescLabel", rt, "描述（可选）", 14, new Color(0.80f, 0.84f, 0.90f));
            descLabel.rectTransform.offsetMax = new Vector2(-12, -176);
            descLabel.rectTransform.offsetMin = new Vector2(12, 238);

            _descField = NewInputField("DescField", rt, 200, "补充现场情况（最多 2000 字）", true);
            SetRect(_descField.GetComponent<RectTransform>(), new Vector2(12, 100), new Vector2(-12, 234));

            _priorityLabel = NewText("PriorityLabel", rt, "优先级：中", 14, new Color(0.80f, 0.84f, 0.90f));
            _priorityLabel.rectTransform.offsetMax = new Vector2(-12, -290);
            _priorityLabel.rectTransform.offsetMin = new Vector2(12, 68);

            var prRow = new GameObject("PriorityRow", typeof(RectTransform));
            prRow.transform.SetParent(rt, false);
            SetRect(prRow.GetComponent<RectTransform>(), new Vector2(12, 40), new Vector2(-12, 68));
            var hl = prRow.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 8;
            hl.childForceExpandWidth = true;
            hl.childForceExpandHeight = true;
            hl.childControlWidth = true;
            hl.childControlHeight = true;

            _priorityButtons.Clear();
            foreach (var (label, pr) in new[]
                     {
                         ("低", IssuePriority.Low), ("中", IssuePriority.Medium), ("高", IssuePriority.High)
                     })
            {
                var b = NewButton("Priority_" + pr, prRow.transform, label, new Color(0.22f, 0.24f, 0.27f), 14,
                    () => SetPriority(pr));
                _priorityButtons.Add((b, pr));
            }

            _coordinateText = NewText("Coord", rt, "", 13, new Color(0.70f, 0.76f, 0.84f));
            _coordinateText.rectTransform.offsetMax = new Vector2(-12, -318);
            _coordinateText.rectTransform.offsetMin = new Vector2(12, 40);

            var actions = new GameObject("Actions", typeof(RectTransform));
            actions.transform.SetParent(rt, false);
            SetRect(actions.GetComponent<RectTransform>(), new Vector2(12, 8), new Vector2(-12, 36));
            var ahl = actions.AddComponent<HorizontalLayoutGroup>();
            ahl.spacing = 8;
            ahl.childForceExpandWidth = true;
            ahl.childForceExpandHeight = true;
            ahl.childControlWidth = true;
            ahl.childControlHeight = true;

            _submitButton = NewButton("Submit", actions.transform, "提交", OkColor, 15, OnSubmitClicked);
            _submitLabel = _submitButton.GetComponentInChildren<Text>();
            NewButton("Cancel", actions.transform, "取消 (ESC)", new Color(0.35f, 0.37f, 0.40f), 15, OnCancelClicked);

            _formPanel.SetActive(false);
        }

        private InputField NewInputField(string name, Transform parent, int charLimit, string placeholder, bool multiline)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.16f, 0.18f, 0.21f, 1f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = DefaultFont();
            text.fontSize = 14;
            text.color = Color.white;
            text.supportRichText = false;
            text.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8, 6);
            textRt.offsetMax = new Vector2(-8, -6);

            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.GetComponent<Text>();
            ph.font = DefaultFont();
            ph.fontSize = 14;
            ph.color = new Color(0.55f, 0.58f, 0.62f);
            ph.text = placeholder;
            ph.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            var phRt = phGo.GetComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(8, 6);
            phRt.offsetMax = new Vector2(-8, -6);

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = ph;
            field.characterLimit = charLimit;
            field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            return field;
        }

        private static void SetRect(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        /// <summary>供测试/自动化读取当前提示文字。</summary>
        public string CurrentToast => _toastText != null ? _toastText.text : string.Empty;
        /// <summary>供测试/自动化读取表单是否打开。</summary>
        public bool IsFormOpen => _formPanel != null && _formPanel.activeSelf;

        /// <summary>平台判定：是否支持真实 AR（仅用于提示，不影响功能）。</summary>
        public static bool IsArCapablePlatform()
        {
            return Application.platform == RuntimePlatform.Android ||
                   Application.platform == RuntimePlatform.IPhonePlayer;
        }

        /// <summary>演示/自动化用：填充表单内容（不触发提交）。</summary>
        public void AutoFillForm(string title, string description, IssuePriority priority)
        {
            if (_formPanel == null)
            {
                return;
            }
            if (_titleField != null)
            {
                _titleField.text = title;
            }
            if (_descField != null)
            {
                _descField.text = description;
            }
            SetPriority(priority);
        }

        /// <summary>演示/自动化用：等价于点“提交”按钮。</summary>
        public void AutoSubmit()
        {
            OnSubmitClicked();
        }

        /// <summary>由 SceneBootstrap 在启动时置位。</summary>
        public void SetStartupError(string message)
        {
            ShowBackendStatus(false, message);
            ShowToast(message, ErrColor, 8f);
        }

        /// <summary>后端连通状态（左下角常驻显示，避免“看起来正常但点提交才报错”）。</summary>
        public void ShowBackendStatus(bool ok, string detail)
        {
            _backendOk = ok;
            if (_backendText != null)
            {
                _backendText.color = ok ? new Color(0.60f, 0.92f, 0.68f) : new Color(1f, 0.62f, 0.55f);
                _backendText.text = (ok ? "● 后端 " : "✖ 后端 ") + detail;
            }
            if (ok)
            {
                ShowToast("已连接后端：" + detail, OkColor, 3.5f);
            }
            else
            {
                ShowToast("后端不可用：" + detail + "\n可在 Console 里检查地址，或修改 SceneBootstrap.BackendUrl", ErrColor, 8f);
            }
        }

        private bool _backendOk;

        /// <summary>启动时恢复历史标记的结果反馈。</summary>
        public void ShowHistoryRestored(int restored, int skipped)
        {
            if (restored <= 0 && skipped <= 0)
            {
                return;
            }
            var msg = $"已恢复 {restored} 个历史标记";
            if (skipped > 0)
            {
                msg += $"（{skipped} 条坐标不在当前场景范围，已跳过）";
            }
            if (_historyText != null && restored > 0)
            {
                _historyText.text = string.IsNullOrEmpty(_historyText.text)
                    ? msg
                    : msg + "\n" + _historyText.text;
            }
            ShowToast(msg, OkColor, 5f);
        }

        /// <summary>点击场景中的标记 → 显示问题详情（加分项）。</summary>
        public void ShowMarkerDetail(IssueMarker marker)
        {
            if (marker == null || marker.Issue == null)
            {
                return;
            }
            var i = marker.Issue;
            var sb = new StringBuilder();
            sb.AppendLine("【标记详情】");
            sb.AppendLine(i.title);
            if (!string.IsNullOrEmpty(i.description))
            {
                sb.AppendLine(i.description);
            }
            sb.AppendLine($"优先级：{PriorityCn(i.priority)}    状态：{StatusCn(i.status)}");
            sb.AppendLine($"坐标：{new PositionData(marker.HitPoint)}");
            sb.AppendLine($"ID：{i.id}");

            EnsureDetailPanel();
            _detailText.gameObject.SetActive(true);
            _detailText.text = sb.ToString();
        }

        private void EnsureDetailPanel()
        {
            if (_detailText != null)
            {
                return;
            }
            var rt = NewPanel("DetailPanel", _canvas.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0, 200), new Vector2(520, 180), PanelBg);
            _detailText = NewText("Detail", rt, "", 14, new Color(0.90f, 0.93f, 0.97f));
            _detailText.raycastTarget = false;
        }
    }
}
