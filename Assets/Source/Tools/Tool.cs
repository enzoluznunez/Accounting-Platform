using UnityEngine;

public abstract class Tool : MonoBehaviour
{
    public ManageTools toolManager;
    public ManageSheets sheetManager;
    public ReadSheets readSheets;
    public ToolPanelUI toolPanelUI;

    private bool _active;

    protected bool Active => _active;

    protected abstract ToolType Kind { get; }

    protected void Report(string what) => StateChannel.Record(Kind.ToString(), what);

    protected virtual bool UsesSheetEvents => false;

    protected virtual void OnSheetHover(ReadSheets.Reading reading) { }

    protected virtual void OnSheetSelect(ReadSheets.Reading reading) { }

    protected virtual void OnSheetRelease(ReadSheets.Reading reading) { }

    protected virtual void OnSheetCommit(ReadSheets.Reading reading) { }

    protected virtual void OnSheetCleared() { }

    protected void ClearTint()
    {
        if (sheetManager != null) sheetManager.ClearHoverTint();
    }

    // Lights the line under the finger: one row, or on a grouped column axis the
    // whole metric the column belongs to.
    protected void TintLine(ReadSheets.Reading reading, bool columns, float swell = Style.PreviewSwell)
    {
        int lo = reading.visRow, hi = reading.visRow;
        if (columns) sheetManager.ColumnStripSpan(reading.sheet, reading.visCol, out lo, out hi);
        sheetManager.SetLineTint(reading.sheet, columns ? 1 : 2, lo, hi, swell);
    }

    private bool _listening;

    private void ListenSheets(bool on)
    {
        if (readSheets == null || on == _listening) return;
        _listening = on;
        if (on)
        {
            readSheets.OnHover += OnSheetHover;
            readSheets.OnSelect += OnSheetSelect;
            readSheets.OnRelease += OnSheetRelease;
            readSheets.OnCommit += OnSheetCommit;
            readSheets.OnCleared += OnSheetCleared;
        }
        else
        {
            readSheets.OnHover -= OnSheetHover;
            readSheets.OnSelect -= OnSheetSelect;
            readSheets.OnRelease -= OnSheetRelease;
            readSheets.OnCommit -= OnSheetCommit;
            readSheets.OnCleared -= OnSheetCleared;
        }
    }

    protected virtual void OnToolStart() { }

    protected virtual void OnToolDestroy() { }

    protected virtual void BuildPanelContent() { }

    protected virtual void OnActiveChanged(bool active) { }

    protected virtual void ClearToolState() { }

    protected virtual void OnResetTool() { }

    protected virtual void Start()
    {
        if (toolManager == null) toolManager = FindAnyObjectByType<ManageTools>();
        if (sheetManager == null) sheetManager = FindAnyObjectByType<ManageSheets>();
        if (readSheets == null && sheetManager != null) readSheets = sheetManager.GetComponent<ReadSheets>();
        if (readSheets == null) readSheets = FindAnyObjectByType<ReadSheets>();
        if (toolPanelUI == null) toolPanelUI = FindAnyObjectByType<ToolPanelUI>();

        OnToolStart();
        BuildPanelContent();

        if (toolManager != null)
        {
            toolManager.OnToolChanged += HandleToolChanged;
            toolManager.OnToolReset += HandleToolReset;
        }

        SetActive(toolManager != null && toolManager.SelectedTool == Kind);
    }

    protected virtual void OnDestroy()
    {
        if (toolManager != null)
        {
            toolManager.OnToolChanged -= HandleToolChanged;
            toolManager.OnToolReset -= HandleToolReset;
        }
        ListenSheets(false);
        OnToolDestroy();
    }

    private void HandleToolChanged(ToolType selected) => SetActive(selected == Kind);

    private void HandleToolReset(ToolType tool)
    {
        if (tool != Kind) return;
        ClearToolState();
        OnResetTool();
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active) ClearToolState();
        if (UsesSheetEvents) ListenSheets(active);
        OnActiveChanged(active);
    }
}
