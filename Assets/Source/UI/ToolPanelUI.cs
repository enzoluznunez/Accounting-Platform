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

    // The order the tiles appear in, and the only thing that decides it: the grid
    // is filled in this order regardless of how the buttons sit in the scene. The
    // first row moves the sheet around the room, the second changes what it shows,
    // and the assistant spans the third — so a row is one kind of verb and the
    // grouping needs no label to explain it.
    public static readonly ToolType[] Tools =
    {
        ToolType.Move,
        ToolType.Rotate,
        ToolType.Scale,

        ToolType.Profile,
        ToolType.Filter,
        ToolType.Sort
    };

    public static string Label(ToolType tool) => tool.ToString();

    public static string Description(ToolType tool)
    {
        switch (tool)
        {
            case ToolType.Move:
                return "Slides the sheet through the room, so you can stand where you want and bring the numbers to you. Grab it anywhere with one hand and carry it there.";
            case ToolType.Rotate:
                return "Turns the sheet on the spot, so you can read it from another side without walking around it. Grab it with both hands and twist to the angle you want.";
            case ToolType.Scale:
                return "Resizes the sheet, from a tabletop model up to a wall you stand inside. Grab it with both hands, then move them apart to enlarge it or together to shrink it.";
            case ToolType.Profile:
                return "Lifts one row or column clear of the sheet and reports its count, range, average and total. Press a bar, then sweep your finger along the line you want raised.";
            case ToolType.Sort:
                return "Reorders the rows or the metrics, so the ones you are comparing sit side by side. Pinch a line beside its label, or anywhere along it, and slide it into place.";
            case ToolType.Filter:
                return "Chooses what stands on the sheet, narrowing it to what you asked for. Open By Company or By Metric, then poke a bar to take that line off the sheet, or tap a name in the list; a filled square means it is showing, and tapping it again brings it back.";
            default:
                return string.Empty;
        }
    }

    private class ToolButtonVisual
    {
        public ToolType Tool;
        public UIButton.Handle Handle;
    }

    private class OptionsCard
    {
        public GameObject Root;
        public RectTransform Rect;
        public Transform Content;
    }

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

            // Tools is the order, so the tile is moved to where the list puts it
            // rather than left wherever the scene happened to hold it.
            btnT.SetSiblingIndex(i);

            UIButton.Handle h = UIButton.Adopt(btnT.gameObject);
            StyleTileLabel(h);
            ToolButtonVisual visual = new ToolButtonVisual { Tool = tool, Handle = h };
            _toolButtons.Add(visual);

            ToolType captured = tool;
            Button btn = btnT.GetComponent<Button>();
            if (btn != null)
                btn.onClick.AddListener(() => OnToolButtonClicked(captured));
        }

        BindAssistantButton(toolGrid);
        DropUnboundTiles(toolGrid);
        SquareTiles(toolGrid.GetComponent<GridLayoutGroup>());
    }

    // A tile carries its label at the panel's title weight: at the size a tile is
    // drawn, body text reads as a caption someone forgot to finish.
    private static void StyleTileLabel(UIButton.Handle h)
    {
        if (h != null) Style.ApplyTitle(h.Text);
    }

    // A tool that has been removed can leave its tile behind in the scene, where
    // nothing binds it: it keeps whatever styling it was saved with, answers to
    // no press, and still takes a cell from the ones that work. The grid holds
    // the tiles this panel built and nothing else.
    private void DropUnboundTiles(Transform toolGrid)
    {
        for (int i = toolGrid.childCount - 1; i >= 0; i--)
        {
            GameObject child = toolGrid.GetChild(i).gameObject;
            if (child == _assistantButton.Root || IsBoundTile(child)) continue;

            Debug.LogWarning($"[ToolPanelUI] '{child.name}' under ToolGrid matches no tool in Tools; " +
                             "dropping it from the grid.");

            // Destroy lands at the end of the frame, so the tile is taken out of
            // the layout and renamed now rather than counted for another pass.
            child.SetActive(false);
            child.name += "_Unbound";
            Destroy(child);
        }
    }

    private bool IsBoundTile(GameObject go)
    {
        for (int i = 0; i < _toolButtons.Count; i++)
            if (_toolButtons[i].Handle.Root == go) return true;
        return false;
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
        int span = Mathf.Min(AssistantSpan, columns);
        int cells = _toolButtons.Count + span;
        int rows = Mathf.Max(1, Mathf.CeilToInt(cells / (float)columns));

        float byWidth = (gridRect.rect.width - _toolGrid.padding.left - _toolGrid.padding.right
            - (columns - 1) * _toolGrid.spacing.x) / columns;
        float byHeight = (gridRect.rect.height - _toolGrid.padding.top - _toolGrid.padding.bottom
            - (rows - 1) * _toolGrid.spacing.y) / rows;

        float cell = Mathf.Max(1f, Mathf.Min(byWidth, byHeight));
        if (!Mathf.Approximately(cell, _toolGrid.cellSize.x))
            _toolGrid.cellSize = new Vector2(cell, cell);

        RoundTiles(cell);
        PlaceAssistantTile(cell, columns, span);
    }

    // The corner a tile is cut with follows the size it ended up at, so a panel
    // that fits its tiles to the room it has does not end up with the corners of
    // a larger one. SetRadius sits out a tile that is already cut to size.
    private void RoundTiles(float cell)
    {
        float radius = Style.TileRadius(cell);

        for (int i = 0; i < _toolButtons.Count; i++)
            UIButton.SetRadius(_toolButtons[i].Handle, radius);

        UIButton.SetRadius(_assistantButton, radius);
    }

    // How many cells the assistant tile covers. The tools fill the grid a cell at
    // a time; this one takes the rest of the row they end on, so the block stays
    // rectangular instead of trailing an empty cell.
    private const int AssistantSpan = 3;

    private void BindAssistantButton(Transform toolGrid)
    {
        _assistantButton = UIButton.Create(toolGrid, "Tool_Assistant", "Assistant");
        _assistantButton.Button.onClick.AddListener(OnAssistantClicked);
        StyleTileLabel(_assistantButton);

        // A grid lays out every child at one cell, and a cell is what this tile is
        // not. Opting out of the layout leaves it to FitTiles to place, from the
        // same cell size and spacing the grid gave the tools.
        RectTransform rect = _assistantButton.Root.GetComponent<RectTransform>();
        LayoutElement le = _assistantButton.Root.GetComponent<LayoutElement>();
        if (le == null) le = _assistantButton.Root.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
    }

    private void PlaceAssistantTile(float cell, int columns, int span)
    {
        if (_assistantButton.Root == null || _toolGrid == null) return;

        RectTransform rect = _assistantButton.Root.GetComponent<RectTransform>();
        if (rect == null) return;

        // The tiles the grid arranges are exactly the bound ones: DropUnboundTiles
        // clears out everything else, and the assistant opts out with ignoreLayout.
        int at = _toolButtons.Count;

        // The tile has to sit whole: when the row the tools end on cannot hold it,
        // it starts the next one.
        int row = at / columns;
        int column = at % columns;
        if (column + span > columns) { row++; column = 0; }

        float pitchX = cell + _toolGrid.spacing.x;
        float pitchY = cell + _toolGrid.spacing.y;

        rect.sizeDelta = new Vector2(span * cell + (span - 1) * _toolGrid.spacing.x, cell);
        rect.anchoredPosition = new Vector2(
            _toolGrid.padding.left + ColumnInset(cell, columns) + column * pitchX,
            -(_toolGrid.padding.top + row * pitchY));
    }

    // Square cells rarely fill the width they are given, and the grid centres the
    // columns in what is left over. This tile is placed by hand, so it has to be
    // told about that gutter or it starts a tile's width left of the column it
    // belongs under.
    private float ColumnInset(float cell, int columns)
    {
        RectTransform gridRect = _toolGrid.transform as RectTransform;
        if (gridRect == null) return 0f;

        TextAnchor at = _toolGrid.childAlignment;
        bool centred = at == TextAnchor.UpperCenter || at == TextAnchor.MiddleCenter ||
                       at == TextAnchor.LowerCenter;
        if (!centred) return 0f;

        float columnsWidth = columns * cell + (columns - 1) * _toolGrid.spacing.x;
        float free = gridRect.rect.width - _toolGrid.padding.left - _toolGrid.padding.right
            - columnsWidth;

        return Mathf.Max(0f, free * 0.5f);
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
        AddDescription(content, "Works the tools for you by voice: ask for a sheet, a sort or a filter and it carries the request out. Pick how fast its actions play out on screen.");

        var buttons = new (string, string, UnityEngine.Events.UnityAction)[AssistantSpeedLabels.Length];
        for (int i = 0; i < AssistantSpeedLabels.Length; i++)
        {
            int captured = i;
            buttons[i] = ("AssistantSpeed_" + i, AssistantSpeedLabels[i], () => OnAssistantSpeedClicked(captured));
        }
        _assistantSpeedRow = AddToggleRowIn(content, "AssistantSpeedRow", true, buttons);
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

    // A row of buttons a tool can ask for: one press each, sized to share the
    // width. 'belowHeader' puts it above the description, which is where the
    // assistant's own speed row belongs; a tool's row goes last, under whatever
    // it has already added.
    public ButtonList AddToggleRow(ToolType tool, string rowName,
        params (string name, string label, UnityEngine.Events.UnityAction onClick)[] buttons)
    {
        GameObject content = GetToolContent(tool);
        if (content == null) return null;

        ReplaceNamed(content.transform, rowName);
        return AddToggleRowIn(content, rowName, false, buttons);
    }

    // Destroy is deferred to the end of the frame, so whatever is being replaced
    // is renamed out of the way first: until it is actually gone it would still
    // answer to the name the replacement is about to take.
    private void ReplaceNamed(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing == null) return;

        existing.name = name + "_Old";
        Destroy(existing.gameObject);
    }

    private ButtonList AddToggleRowIn(GameObject content, string rowName, bool belowHeader,
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

        if (belowHeader) InsertBelowHeader(content, row.transform);
        else row.transform.SetAsLastSibling();

        for (int i = 0; i < buttons.Length; i++)
            list.Add(buttons[i].name, buttons[i].label, buttons[i].onClick);

        list.SetSelected(-1);
        return list;
    }

    // A scrollable column of toggles in a tool's pane, for options that are not
    // exclusive: unlike a toggle row, any number can be on, so the caller keeps
    // the handles and lights them itself.
    // 'name' lets one tool keep more than one list: each replaces only the list
    // it named, so rebuilding the metrics does not take the categories with it.
    public ButtonList AddOptionList(ToolType tool, float height, string name = "OptionList")
    {
        GameObject content = GetToolContent(tool);
        if (content == null) return null;

        ReplaceNamed(content.transform, name);

        GameObject host = new GameObject(name);
        host.transform.SetParent(content.transform, false);
        RectTransform rect = host.AddComponent<RectTransform>();

        UILayout.FixedHeight(host, height);

        ButtonList list = new ButtonList(rect, new ButtonList.Options
        {
            axis = ButtonList.Axis.Vertical,
            sizing = ButtonList.Sizing.Measured,
            alignment = TextAnchor.UpperLeft,
            itemHeight = Style.Subbutton.y,
            backed = true,
            scrollable = true
        });

        host.transform.SetAsLastSibling();
        return list;
    }

    // A scrollable column of check rows: the label on the left, a square on the
    // right that is filled while the thing is on the sheet. Unlike a toggle row
    // any number can be checked, so the caller keeps the handles and sets the
    // squares itself.
    public ButtonList AddCheckList(ToolType tool, float height, string name = "CheckList")
    {
        GameObject content = GetToolContent(tool);
        if (content == null) return null;

        ReplaceNamed(content.transform, name);

        GameObject host = new GameObject(name);
        host.transform.SetParent(content.transform, false);
        RectTransform rect = host.AddComponent<RectTransform>();

        UILayout.FixedHeight(host, height);

        ButtonList list = new ButtonList(rect, new ButtonList.Options
        {
            axis = ButtonList.Axis.Vertical,
            sizing = ButtonList.Sizing.Measured,
            alignment = TextAnchor.UpperLeft,
            itemHeight = Style.Subbutton.y,
            backed = true,
            scrollable = true,
            checkable = true
        });

        host.transform.SetAsLastSibling();
        return list;
    }

    // A tool whose pane grew or shrank without the tool changing: the card is
    // measured again so it closes up behind a list that was taken away.
    public void ContentChanged() => QueueOptionsResize();

    // Takes a tool's named block off its pane, for a list that is closed rather
    // than rebuilt: the Filter tool shows one axis at a time, and the axis that
    // is not open leaves no empty band behind it.
    public void RemoveContent(ToolType tool, string name)
    {
        GameObject content = GetToolContent(tool);
        if (content == null) return;
        ReplaceNamed(content.transform, name);
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
