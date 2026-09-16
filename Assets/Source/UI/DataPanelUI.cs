using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class DataPanelUI : PanelUI, IDataPanel
{

    public ManageSheets sheetManager;

    private ManageDatasets _datasets;

    private SheetView _grid;
    private ButtonList _tabs;
    private ButtonList _rail;
    private UIButton.Handle _expand;

    private RectTransform _panelRect;
    private RectTransform _railSlot;
    private RectTransform _tabSlot;
    private float _railWidth;
    private Vector2 _panelSize = Style.DataPanel;
    private GameObject _body;
    private int _sheetId = ManageSheets.FirstSheetId;
    private bool _collapsed;
    private bool _expanded;

    private DataSource _source;
    private bool _gridDirty;
    private int _gridRetries;
    private bool _tabsDirty;

    private void Awake()
    {
        InitPanel(Style.DataPanel);

        try
        {
            BuildLayout();
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataPanelUI] Setup failed; the panel will still open but may be incomplete: {e}");
        }
        finally
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }
    }

    private void Start()
    {
        _datasets = ManageDatasets.Instance;

        if (_datasets != null)
        {
            _datasets.OnDatasetsChanged += OnDatasetsChanged;
            _datasets.OnActiveDatasetChanged += OnActiveDatasetChanged;
        }

        if (sheetManager == null) sheetManager = FindAnyObjectByType<ManageSheets>();
        if (sheetManager != null) sheetManager.OnSheetsChanged += OnSheetsChanged;

        RefreshRail();
        RefreshTabs();
        LayoutSatellites();

        DataSource source = _datasets != null ? _datasets.Active : null;
        if (source == null && sheetManager != null) source = sheetManager.dataSource;
        Rebind(source);
        UpdateEmptyState();
    }

    private void OnDestroy()
    {
        if (_datasets != null)
        {
            _datasets.OnDatasetsChanged -= OnDatasetsChanged;
            _datasets.OnActiveDatasetChanged -= OnActiveDatasetChanged;
        }
        if (sheetManager != null) sheetManager.OnSheetsChanged -= OnSheetsChanged;
        Unsubscribe();
        _grid?.Dispose();
    }

    private void Unsubscribe()
    {
        if (_source == null) return;
        _source.OnDataLoaded -= OnDataLoaded;
        _source.OnOrderChanged -= OnOrderChanged;
        _source = null;
    }

    private void OnDataLoaded() => MarkGridDirty();

    private void OnOrderChanged() => MarkGridDirty();

    private void OnSheetsChanged()
    {
        _tabsDirty = true;
        MarkGridDirty();
    }

    private bool Present => _body != null && _body.activeInHierarchy;

    private void MarkGridDirty()
    {
        _gridDirty = true;
        _gridRetries = 0;
    }

    private void RebuildVisible()
    {
        if (!Present || !_gridDirty) return;

        if (_grid == null)
        {
            _gridDirty = false;
            _gridRetries = 0;
            return;
        }

        bool last = _gridRetries >= UILayout.SettlePasses;
        if (_grid.Rebuild(_source, ActiveSheet, last) || last)
        {
            _gridDirty = false;
            _gridRetries = 0;
            return;
        }

        _gridRetries++;
    }

    private void FlushPending()
    {
        if (_tabsDirty)
        {
            _tabsDirty = false;
            RefreshTabs();
            LayoutSatellites();
        }

        RebuildVisible();
    }

    protected override void OnLateUpdate()
    {
        if (!_tabsDirty && !(_gridDirty && Present)) return;

        FlushPending();
        RefreshPanelSize();
    }

    private void BuildLayout()
    {
        Transform panelRoot = FindTransform.FindDeep(transform, "PanelRoot");
        if (panelRoot == null)
        {
            Debug.LogError("[DataPanelUI] PanelRoot not found; the Data Panel canvas is missing its shell.");
            return;
        }

        _panelRect = FindTransform.FindDeep(transform, "PrimaryPanel") as RectTransform;

        _body = new GameObject("Body", typeof(RectTransform));
        _body.transform.SetParent(panelRoot, false);

        VerticalLayoutGroup vlg = _body.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        ApplyPaneLayout(vlg);

        LayoutElement bodyLE = _body.AddComponent<LayoutElement>();
        bodyLE.flexibleHeight = 1f;

        RectTransform bodyRect = _body.GetComponent<RectTransform>();
        _grid = new SheetView(bodyRect);
        _grid.SetVisible(true);

        BindTitleBar();
        BuildRailSlot();
        BuildTabSlot();
        SetGrabRects(_panelRect, _railSlot, _tabSlot);
        LayoutSatellites();
    }

    private void BindTitleBar()
    {
        Transform titleBar = FindTransform.FindDeep(transform, "TitleBar");
        if (titleBar == null) return;

        _expand = UIButton.Create(titleBar, "Expand_Btn", "Expand", width: Style.TitleBarButtonWidth);
        _expand.Button.onClick.AddListener(ToggleExpand);
    }

    private void ToggleExpand() => SetExpanded(!_expanded);

    public bool IsExpanded => _expanded;

    public void SetExpanded(bool on)
    {
        if (on && !CanExpand) return;
        if (_expanded == on) return;

        _expanded = on;
        UIButton.SetSelected(_expand, _expanded);
        RefreshPanelSize();

        StateChannel.RecordState("dataPanelSize", _expanded
            ? "the data panel is expanded to fit the sheet it is showing"
            : "the data panel is at its default size");
    }

    private static float TabStrip => Style.SmallPadding + Style.Subbutton.y;

    private static Vector2 PanelChrome => new Vector2(
        Style.SmallBorder * 2f + Style.PanelInset * 2f,
        Style.SmallBorder * 2f + TitleBarHeight + Style.SmallPadding * 2f);

    private enum Fit { Unknown, Fits, Expands }

    public bool CanExpand => MeasureFit(out _) == Fit.Expands;

    private bool TryMeasureContent(out Vector2 content)
    {
        content = Vector2.zero;
        if (!Present) return false;

        return _grid != null && _grid.TryMeasure(out content);
    }

    private Fit MeasureFit(out Vector2 size)
    {
        size = Style.DataPanel;

        if (!TryMeasureContent(out Vector2 content)) return Fit.Unknown;
        if (content.x <= 0f || content.y <= 0f) return Fit.Fits;

        Vector2 wanted = Vector2.Max(Style.DataPanel, content + PanelChrome);
        if (wanted == Style.DataPanel) return Fit.Fits;

        size = wanted;
        return Fit.Expands;
    }

    private void SyncExpand(bool canExpand)
    {
        if (!_expanded || canExpand) return;

        _expanded = false;
        UIButton.SetSelected(_expand, false);
        StateChannel.SetState("dataPanelSize", "the data panel is at its default size");
    }

    private void ShowExpand(bool canExpand)
    {
        if (_expand == null || _expand.Root == null) return;
        if (_expand.Root.activeSelf != canExpand) _expand.Root.SetActive(canExpand);
    }

    private void RefreshPanelSize()
    {
        Fit fit = MeasureFit(out Vector2 target);
        ShowExpand(fit == Fit.Expands);
        if (fit == Fit.Unknown) return;

        SyncExpand(fit == Fit.Expands);

        Vector2 next = _expanded ? target : Style.DataPanel;
        if (next == _panelSize) return;

        _panelSize = next;
        LayoutSatellites();
    }

    protected override void ResolveDeferredLayout() => RemeasureButtons();

    private void RemeasureButtons()
    {
        _tabs?.Remeasure();

        if (_rail == null) return;

        _rail.Remeasure();
        _railWidth = _rail.MaxItemExtent;
        LayoutSatellites();
    }

    private void LayoutSatellites()
    {
        RectTransform canvas = CanvasRect;
        if (canvas == null || _panelRect == null) return;

        float rail = _railWidth > 0f ? _railWidth + Style.SmallPadding : 0f;
        float width = rail + _panelSize.x;
        float body = rail + _panelSize.x * 0.5f;

        canvas.pivot = new Vector2(0f, 1f);
        canvas.anchoredPosition = new Vector2(-body * canvas.localScale.x, 0f);
        canvas.sizeDelta = new Vector2(width, _panelSize.y + TabStrip);

        if (_railSlot != null) _railSlot.sizeDelta = new Vector2(_railWidth, _panelSize.y);
        if (_tabSlot != null) _tabSlot.sizeDelta = new Vector2(_panelSize.x, Style.Subbutton.y);

        Vector2 bottomRight = new Vector2(1f, 0f);
        _panelRect.anchorMin = bottomRight;
        _panelRect.anchorMax = bottomRight;
        _panelRect.pivot = bottomRight;
        _panelRect.anchoredPosition = Vector2.zero;
        _panelRect.sizeDelta = _panelSize;

        InvalidateGrabBounds();
    }

    private RectTransform Satellite(string name, Vector2 anchor, Vector2 size)
    {
        GameObject slot = new GameObject(name, typeof(RectTransform));
        RectTransform rt = slot.GetComponent<RectTransform>();
        rt.SetParent(CanvasRect != null ? CanvasRect : transform, false);

        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        return rt;
    }

    private void BuildRailSlot()
    {
        _railSlot = Satellite("DatasetRail", new Vector2(0f, 0f), new Vector2(0f, _panelSize.y));

        _rail = new ButtonList(_railSlot, new ButtonList.Options
        {
            axis = ButtonList.Axis.Vertical,
            sizing = ButtonList.Sizing.Measured,
            alignment = TextAnchor.UpperRight,
            padding = new RectOffset(0, 0,
                (int)Style.SmallPadding, (int)Style.SmallPadding),
            expandCrossAxis = false,
            itemHeight = Style.Subbutton.y,
            newestFirst = true,
            backed = true,
            scrollable = true
        });
    }

    private void RefreshRail()
    {
        if (_rail == null) return;

        _rail.Clear();
        _railWidth = 0f;
        InvalidateLayout();
        if (_datasets == null) return;

        IReadOnlyList<ManageDatasets.Dataset> datasets = _datasets.Datasets;
        for (int i = 0; i < datasets.Count; i++)
        {
            int captured = i;
            _rail.Add($"Dataset_{i}", datasets[i].label, () => OnDatasetChipClicked(captured));
        }

        _railWidth = _rail.MaxItemExtent;
        ApplyRailActive();
    }

    private void ApplyRailActive()
    {
        int active = (_datasets != null && !_collapsed) ? _datasets.ActiveIndex : -1;
        _rail?.SetSelected(active);
    }

    private void BuildTabSlot()
    {
        _tabSlot = Satellite("SheetTabs", new Vector2(1f, 1f),
            new Vector2(_panelSize.x, Style.Subbutton.y));

        _tabs = new ButtonList(_tabSlot, new ButtonList.Options
        {
            axis = ButtonList.Axis.Horizontal,
            sizing = ButtonList.Sizing.Measured,
            alignment = TextAnchor.MiddleLeft,
            itemHeight = Style.Subbutton.y,
            backed = true,
            scrollable = true
        });
    }

    private IReadOnlyList<CreateSheet> Sheets =>
        sheetManager != null ? sheetManager.Sheets : null;

    private CreateSheet ActiveSheet =>
        sheetManager != null ? sheetManager.SheetById(_sheetId) : null;

    private int ResolveSheetId(int wanted)
    {
        if (sheetManager == null) return -1;
        if (sheetManager.SheetById(wanted) != null) return wanted;

        IReadOnlyList<CreateSheet> sheets = sheetManager.Sheets;
        return sheets.Count > 0 ? sheets[0].sheetId : -1;
    }

    private int IndexOfSheet(int id)
    {
        IReadOnlyList<CreateSheet> sheets = Sheets;
        if (sheets == null) return -1;

        for (int i = 0; i < sheets.Count; i++)
            if (sheets[i].sheetId == id) return i;
        return -1;
    }

    private void RefreshTabs()
    {
        if (_tabs == null) return;

        _tabs.Clear();
        InvalidateLayout();

        IReadOnlyList<CreateSheet> sheets = Sheets;
        if (sheets == null) return;

        for (int i = 0; i < sheets.Count; i++)
        {
            int id = sheets[i].sheetId;
            _tabs.Add($"Sheet_{id}", $"Sheet {id}", () => ShowSheet(id));
        }

        int resolved = ResolveSheetId(_sheetId);
        if (resolved != _sheetId)
        {
            _sheetId = resolved;
            MarkGridDirty();
        }

        ApplyTabActive();
    }

    private void ApplyTabActive() => _tabs?.SetSelected(IndexOfSheet(_sheetId));

    private void OnDatasetChipClicked(int index)
    {
        if (_datasets == null) return;

        if (!_collapsed && index == _datasets.ActiveIndex) CollapseData();
        else ShowDataset(index);
    }

    public bool IsCollapsed => _collapsed;

    public void ShowDataset(int index)
    {
        if (_datasets == null) return;

        bool wasCollapsed = _collapsed;
        _collapsed = false;
        if (index != _datasets.ActiveIndex) _datasets.SwitchDataset(index);

        ApplyRailActive();
        UpdateEmptyState();
        if (wasCollapsed) StateChannel.RecordState("datasetShown", "the dataset is showing");
    }

    public void CollapseData()
    {
        if (_datasets == null || _collapsed) return;

        _collapsed = true;
        ApplyRailActive();
        UpdateEmptyState();
        StateChannel.RecordState("datasetShown", "the dataset is collapsed, hiding the sheet");
    }

    private void OnActiveDatasetChanged(int index)
    {
        _collapsed = false;
        ApplyRailActive();
        UpdateEmptyState();
    }

    private void OnDatasetsChanged()
    {
        RefreshRail();
        LayoutSatellites();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        // Listed is not read: the industries stand in the rail from startup, and
        // there is nothing to show until one of them has actually been opened.
        bool hasData = _datasets != null && _datasets.Active != null;
        bool present = hasData && !_collapsed;

        if (sheetManager != null) sheetManager.SetPresented(present);
        if (_body != null) _body.SetActive(present);

        MarkGridDirty();
        RefreshPanelSize();
    }

    public void Rebind(DataSource source)
    {
        Unsubscribe();
        _source = source;
        if (_source != null)
        {
            _source.OnDataLoaded += OnDataLoaded;
            _source.OnOrderChanged += OnOrderChanged;
        }
        OnDataLoaded();
    }

    public void ShowSheet(int sheetId)
    {
        int next = ResolveSheetId(sheetId);
        if (next < 0 || next == _sheetId) return;

        _sheetId = next;
        MarkGridDirty();
        ApplyTabActive();

        StateChannel.RecordState("sheetView", $"the data panel is showing sheet {_sheetId}");
    }

    public int ActiveSheetId => _sheetId;

    public override void ShowPanel()
    {
        if (_canvas == null)
        {
            Debug.LogError("[DataPanelUI] ShowPanel called but _canvas is null; InitPanel never found a Canvas.");
            return;
        }

        ShowCanvas();
        FlushPending();
        RefreshPanelSize();
        InvalidateGrabBounds();

        StateChannel.RecordState("dataPanel", "the data panel is open");
    }

    public override void HidePanel()
    {
        if (_canvas == null) return;
        HideCanvas();
        StateChannel.RecordState("dataPanel", "the data panel is closed");
    }

    private class SheetView
    {
        private enum CellKind { Corner, ColumnHeader, RowHeader, Value }

        private const float RowHeight = Style.CellRowHeight;
        private const float LineWidth = Style.SmallBorder;

        private float _titleColumnWidth = Style.TitleColumn;
        private float _valueColumnWidth = Style.ValueColumn;

        private static readonly Color LineColor = Style.Black;

        private readonly ScrollList _scroll;
        private DataSource _source;

        private readonly RectTransform _frozenColumn;
        private readonly RectTransform _frozenRow;
        private readonly RectTransform _frozenCorner;

        private TextMeshProUGUI _corner;
        private TextMeshProUGUI[] _columnLabels;
        private TextMeshProUGUI[] _rowLabels;
        private TextMeshProUGUI[] _valueCells;
        private int _winRowMin;
        private int _winColMin;
        private Vector2 _contentSize;

        public bool TryMeasure(out Vector2 size)
        {
            size = Vector2.zero;
            if (!_scroll.Viewport.gameObject.activeInHierarchy) return false;

            size = _contentSize;
            return true;
        }

        public SheetView(RectTransform parent)
        {
            _scroll = new ScrollList(parent, "xlsx", ScrollList.ContentSizing.Manual);

            Image background = _scroll.Content.gameObject.AddComponent<Image>();
            background.color = Style.White;
            background.raycastTarget = false;
            PanelUI.ApplyFront(background);

            _frozenColumn = Layer("FrozenColumn");
            _frozenRow = Layer("FrozenRow");
            _frozenCorner = Layer("FrozenCorner");

            if (_scroll.Scroll.horizontalScrollbar != null)
                _scroll.Scroll.horizontalScrollbar.transform.SetAsLastSibling();
            if (_scroll.Scroll.verticalScrollbar != null)
                _scroll.Scroll.verticalScrollbar.transform.SetAsLastSibling();

            _scroll.Scroll.onValueChanged.AddListener(SyncFrozen);
        }

        private RectTransform Layer(string name)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.SetParent(_scroll.Viewport, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            return rt;
        }

        public void SetVisible(bool on) => _scroll.SetVisible(on);

        public void Dispose()
        {
            if (_scroll != null && _scroll.Scroll != null)
                _scroll.Scroll.onValueChanged.RemoveListener(SyncFrozen);
        }

        private void SyncFrozen(Vector2 _)
        {
            Vector2 p = _scroll.Content.anchoredPosition;
            _frozenRow.anchoredPosition = new Vector2(p.x, 0f);
            _frozenColumn.anchoredPosition = new Vector2(0f, p.y);
        }

        private static float RowTop(int visualRow) => LineWidth + visualRow * (RowHeight + LineWidth);

        private float ColumnLeft(int visualColumn) =>
            visualColumn == 0
                ? LineWidth
                : LineWidth + _titleColumnWidth + LineWidth
                  + (visualColumn - 1) * (_valueColumnWidth + LineWidth);

        public bool Rebuild(DataSource source, CreateSheet piece, bool force)
        {
            if (TryFastRefresh(source, piece)) return true;

            _source = source;

            _scroll.Clear();
            UILayout.Clear(_frozenColumn);
            UILayout.Clear(_frozenRow);
            UILayout.Clear(_frozenCorner);

            _corner = null;
            _columnLabels = null;
            _rowLabels = null;
            _valueCells = null;

            if (!TryWindow(piece, out int rowMin, out int colMin, out int rows, out int cols))
            {
                _contentSize = Vector2.zero;
                _scroll.Content.sizeDelta = Vector2.zero;
                _frozenColumn.sizeDelta = Vector2.zero;
                _frozenRow.sizeDelta = Vector2.zero;
                _frozenCorner.sizeDelta = Vector2.zero;
                return true;
            }

            _corner = MakeLabel(_frozenCorner, CellKind.Corner);
            _corner.text = _source.RowAxisTitle ?? "";

            _columnLabels = new TextMeshProUGUI[cols];
            for (int c = 0; c < cols; c++)
            {
                _columnLabels[c] = MakeLabel(_frozenRow, CellKind.ColumnHeader);
                _columnLabels[c].text = _source.TitleAt(true, colMin + c) ?? "";
            }

            _rowLabels = new TextMeshProUGUI[rows];
            for (int r = 0; r < rows; r++)
            {
                _rowLabels[r] = MakeLabel(_frozenColumn, CellKind.RowHeader);
                _rowLabels[r].text = _source.TitleAt(false, rowMin + r) ?? "";
            }

            if (!FitColumnsToTitles() && !force) return false;

            float width = ColumnLeft(cols + 1);
            float height = RowTop(rows + 1);
            float bandHeight = RowTop(1);
            float bandWidth = ColumnLeft(1);

            _contentSize = new Vector2(width, height);

            _scroll.Content.sizeDelta = _contentSize;
            BuildDecoration(_scroll.Content, rows, cols, width, height, false);

            _frozenColumn.sizeDelta = new Vector2(bandWidth, height);
            BuildDecoration(_frozenColumn, rows, 0, bandWidth, height, true);

            _frozenRow.sizeDelta = new Vector2(width, bandHeight);
            BuildDecoration(_frozenRow, 0, cols, width, bandHeight, true);

            _frozenCorner.sizeDelta = new Vector2(bandWidth, bandHeight);
            BuildDecoration(_frozenCorner, 0, 0, bandWidth, bandHeight, true);

            _corner.rectTransform.SetAsLastSibling();
            for (int c = 0; c < cols; c++) _columnLabels[c].rectTransform.SetAsLastSibling();
            for (int r = 0; r < rows; r++) _rowLabels[r].rectTransform.SetAsLastSibling();

            Place(_corner, ColumnLeft(0), RowTop(0), _titleColumnWidth);
            for (int c = 0; c < cols; c++)
                Place(_columnLabels[c], ColumnLeft(c + 1), RowTop(0), _valueColumnWidth);

            IReadOnlyList<int> rowOrder = _source.RowOrder;
            IReadOnlyList<int> colOrder = _source.ColumnOrder;

            _winRowMin = rowMin;
            _winColMin = colMin;
            _valueCells = new TextMeshProUGUI[rows * cols];

            for (int r = 0; r < rows; r++)
            {
                float y = RowTop(r + 1);
                Place(_rowLabels[r], ColumnLeft(0), y, _titleColumnWidth);

                int dataRow = rowOrder[rowMin + r];
                for (int c = 0; c < cols; c++)
                {
                    int dataCol = colOrder[colMin + c];

                    TextMeshProUGUI cell = MakeLabel(_scroll.Content, CellKind.Value);
                    cell.text = _source.HasValue(dataRow, dataCol)
                        ? Formatter.Compact(_source.GetValue(dataRow, dataCol))
                        : "";

                    _valueCells[r * cols + c] = cell;
                    Place(cell, ColumnLeft(c + 1), y, _valueColumnWidth);
                }
            }

            SyncFrozen(Vector2.zero);
            return true;
        }

        private bool TryFastRefresh(DataSource source, CreateSheet piece)
        {
            if (source == null || source != _source || _valueCells == null) return false;
            if (_corner == null || _rowLabels == null || _columnLabels == null) return false;
            if (!TryWindow(piece, out int rowMin, out int colMin, out int rows, out int cols)) return false;
            if (rowMin != _winRowMin || colMin != _winColMin) return false;
            if (_rowLabels.Length != rows || _columnLabels.Length != cols) return false;
            if (_valueCells.Length != rows * cols) return false;

            for (int c = 0; c < cols; c++)
                _columnLabels[c].text = source.TitleAt(true, colMin + c) ?? "";
            for (int r = 0; r < rows; r++)
                _rowLabels[r].text = source.TitleAt(false, rowMin + r) ?? "";

            IReadOnlyList<int> rowOrder = source.RowOrder;
            IReadOnlyList<int> colOrder = source.ColumnOrder;
            for (int r = 0; r < rows; r++)
            {
                int dataRow = rowOrder[rowMin + r];
                for (int c = 0; c < cols; c++)
                {
                    int dataCol = colOrder[colMin + c];
                    _valueCells[r * cols + c].text = source.HasValue(dataRow, dataCol)
                        ? Formatter.Compact(source.GetValue(dataRow, dataCol))
                        : "";
                }
            }

            float titleWidth = _titleColumnWidth;
            float valueWidth = _valueColumnWidth;
            if (!FitColumnsToTitles()
                || !Mathf.Approximately(titleWidth, _titleColumnWidth)
                || !Mathf.Approximately(valueWidth, _valueColumnWidth))
            {
                _titleColumnWidth = titleWidth;
                _valueColumnWidth = valueWidth;
                _valueCells = null;
                return false;
            }
            return true;
        }

        private bool TryWindow(CreateSheet piece, out int rowMin, out int colMin, out int rows, out int cols)
        {
            rowMin = colMin = 0;
            rows = cols = 0;
            if (_source == null || !_source.IsLoaded) return false;

            int rowMax = _source.RowOrder.Count - 1;
            int colMax = _source.ColumnOrder.Count - 1;

            if (piece != null)
            {
                rowMin = Mathf.Max(0, piece.rowMin);
                colMin = Mathf.Max(0, piece.colMin);
                rowMax = Mathf.Min(rowMax, piece.rowMax);
                colMax = Mathf.Min(colMax, piece.colMax);
            }

            rows = rowMax - rowMin + 1;
            cols = colMax - colMin + 1;
            return rows > 0 && cols > 0;
        }

        private void BuildDecoration(RectTransform parent, int rows, int cols, float width, float height, bool opaque)
        {
            if (opaque) Strip(parent, Style.White, 0f, 0f, width, height);

            for (int r = 0; r <= rows + 1; r++)
                Strip(parent, LineColor, 0f, RowTop(r) - LineWidth, width, LineWidth);

            for (int c = 0; c <= cols + 1; c++)
                Strip(parent, LineColor, ColumnLeft(c) - LineWidth, 0f, LineWidth, height);
        }

        private bool FitColumnsToTitles()
        {
            float padding = Style.SmallPadding * 2f;
            bool measured = true;

            _titleColumnWidth = Style.TitleColumn;
            measured &= Widen(ref _titleColumnWidth, _corner, padding);
            for (int r = 0; r < _rowLabels.Length; r++)
                measured &= Widen(ref _titleColumnWidth, _rowLabels[r], padding);

            _valueColumnWidth = Style.ValueColumn;
            for (int c = 0; c < _columnLabels.Length; c++)
                measured &= Widen(ref _valueColumnWidth, _columnLabels[c], padding);

            return measured;
        }

        private static bool Widen(ref float column, TextMeshProUGUI label, float padding)
        {
            if (!UIMeasure.TryTextWidth(label, out float width)) return false;

            column = Mathf.Max(column, width + padding);
            return true;
        }

        private TextMeshProUGUI MakeLabel(RectTransform parent, CellKind kind) =>
            UILabel.Make(parent, "Label",
                Style.Black,
                kind == CellKind.ColumnHeader
                    ? TextAlignmentOptions.Center
                    : TextAlignmentOptions.MidlineLeft,
                TextOverflowModes.Ellipsis, true, kind != CellKind.Value);

        private static void Place(TextMeshProUGUI label, float x, float y, float width)
        {
            RectTransform rt = label.rectTransform;
            rt.anchoredPosition = new Vector2(x + Style.SmallPadding, -y);
            rt.sizeDelta = new Vector2(width - Style.SmallPadding * 2f, RowHeight);
        }

        private static void Strip(RectTransform parent, Color color, float x, float y, float width, float height)
        {
            GameObject obj = new GameObject("Strip", typeof(RectTransform));
            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);

            Image image = obj.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            PanelUI.ApplyFront(image);
        }
    }
}
