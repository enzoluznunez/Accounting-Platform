using UnityEngine;

public class ProfileTool : Tool
{
    public float liftAboveMaximumHeight = 0f;

    [Tooltip("How far a pressed finger must travel, in cells, before the sweep direction chooses the axis.")]
    public float travelCells = 0.6f;

    private readonly AxisIntent _intent = new AxisIntent { deadband = 0.25f };
    private bool _pressed;
    private Vector3 _pressLocal;

    protected override ToolType Kind => ToolType.Profile;

    protected override bool UsesSheetEvents => true;

    protected override void OnResetTool() => StatsTooltip.Hide();

    protected override void OnActiveChanged(bool active)
    {
        _pressed = false;
        _intent.Reset();
        if (!active)
        {
            ClearTint();
            StatsTooltip.Hide();
        }
    }

    private static bool Usable(ReadSheets.Reading reading) =>
        reading.valid && reading.cube != null && reading.sheet != null;

    private void Tint(ReadSheets.Reading reading, float swell) => TintLine(reading, _intent.Columns, swell);

    private void FeedReach(ReadSheets.Reading reading)
    {
        if (reading.tip == Vector3.zero) return;
        if (AxisIntent.ReachScores(reading.sheet, reading.wrist, reading.tip, out float forColumns, out float forRows))
            _intent.Feed(forColumns, forRows);
    }

    private void FeedSweep(ReadSheets.Reading reading)
    {
        if (reading.tip == Vector3.zero) return;

        Vector3 delta = reading.sheet.transform.InverseTransformPoint(reading.tip) - _pressLocal;
        float travel = travelCells * reading.sheet.CellSize;
        if (Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.z)) < travel) return;

        AxisIntent.SweepScores(delta, out float forColumns, out float forRows);
        _intent.Feed(forColumns, forRows);
        _intent.Latch();
    }

    protected override void OnSheetHover(ReadSheets.Reading reading)
    {
        if (!Active || sheetManager == null || !Usable(reading))
        {
            ClearTint();
            return;
        }

        if (_pressed) FeedSweep(reading);
        else FeedReach(reading);

        if (!_intent.Decided)
        {
            ClearTint();
            return;
        }

        Tint(reading, _pressed ? Style.PreviewSwell + Style.EngageSwell : Style.PreviewSwell);
    }

    protected override void OnSheetSelect(ReadSheets.Reading reading)
    {
        if (!Active || sheetManager == null || !Usable(reading)) return;

        FeedReach(reading);
        _pressed = true;
        _pressLocal = reading.sheet.transform.InverseTransformPoint(
            reading.tip == Vector3.zero ? reading.point : reading.tip);

        if (_intent.Decided) Tint(reading, Style.PreviewSwell + Style.EngageSwell);
    }

    protected override void OnSheetRelease(ReadSheets.Reading reading)
    {
        ClearTint();
        _pressed = false;
        _intent.Release();
    }

    protected override void OnSheetCleared()
    {
        ClearTint();
        _pressed = false;
        _intent.Reset();
    }

    protected override void OnSheetCommit(ReadSheets.Reading reading)
    {
        ClearTint();
        if (!Active || sheetManager == null || !Usable(reading) || !_intent.Decided) return;

        bool columns = _intent.Columns;
        if (!Project(columns, reading.cube.dataRow, reading.cube.dataCol, reading.visRow, reading.visCol)) return;

        ShowStats(columns, reading);
    }

    private void ShowStats(bool columns, ReadSheets.Reading reading)
    {
        if (!StatsTooltip.TryResolve(sheetManager, reading,
                out Tooltip tooltip, out DataSource data, out CreateSheet piece)) return;

        int line = columns ? reading.visCol : reading.visRow;
        string name = columns && data.IsGrouped(true)
            ? DataSource.GroupLabelAt(data, true, data.GroupOf(true, line))
            : data.TitleAt(columns, line);
        string title = string.IsNullOrEmpty(name) ? $"{(columns ? "Column" : "Row")} {line + 1}" : name;

        // Across a grouped axis the cells hold different metrics, so a row's
        // summary is taken over the one metric that was touched rather than over
        // every column, which would average dollars with share counts.
        int colLo = piece.colMin;
        int colHi = piece.colMax;
        if (!columns && data.IsGrouped(true))
        {
            int group = data.GroupOf(true, reading.visCol);
            data.GroupSpan(true, group, out colLo, out colHi);
            colLo = Mathf.Max(colLo, piece.colMin);
            colHi = Mathf.Min(colHi, piece.colMax);
            title += " · " + DataSource.GroupLabelAt(data, true, group);
        }

        // A column strip raises the whole metric, so its summary covers every year.
        int stripLo = line, stripHi = line;
        if (columns) sheetManager.ColumnStripSpan(piece, line, out stripLo, out stripHi);

        Tooltip.SelectionStats selection = new Tooltip.SelectionStats
        {
            title = title,
            stats = columns
                ? SheetStats.Over(data, piece.rowMin, piece.rowMax, stripLo, stripHi)
                : SheetStats.Over(data, line, line, colLo, colHi)
        };

        ManageSheets sheets = sheetManager;
        CreateSheet target = piece;
        float lift = liftAboveMaximumHeight;
        Vector3 fallback = reading.point;

        tooltip.ShowStats(
            () => sheets != null && sheets.TryStripTopPoint(target, columns, line, lift, out Vector3 raised)
                ? raised
                : fallback,
            selection);
    }

    public bool ShowProfile(bool columns, int visRow, int visCol)
    {
        if (!Active || sheetManager == null) return false;

        CreateCube cube = sheetManager.CubeAt(visRow, visCol);
        if (cube == null) return false;

        return Project(columns, cube.dataRow, cube.dataCol, visRow, visCol);
    }

    private bool Project(bool columns, int dataRow, int dataCol, int visRow, int visCol)
    {
        ProjectionRecord rec = new ProjectionRecord
        {
            isColumn = columns,
            dataRow = dataRow,
            dataCol = dataCol,
            lift = liftAboveMaximumHeight
        };

        if (!sheetManager.PushProjection(rec, EditKind.Profile)) return false;

        DataSource data = Scene.Data;
        int line = columns ? visCol : visRow;
        string label = columns && data != null && data.IsGrouped(true)
            ? DataSource.GroupLabelAt(data, true, data.GroupOf(true, line))
            : DataSource.LabelAt(data, columns, line);
        Report($"projected {label}");
        return true;
    }
}
