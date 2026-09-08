using UnityEngine;

public class SliceTool : Tool
{
    public float sliceGapCells = 0.5f;

    private struct CutInfo
    {
        public bool valid;
        public bool columns;
        public CreateSheet sheet;
        public int boundary;
        public int tintMin;
        public int tintMax;
    }

    private readonly AxisIntent _intent = new AxisIntent { deadband = 0.3f };

    protected override ToolType Kind => ToolType.Slice;

    protected override bool UsesSheetEvents => true;

    protected override void OnResetTool()
    {
        if (sheetManager != null) sheetManager.ResetSlices();
        ClearTint();
    }

    protected override void OnActiveChanged(bool active)
    {
        if (!active) ClearTint();
        _intent.Reset();
    }

    private bool ResolveAxis(ReadSheets.Reading reading)
    {
        CreateSheet sheet = reading.sheet;
        Vector3 local = sheet.transform.InverseTransformPoint(reading.point);

        if (reading.tip != Vector3.zero
            && AxisIntent.FaceScores(sheet, reading.normal, out float forColumns, out float forRows))
            _intent.Feed(forColumns, forRows);
        else if (AxisIntent.SeamScores(sheet, local, out forColumns, out forRows))
            _intent.Feed(forColumns, forRows);

        return _intent.Decided;
    }

    private CutInfo ComputeCut(ReadSheets.Reading reading, bool columns)
    {
        CutInfo info = default;
        if (!Active || sheetManager == null) return info;
        if (!reading.valid || reading.sheet == null) return info;

        CreateSheet sheet = reading.sheet;
        Vector3 local = sheet.transform.InverseTransformPoint(reading.point);

        // Cuts are chosen in block space, so a grouped axis can only ever be cut
        // between metrics and never through a pair.
        int min = sheet.BlockMin(columns);
        int max = sheet.BlockMax(columns);
        if (max - min < 1) return info;

        float fractional = sheet.BlockFraction(columns, columns ? local.x : local.z);
        int block = Mathf.Clamp(Mathf.RoundToInt(fractional - 0.5f), min, max - 1);
        int size = sheet.GroupSizeOn(columns);

        info.valid = true;
        info.columns = columns;
        info.sheet = sheet;
        info.boundary = block * size + size - 1;
        info.tintMin = block * size;
        info.tintMax = (block + 2) * size - 1;
        return info;
    }

    private bool Preview(ReadSheets.Reading reading, float swell, out CutInfo cut)
    {
        cut = default;
        if (!Active || sheetManager == null || !reading.valid || reading.sheet == null) return false;
        if (!ResolveAxis(reading)) return false;

        cut = ComputeCut(reading, _intent.Columns);
        if (!cut.valid) return false;

        sheetManager.SetLineTint(cut.sheet, cut.columns ? 1 : 2, cut.tintMin, cut.tintMax, swell);
        return true;
    }

    protected override void OnSheetHover(ReadSheets.Reading reading)
    {
        if (!Preview(reading, Style.PreviewSwell, out _)) ClearTint();
    }

    protected override void OnSheetSelect(ReadSheets.Reading reading)
    {
        if (Preview(reading, Style.PreviewSwell + Style.EngageSwell, out _)) _intent.Latch();
        else ClearTint();
    }

    protected override void OnSheetRelease(ReadSheets.Reading reading) => _intent.Release();

    protected override void OnSheetCleared()
    {
        ClearTint();
        _intent.Reset();
    }

    protected override void OnSheetCommit(ReadSheets.Reading reading)
    {
        if (_intent.Decided)
        {
            CutInfo cut = ComputeCut(reading, _intent.Columns);
            if (cut.valid) CutAt(cut.columns, cut.sheet, cut.boundary, out _);
        }
        ClearTint();
        _intent.Release();
    }

    public bool CutAt(bool columns, CreateSheet sheet, int boundary, out SliceRecord record)
    {
        record = default;
        if (!Active || sheetManager == null || sheet == null) return false;

        float gap = sliceGapCells * sheetManager.CellSize;
        SliceAxis axis = columns ? SliceAxis.Column : SliceAxis.Row;
        if (!sheetManager.Slice(sheet, axis, boundary, gap, out record, StateChannel.InAgentCall)) return false;

        ManageDatasets.ActiveEdits.PushSlice(record);

        DataSource data = Scene.Data;
        string line = DataSource.GroupLabelAt(data, columns,
            data != null ? data.GroupOf(columns, record.boundary) : record.boundary);

        string layout = PiecesFact.Update();
        Report($"sliced piece {record.aId} after {line}, making pieces {record.aId} and {record.bId}; " +
               $"the pieces now run {layout} in order along the sheet");
        return true;
    }
}
