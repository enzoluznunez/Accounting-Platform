using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ToolPanelUI : PanelUI
{

    public ManageTools toolManager;

    private readonly List<ToolButtonVisual> _toolButtons = new List<ToolButtonVisual>();
    private readonly Dictionary<ToolType, GameObject> _toolContents = new Dictionary<ToolType, GameObject>();
    private RectTransform _primaryRect;
    private RectTransform _panelRootRect;

    private OptionsCard _toolCard;
    private OptionsCard _assistantCard;
    private readonly List<OptionsCard> _stack = new List<OptionsCard>();
    private int _suspendedSlot = -1;

    private Coroutine _resizeRoutine;
    private GridLayoutGroup _toolGrid;
    private UIButton.Handle _assistantButton;
    private Watch _assistant;

    public static readonly string[] AssistantSpeedLabels = { "0.5 m/s", "1 m/s", "Instant" };
    private static readonly float[] AssistantSpeedValues = { 0.5f, 1f, 0f };

    private GameObject _assistantContent;
    private ButtonList _assistantSpeedRow;
    private int _assistantSpeedIndex;

    public static readonly ToolType[] Tools =
    {
        ToolType.Detail,
        ToolType.Slice,
        ToolType.Move,
        ToolType.Rotate,
        ToolType.Scale,
        ToolType.Color,
        ToolType.Sort,
        ToolType.Profile
    };

    public static string Label(ToolType tool) => tool.ToString();

    public static string Description(ToolType tool)
    {
        switch (tool)
        {
            case ToolType.Detail:
                return "The Detail tool lets you pull a single cube out of the sheet to see it enlarged.";
            case ToolType.Slice:
                return "The Slice tool lets you break a sheet apart by touching the gap between two columns or two rows.";
            case ToolType.Color:
                return "The Color tool lets you change a sheet's color by selecting a color and touching the bars.";
            case ToolType.Move:
                return "The Move tool lets you move a sheet by grabbing it with one hand.";
            case ToolType.Rotate:
                return "The Rotate tool lets you rotate a sheet by grabbing it with both hands and twisting.";
            case ToolType.Scale:
                return "The Scale tool lets you resize a sheet by grabbing it with both hands and moving them apart or together.";
            case ToolType.Sort:
                return "The Sort tool lets you pinch a row or column and slide it to reorder the sheet. Grab beside its label, or anywhere along it.";
            case ToolType.Profile:
                return "The Profile tool lets you press a cube and sweep along a row or column to lift it out and read its statistics.";
            default:
                return string.Empty;
        }
    }

    private class ToolButtonVisual
    {
        public ToolType Tool;
        public UIButton.Handle Handle;
    }

    private class SwatchGrid
    {
        public ButtonList List;
        public LayoutElement Host;
        public int Columns;
        public float PadX;
        public float ResolvedWidth;
    }

    private class OptionsCard
    {
        public GameObject Root;
        public RectTransform Rect;
        public Transform Content;
    }

    private readonly Dictionary<ToolType, SwatchGrid> _swatchGrids = new Dictionary<ToolType, SwatchGrid>();

    private void Awake()
    {
        InitPanel(Style.Panel);
        if (toolManager == null) toolManager = GetComponent<ManageTools>();

        GameObject optionsPanel = FindTransform.FindDeep(transform, "OptionsPanel")?.gameObject;
        _primaryRect = FindTransform.FindDeep(transform, "PrimaryPanel") as RectTransform;
        _panelRootRect = FindTransform.FindDeep(transform, "PanelRoot") as RectTransform;

        _toolCard = AdoptCard(optionsPanel);
        _assistantCard = CloneCard(_toolCard, "AssistantPanel");

        BindToolButtons();
        BindToolContents();
        BindTitleBar();

        SetGrabRects(_primaryRect,
            _toolCard != null ? _toolCard.Rect : null,
            _assistantCard != null ? _assistantCard.Rect : null);

        if (toolManager != null)
            toolManager.OnToolChanged += OnToolChanged;

        OnToolChanged(toolManager != null ? toolManager.SelectedTool : ToolType.None);

        if (_canvas != null)
            _canvas.gameObject.SetActive(false);
    }

    private static OptionsCard AdoptCard(GameObject root)
    {
        if (root == null) return null;

        RectTransform rect = root.transform as RectTransform;
        if (rect == null) return null;

        rect.sizeDelta = new Vector2(rect.sizeDelta.x, Style.Subpanel.y);
        root.SetActive(false);

        return new OptionsCard
        {
            Root = root,
            Rect = rect,
            Content = FindTransform.FindDeep(root.transform, "ContentArea")
        };
    }

    private OptionsCard CloneCard(OptionsCard source, string name)
    {
        if (source == null || source.Root == null) return null;

        GameObject root = Instantiate(source.Root, source.Root.transform.parent, false);
        root.name = name;

        OptionsCard card = AdoptCard(root);
        if (card != null && card.Content != null) UILayout.Clear(card.Content);
        return card;
    }

    private Watch Assistant
    {
        get
        {
            if (_assistant == null)
            {
                _assistant = Scene.Assistant;
                if (_assistant != null)
                {
                    _assistant.AssistantActiveChanged += OnAssistantActiveChanged;
                    OnAssistantActiveChanged(_assistant.IsGeminiActive, _assistant.Status);
                }
            }
            return _assistant;
        }
    }

    protected override void ResolveDeferredLayout() => RefreshOptionCards();

    private void OnEnable()
    {
        if (_assistant != null) OnAssistantActiveChanged(_assistant.IsGeminiActive, _assistant.Status);
    }

    private void OnDisable()
    {
        _connectingPulse = null;
        _resizeRoutine = null;
        _fitTiles = null;
    }

    private void Start()
    {
        if (ManageDatasets.Instance != null)
        {
            ManageDatasets.Instance.OnActiveDatasetChanged += OnActiveDatasetChanged;
        }

        _ = Assistant;
    }

    private void OnDestroy()
    {
        if (toolManager != null)
            toolManager.OnToolChanged -= OnToolChanged;
        if (ManageDatasets.Instance != null)
        {
            ManageDatasets.Instance.OnActiveDatasetChanged -= OnActiveDatasetChanged;
        }
        if (_assistant != null)
            _assistant.AssistantActiveChanged -= OnAssistantActiveChanged;
    }

    private void OnActiveDatasetChanged(int index)
    {
        _suspendedSlot = -1;
        if (toolManager != null) toolManager.ForgetSuspendedTool();
    }

    private void BindToolButtons()
    {
        Transform toolGrid = FindTransform.FindDeep(transform, "ToolGrid");
        if (toolGrid == null) return;

        for (int i = 0; i < Tools.Length; i++)
        {
            ToolType tool = Tools[i];
            Transform btnT = toolGrid.Find($"Tool_{Label(tool)}");
            if (btnT == null)
            {
                Debug.LogError($"[ToolPanelUI] No button named 'Tool_{Label(tool)}' under ToolGrid; " +
                               $"the {Label(tool)} tool will not be selectable.");
                continue;
            }

            UIButton.Handle h = UIButton.Adopt(btnT.gameObject);
            ToolButtonVisual visual = new ToolButtonVisual { Tool = tool, Handle = h };
            _toolButtons.Add(visual);

            ToolType captured = tool;
            Button btn = btnT.GetComponent<Button>();
            if (btn != null)
                btn.onClick.AddListener(() => OnToolButtonClicked(captured));
        }

        BindAssistantButton(toolGrid);
        SquareTiles(toolGrid.GetComponent<GridLayoutGroup>());
    }

    private void SquareTiles(GridLayoutGroup grid)
    {
        if (grid == null) return;
        _toolGrid = grid;

        grid.padding = new RectOffset(
            (int)Style.PanelInset, (int)Style.PanelInset,
            (int)Style.SmallPadding, (int)Style.SmallPadding);
        grid.spacing = new Vector2(Style.SmallPadding, Style.SmallPadding);

        LayoutElement le = grid.GetComponent<LayoutElement>();
        if (le == null) le = grid.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 0f;
        le.preferredHeight = 0f;
        le.flexibleHeight = 1f;
    }

    private Coroutine _fitTiles;

    private void QueueFitTiles()
    {
        FitTiles();

        if (!isActiveAndEnabled) return;
        if (_fitTiles != null) StopCoroutine(_fitTiles);
        _fitTiles = StartCoroutine(FitTilesUntilStable());
    }

    private IEnumerator FitTilesUntilStable()
    {
        yield return UILayout.Converge(
            () => IsVisible,
            FitTiles,
            () => _toolGrid != null ? _toolGrid.cellSize.x : -1f);

        _fitTiles = null;
    }

    private void FitTiles()
    {
        if (_toolGrid == null) return;

        RectTransform gridRect = _toolGrid.transform as RectTransform;
        if (gridRect == null || _panelRootRect == null) return;

        LayoutRebuilder.ForceRebuildLayoutImmediate(_panelRootRect);

        int columns = Mathf.Max(1, _toolGrid.constraintCount);
        int rows = Mathf.Max(1, Mathf.CeilToInt(_toolGrid.transform.childCount / (float)columns));

        float byWidth = (gridRect.rect.width - _toolGrid.padding.left - _toolGrid.padding.right
            - (columns - 1) * _toolGrid.spacing.x) / columns;
        float byHeight = (gridRect.rect.height - _toolGrid.padding.top - _toolGrid.padding.bottom
            - (rows - 1) * _toolGrid.spacing.y) / rows;

        float cell = Mathf.Max(1f, Mathf.Min(byWidth, byHeight));
        if (Mathf.Approximately(cell, _toolGrid.cellSize.x)) return;

        _toolGrid.cellSize = new Vector2(cell, cell);
    }

    private void BindAssistantButton(Transform toolGrid)
    {
        _assistantButton = UIButton.Create(toolGrid, "Tool_Assistant", "Assistant");
        _assistantButton.Button.onClick.AddListener(OnAssistantClicked);
    }

    private void OnAssistantClicked()
    {
        Watch assistant = Assistant;
        if (assistant == null) return;
        bool turningOn = !assistant.IsGeminiActive;
        assistant.SetGeminiActive(turningOn, AssistantCause.User);
    }

    private void OnAssistantActiveChanged(bool active, GeminiStatus status)
    {
        bool live = active && status == GeminiStatus.Live;
        UIButton.SetSelected(_assistantButton, live);

        RefreshOptionCards();

        if (active && !live)
        {
            if (_connectingPulse == null && isActiveAndEnabled)
                _connectingPulse = StartCoroutine(ConnectingPulse());
            return;
        }

        if (_connectingPulse != null)
        {
            StopCoroutine(_connectingPulse);
            _connectingPulse = null;
        }
        SetAssistantLabel("Assistant");
    }

    private Coroutine _connectingPulse;
    private const float ConnectingPulseSeconds = 0.4f;

    private IEnumerator ConnectingPulse()
    {
        int dots = 0;
        var wait = new WaitForSeconds(ConnectingPulseSeconds);
        while (true)
        {
            SetAssistantLabel("Connecting" + new string('.', dots));
            dots = (dots + 1) % 4;
            yield return wait;
        }
    }

    private void SetAssistantLabel(string text)
    {
        if (_assistantButton != null && _assistantButton.Text != null)
            _assistantButton.Text.text = text;
    }

    private void BindToolContents()
    {
        Transform contentArea = _toolCard != null ? _toolCard.Content : null;
        if (contentArea == null) return;

        _toolContents.Clear();
        for (int i = 0; i < Tools.Length; i++)
        {
            ToolType tool = Tools[i];
            Transform content = contentArea.Find($"{Label(tool)}Content");
            if (content == null)
            {
                Debug.LogError($"[ToolPanelUI] No pane named '{Label(tool)}Content' under ContentArea; " +
                               $"the {Label(tool)} tool will show an empty subpanel.");
                continue;
            }

            _toolContents[tool] = content.gameObject;
            SizeHeader(content.gameObject);
            AddDescription(content.gameObject, Description(tool));

            VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null) ApplyPaneLayout(contentLayout);
        }

        if (_assistantCard != null && _assistantCard.Content != null)
            _assistantContent = BuildAssistantContent(_assistantCard.Content);

        ApplyDividers(transform);
    }

    private GameObject BuildAssistantContent(Transform contentArea)
    {
        GameObject content = new GameObject("AssistantContent");
        content.transform.SetParent(contentArea, false);

        RectTransform rect = content.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
        ApplyPaneLayout(layout);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        AddHeader(content, "Assistant");
        AddDescription(content, "The Assistant works the tools for you by voice. Pick how fast its actions play out.");

        var buttons = new (string, string, UnityEngine.Events.UnityAction)[AssistantSpeedLabels.Length];
        for (int i = 0; i < AssistantSpeedLabels.Length; i++)
        {
            int captured = i;
            buttons[i] = ("AssistantSpeed_" + i, AssistantSpeedLabels[i], () => OnAssistantSpeedClicked(captured));
        }
        _assistantSpeedRow = AddToggleRowIn(content, "AssistantSpeedRow", buttons);
        _assistantSpeedRow?.SetSelected(0);

        content.SetActive(false);
        return content;
    }

    private void OnAssistantSpeedClicked(int index)
    {
        if (!ApplyAssistantSpeed(index)) return;
        StateChannel.RecordState("assistantSpeed",
            $"the assistant's motion speed is {AssistantSpeedLabels[_assistantSpeedIndex]}");
    }

    private bool ApplyAssistantSpeed(int index)
    {
        if (index < 0 || index >= AssistantSpeedLabels.Length || _assistantSpeedIndex == index) return false;
        _assistantSpeedIndex = index;
        _assistantSpeedRow?.SetSelected(index);
        var sheets = Scene.Sheets;
        if (sheets != null) sheets.SetAgentMotion(AssistantSpeedValues[index], AssistantSpeedValues[index] <= 0f);
        return true;
    }

    public string AssistantSpeedName => AssistantSpeedLabels[_assistantSpeedIndex];

    public bool SetAssistantSpeed(string option)
    {
        int index = MatchAssistantSpeed(option);
        if (index < 0) return false;
        OnAssistantSpeedClicked(index);
        return true;
    }

    private static string NormalizeSpeed(string s) => s.Trim().ToLowerInvariant().Replace(" ", "");

    private static int MatchAssistantSpeed(string option)
    {
        if (string.IsNullOrEmpty(option)) return -1;
        string wanted = NormalizeSpeed(option);

        for (int i = 0; i < AssistantSpeedLabels.Length; i++)
        {
            string label = NormalizeSpeed(AssistantSpeedLabels[i]);
            if (wanted == label) return i;

            int unit = label.IndexOf("m/s", StringComparison.Ordinal);
            if (unit > 0 && wanted == label.Substring(0, unit)) return i;
        }
        return -1;
    }

    private void BindTitleBar()
    {
        Transform titleBar = FindTransform.FindDeep(transform, "TitleBar");
        if (titleBar == null) return;

        Transform undoAllBtn = FindTransform.FindDeep(titleBar, "UndoAll_Btn");
        if (undoAllBtn != null)
        {
            Button btn = undoAllBtn.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(OnUndoAll);

            Transform textT = undoAllBtn.Find("Text");
            TextMeshProUGUI label = textT != null
                ? textT.GetComponent<TextMeshProUGUI>()
                : undoAllBtn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = "Undo All";

            UIButton.Adopt(undoAllBtn.gameObject);

            LayoutElement undoAllLE = undoAllBtn.GetComponent<LayoutElement>();
            if (undoAllLE == null) undoAllLE = undoAllBtn.gameObject.AddComponent<LayoutElement>();
            undoAllLE.minHeight = Style.Button.y;
            undoAllLE.preferredHeight = Style.Button.y;
            undoAllLE.preferredWidth = Style.TitleBarButtonWidth;
            undoAllLE.flexibleWidth = 0f;

            Transform anchor = undoAllBtn.parent != null && undoAllBtn.parent.name.EndsWith("_Border")
                ? undoAllBtn.parent
                : undoAllBtn;
            UIButton.Handle undo = UIButton.Create(anchor.parent, "Undo_Btn", "Undo",
                width: Style.TitleBarButtonWidth);
            undo.Root.transform.SetSiblingIndex(anchor.GetSiblingIndex());
            undo.Button.onClick.AddListener(OnUndo);
        }
    }

    private void OnUndo()
    {
        if (toolManager != null) toolManager.Undo();
    }

    private void OnToolButtonClicked(ToolType tool)
    {
        if (toolManager == null) return;
        toolManager.SelectTool(tool);
    }

    private void OnUndoAll()
    {
        if (toolManager != null)
        {
            toolManager.UndoAll();
            toolManager.DeselectTool();
            toolManager.ForgetSuspendedTool();
            _suspendedSlot = -1;
        }
    }

    private static bool HasOptions(ToolType tool) => tool != ToolType.None;

    private bool AssistantPaneWanted() => _assistant != null && _assistant.IsGeminiActive;

    private void OnToolChanged(ToolType selected)
    {
        for (int i = 0; i < _toolButtons.Count; i++)
            ApplyToolButtonVisual(_toolButtons[i], _toolButtons[i].Tool == selected);

        foreach (KeyValuePair<ToolType, GameObject> pair in _toolContents)
            pair.Value.SetActive(pair.Key == selected);

        RefreshOptionCards();
    }

    private readonly List<GameObject> _prewarmActivated = new List<GameObject>();

    public void PrewarmContent(bool on)
    {
        if (on)
        {
            _prewarmActivated.Clear();
            foreach (KeyValuePair<ToolType, GameObject> pair in _toolContents) WarmActivate(pair.Value);
            WarmActivate(_assistantContent);
            WarmActivate(_toolCard?.Root);
            WarmActivate(_assistantCard?.Root);
            return;
        }

        for (int i = 0; i < _prewarmActivated.Count; i++)
            if (_prewarmActivated[i] != null) _prewarmActivated[i].SetActive(false);
        _prewarmActivated.Clear();
    }

    private void WarmActivate(GameObject go)
    {
        if (go == null || go.activeSelf) return;
        go.SetActive(true);
        _prewarmActivated.Add(go);
    }

    private void RefreshOptionCards()
    {
        ToolType selected = toolManager != null ? toolManager.SelectedTool : ToolType.None;
        bool assistantPane = AssistantPaneWanted();

        if (_assistantContent != null) _assistantContent.SetActive(assistantPane);

        SetCardOpen(_toolCard, HasOptions(selected));
        SetCardOpen(_assistantCard, assistantPane);

        ReportCardStack();
        QueueOptionsResize();
    }

    private void SetCardOpen(OptionsCard card, bool open)
    {
        if (card == null || card.Root == null) return;

        if (card.Root.activeSelf != open) card.Root.SetActive(open);

        bool listed = _stack.Contains(card);
        if (open && !listed) OpenCard(card);
        else if (!open && listed) _stack.Remove(card);
    }

    private void OpenCard(OptionsCard card)
    {
        int slot = card == _toolCard ? _suspendedSlot : -1;
        if (card == _toolCard) _suspendedSlot = -1;

        if (slot >= 0 && slot <= _stack.Count) _stack.Insert(slot, card);
        else _stack.Add(card);
    }

    private void LayoutOptionCards()
    {
        float offset = Style.Panel.y;

        for (int i = 0; i < _stack.Count; i++)
        {
            RectTransform rect = _stack[i].Rect;
            if (rect == null) continue;

            offset += Style.SmallPadding;

            Vector2 pos = rect.anchoredPosition;
            pos.y = -offset;
            rect.anchoredPosition = pos;

            offset += rect.sizeDelta.y;
        }

        RectTransform canvas = CanvasRect;
        if (canvas != null) canvas.sizeDelta = new Vector2(Style.Panel.x, offset);

        InvalidateGrabBounds();
    }

    private float StackHeight()
    {
        float total = 0f;
        for (int i = 0; i < _stack.Count; i++)
            if (_stack[i].Rect != null) total += _stack[i].Rect.sizeDelta.y;
        return total;
    }

    private RectTransform PaneRect(OptionsCard card)
    {
        GameObject pane = card == _assistantCard
            ? _assistantContent
            : GetToolContent(toolManager != null ? toolManager.SelectedTool : ToolType.None);

        return pane != null ? pane.transform as RectTransform : null;
    }

    private void SizeCardToContent(OptionsCard card)
    {
        if (card == null || card.Rect == null) return;

        RectTransform paneRect = PaneRect(card);
        if (paneRect == null) return;

        if (!UIMeasure.TryPreferredHeight(card.Rect, paneRect, out float contentHeight)) return;

        if (card == _toolCard && toolManager != null &&
            ResolveSwatchGrid(toolManager.SelectedTool, paneRect) &&
            !UIMeasure.TryPreferredHeight(card.Rect, paneRect, out contentHeight)) return;

        Vector2 size = card.Rect.sizeDelta;
        size.y = Mathf.Min(contentHeight + Style.SmallPadding, Style.Subpanel.y);
        card.Rect.sizeDelta = size;
    }

    private void SizeCardsToContent()
    {
        for (int i = 0; i < _stack.Count; i++) SizeCardToContent(_stack[i]);
    }

    private void ResizeOptionCards()
    {
        if (_stack.Count == 0) return;

        RectTransform canvas = CanvasRect;
        if (canvas != null) LayoutRebuilder.ForceRebuildLayoutImmediate(canvas);
        SizeCardsToContent();
        LayoutOptionCards();
    }

    private void QueueOptionsResize()
    {
        SizeCardsToContent();
        LayoutOptionCards();

        if (!isActiveAndEnabled) return;
        if (_resizeRoutine != null) StopCoroutine(_resizeRoutine);
        _resizeRoutine = StartCoroutine(ResizeOptionsUntilStable());
    }

    private IEnumerator ResizeOptionsUntilStable()
    {
        yield return UILayout.Converge(
            () => _stack.Count > 0,
            ResizeOptionCards,
            StackHeight);

        _resizeRoutine = null;
    }

    private void ReportCardStack()
    {
        bool tool = _toolCard != null && _stack.Contains(_toolCard);
        bool assistant = _assistantCard != null && _stack.Contains(_assistantCard);

        string what;
        if (tool && assistant)
            what = _stack.IndexOf(_assistantCard) < _stack.IndexOf(_toolCard)
                ? "the assistant options sit above the tool options"
                : "the tool options sit above the assistant options";
        else if (tool) what = "only the tool options are open";
        else if (assistant) what = "only the assistant options are open";
        else what = "no options subpanel is open";

        StateChannel.SetState("optionsPanels", what);
    }

    private void ApplyToolButtonVisual(ToolButtonVisual visual, bool active)
    {
        if (visual == null) return;
        UIButton.SetSelected(visual.Handle, active);
    }

    public override void ShowPanel()
    {
        if (_canvas == null) return;
        ShowCanvas();
        QueueFitTiles();
        PanelGuard.ClearToolPanel();
        StateChannel.RecordState("toolPanel", "the tool panel is open, so the tool buttons are on screen");

        Watch assistant = Assistant;
        if (assistant != null) assistant.NotifyIntent();

        if (toolManager != null) toolManager.ResumeTool();

        RefreshOptionCards();

        InvalidateGrabBounds();
    }

    public override void HidePanel()
    {
        if (_canvas == null) return;
        if (IsVisible && toolManager != null)
        {
            _suspendedSlot = _stack.IndexOf(_toolCard);
            toolManager.SuspendTool();
        }
        HideCanvas();
        if (StateChannel.UserDriven) PanelGuard.MarkToolPanelClosedByUser();
        StateChannel.RecordState("toolPanel", "the tool panel is closed, so the tool buttons are off screen");

        Watch assistant = Assistant;
        if (assistant != null) assistant.NotifyIntentEnded();
    }

    private GameObject GetToolContent(ToolType tool) =>
        _toolContents.TryGetValue(tool, out GameObject go) ? go : null;

    private ButtonList AddToggleRowIn(GameObject content, string rowName,
        params (string name, string label, UnityEngine.Events.UnityAction onClick)[] buttons)
    {
        if (content == null) return null;

        GameObject row = new GameObject(rowName);
        row.transform.SetParent(content.transform, false);
        RectTransform rowRect = row.AddComponent<RectTransform>();

        UILayout.FixedHeight(row, Style.Button.y);

        ButtonList list = new ButtonList(rowRect, new ButtonList.Options
        {
            axis = ButtonList.Axis.Horizontal,
            sizing = ButtonList.Sizing.Equal
        });

        InsertBelowHeader(content, row.transform);

        for (int i = 0; i < buttons.Length; i++)
            list.Add(buttons[i].name, buttons[i].label, buttons[i].onClick);

        list.SetSelected(-1);
        return list;
    }

    public ButtonList AddSwatchGrid(ToolType tool, IReadOnlyList<Color> colors,
        UnityEngine.Events.UnityAction<int> onPick)
    {
        GameObject content = GetToolContent(tool);
        if (content == null || colors == null || colors.Count == 0) return null;

        GameObject grid = new GameObject("SwatchGrid");
        grid.transform.SetParent(content.transform, false);
        RectTransform gridRect = grid.AddComponent<RectTransform>();

        VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
        float padX = contentLayout != null
            ? contentLayout.padding.left + contentLayout.padding.right
            : 0f;

        int columns = colors.Count;
        float cell = SwatchCell(Style.Panel.x - Style.SmallBorder * 2f - padX, columns);

        LayoutElement host = UILayout.FixedHeight(grid, cell);

        ButtonList list = new ButtonList(gridRect, new ButtonList.Options
        {
            sizing = ButtonList.Sizing.Square,
            cellSize = cell,
            columns = columns
        });

        for (int i = 0; i < colors.Count; i++)
        {
            int captured = i;
            list.AddSwatch($"Swatch_{i}", colors[i], () => onPick?.Invoke(captured));
        }

        InsertBelowHeader(content, grid.transform);

        _swatchGrids[tool] = new SwatchGrid
        {
            List = list,
            Host = host,
            Columns = columns,
            PadX = padX
        };

        list.SetSelected(-1);
        return list;
    }

    private static float SwatchCell(float innerWidth, int columns) =>
        (innerWidth - (columns - 1) * Style.SmallPadding) / Mathf.Max(columns, 1);

    private bool ResolveSwatchGrid(ToolType tool, RectTransform paneRect)
    {
        if (!_swatchGrids.TryGetValue(tool, out SwatchGrid grid)) return false;

        float inner = paneRect.rect.width - grid.PadX;
        if (inner <= 0f || Mathf.Approximately(inner, grid.ResolvedWidth)) return false;

        float cell = SwatchCell(inner, grid.Columns);
        if (cell <= 0f) return false;

        grid.List.SetCellSize(cell);
        if (grid.Host != null)
        {
            grid.Host.minHeight = cell;
            grid.Host.preferredHeight = cell;
        }

        grid.ResolvedWidth = inner;
        return true;
    }

    private static void SizeHeader(GameObject content)
    {
        Transform header = content.transform.Find("Header");
        if (header != null) UILayout.FixedHeight(header.gameObject, Style.HeaderHeight);
    }

    private static void AddHeader(GameObject content, string text)
    {
        if (content.transform.Find("Header") != null) return;

        TextMeshProUGUI label = UILabel.Make(content.transform, "Header", Style.Black,
            TextAlignmentOptions.Left, TextOverflowModes.Ellipsis, bold: true);
        label.text = text;
        label.transform.SetAsFirstSibling();

        SizeHeader(content);
    }

    private void AddDescription(GameObject content, string text)
    {
        if (string.IsNullOrEmpty(text) || content.transform.Find("Description") != null) return;

        TextMeshProUGUI label = UILabel.Make(content.transform, "Description", Style.Black,
            TextAlignmentOptions.TopLeft, TextOverflowModes.Overflow, wrap: true);
        label.text = text;

        InsertBelowHeader(content, label.transform);
    }

    private static void InsertBelowHeader(GameObject content, Transform child)
    {
        Transform anchor = content.transform.Find("Header");
        child.SetSiblingIndex(anchor != null ? anchor.GetSiblingIndex() + 1 : 0);
    }
}
