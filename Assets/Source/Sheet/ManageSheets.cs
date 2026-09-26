using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;

public class ManageSheets : MonoBehaviour
{
    public const int FirstSheetId = 1;

    public DataSource dataSource;
    public GameObject sheetPrefab;
    public Material cubeMaterial;

    [Tooltip("Width of one bar's footprint, in metres.")]
    [Range(0.005f, 0.15f)] public float cubeSide = 0.025f;
    [Tooltip("Clear space between neighbouring bars, in metres.")]
    [Range(0f, 0.15f)] public float cubeGap = 0.02f;
    [Tooltip("Extra space between column groups, in metres. Only used when the dataset pairs its columns.")]
    [Range(0f, 0.2f)] public float groupGap = 0.03f;
    [Tooltip("Height of the full value range, in metres.")]
    [Range(0.02f, 1f)] public float maximumHeight = 0.25f;

    public float minimumZOffsetFromCamera = 0f;

    [Header("Row and column titles")]
    public bool showLabels = true;
    [Tooltip("Distance from the sheet edge out to the title, in metres.")]
    [Range(0f, 0.5f)] public float labelGap = 0.037f;

    [Header("System motion")]
    [Tooltip("Speed of the fastest-moving visible point in any system motion, metres per second.")]
    public float systemMotionSpeed = 0.5f;
    [Tooltip("Shortest system motion, seconds.")]
    public float minMotionSeconds = 0.1f;
    [Tooltip("Longest system motion, seconds; a large motion moves faster than the shared speed instead of running longer.")]
    public float maxMotionSeconds = 1.5f;

    public event Action OnSheetsChanged;
    public event Action<CreateSheet, Vector3, Quaternion, Vector3> OnSheetMoveCommitted;

    private struct Projection
    {
        public ProjectionRecord rec;
        public CreateSheet view;
        public CreateSheet source;
        public int visRow;
        public int visCol;
    }

    private readonly List<CreateSheet> _sheets = new List<CreateSheet>();
    private Projection _projection;
    private bool _hasProjection;
    private float _projectionRise;
    private Coroutine _projectionRiseRoutine;

    private DataSource _bound;
    private Transform _root;

    private int _rowCount;
    private int _colCount;
    private float _cellSize;
    private float _groupGap;
    private float _baseY;

    private bool _placementPending = true;
    private bool _anchored;
    private Vector3 _anchorCenter;
    private Quaternion _anchorYaw = Quaternion.identity;

    private bool _sheetsGrabbable;

    public IReadOnlyList<CreateSheet> Sheets => _sheets;

    // The sheet. There is one: nothing cuts it apart any more, so callers that
    // used to pick a piece ask for this instead.
    public CreateSheet Sheet => _sheets.Count > 0 ? _sheets[0] : null;
    public int RowCount => _rowCount;
    public int ColCount => _colCount;
    public float CellSize => _cellSize;
    public float Height => maximumHeight;
    public float BaseY => _baseY;
    public bool IsBuilt => _sheets.Count > 0;

    private bool _recenterHooked;

    private void OnEnable()
    {
        Bind(dataSource != null ? dataSource : ManageDatasets.ActiveSource);
        OVRManager.HMDMounted += RecenterToUser;
    }

    private void OnDisable()
    {
        Unbind();
        OVRManager.HMDMounted -= RecenterToUser;
        if (_recenterHooked && OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose -= RecenterToUser;
            _recenterHooked = false;
        }
    }

    private void Update()
    {
        if (!_recenterHooked && OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose += RecenterToUser;
            _recenterHooked = true;
        }

        if (_placementPending && ApplyPlacement()) _placementPending = false;

        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            if (s == null) continue;

            bool held = s.IsGrabbed && !ScaleArmed;

            if (!held) s.SetGrabLook(false);
            if (s.PollGrabRelease(out Vector3 pos, out Quaternion rot, out Vector3 scale))
                NotifyMoveCommitted(s, pos, rot, scale);
            if (held) s.SetGrabLook(true);
        }
    }

    private static bool ScaleArmed =>
        Scene.Tools != null && Scene.Tools.SelectedTool == ToolType.Scale;

    private void LateUpdate() => PlaceCurrentProjection();

    public void SetDataSource(DataSource source)
    {
        Unbind();
        dataSource = source;
        ClearProjection();
        ClearSheets();
        Bind(source);
    }

    private void Bind(DataSource source)
    {
        if (source == null || _bound == source) return;
        _bound = source;
        dataSource = source;
        _bound.OnDataLoaded += RebuildAll;
        _bound.OnLayoutInvalidated += RebuildAll;
        if (_bound.IsLoaded) RebuildAll();
    }

    private void Unbind()
    {
        if (_bound == null) return;
        _bound.OnDataLoaded -= RebuildAll;
        _bound.OnLayoutInvalidated -= RebuildAll;
        _bound = null;
    }

    public void RebuildAll()
    {
        DataSource data = _bound;
        if (data == null || !data.IsLoaded) { ClearProjection(); ClearSheets(); return; }

        _rowCount = data.RowOrder.Count;
        _colCount = data.ColumnOrder.Count;
        if (_rowCount == 0 || _colCount == 0) { ClearProjection(); ClearSheets(); return; }

        _cellSize = cubeSide + cubeGap;
        _groupGap = data.ColumnGroupSize > 1 ? groupGap : 0f;
        _baseY = data.ZeroFraction * maximumHeight;

        EnsureRoot();

        int maxRow = _rowCount - 1;
        int maxCol = _colCount - 1;

        RefitSpan(data);

        bool skipReflow = _skipReflowOnce;
        _skipReflowOnce = false;
        List<Dictionary<int, float>[]> lineSnapshot =
            !skipReflow && _sheets.Count > 0 ? SnapshotLineCoords() : null;

        if (_sheets.Count > 0 && !CoversWholeField(maxRow, maxCol))
        {
            lineSnapshot = null;
            ClearSheets();
            ClearProjection();
            ManageDatasets.ActiveEdits.Clear();
            Notices.EditsDropped(this, "The dataset changed shape, so its edits were cleared.");
        }

        if (_sheets.Count == 0)
        {
            lineSnapshot = null;
            CreateSheet root = NewSheet();
            root.Build(data, cubeMaterial, 0, maxRow, 0, maxCol,
                _cellSize, _groupGap, maximumHeight, _baseY, cubeSide, LabelStyle());
            root.PlayGrow(GrowDuration());
            _sheets.Add(root);
        }
        else
        {
            for (int i = 0; i < _sheets.Count; i++) RebuildSheet(_sheets[i]);
        }

        if (lineSnapshot != null)
        {
            if (AgentInstant) StopReflow();
            else StartReflow(lineSnapshot);
        }

        _builtColumns.Clear();
        _builtColumns.AddRange(data.ColumnOrder);
        _builtRows.Clear();
        _builtRows.AddRange(data.RowOrder);

        _placementPending = !ApplyPlacement();
        RebuildProjection();
        OnSheetsChanged?.Invoke();
    }

    // The columns and rows that were on the sheet when it was last built, by data index.
    private readonly List<int> _builtColumns = new List<int>();
    private readonly List<int> _builtRows = new List<int>();

    // A filter changes which columns or companies exist, so the sheet's span is
    // stated in positions that have just moved. The sheet holds every line there
    // is, so refitting it is restating that: from the first to the last.
    private void RefitSpan(DataSource data)
    {
        if (_sheets.Count == 0) return;

        IReadOnlyList<int> cols = data.ColumnOrder;
        IReadOnlyList<int> rows = data.RowOrder;
        bool refitCols = _builtColumns.Count > 0 && !SameLines(_builtColumns, cols);
        bool refitRows = _builtRows.Count > 0 && !SameLines(_builtRows, rows);
        if (!refitCols && !refitRows) return;

        for (int i = 0; i < _sheets.Count; i++)
        {
            if (_sheets[i] == null) continue;
            if (refitCols) { _sheets[i].colMin = 0; _sheets[i].colMax = cols.Count - 1; }
            if (refitRows) { _sheets[i].rowMin = 0; _sheets[i].rowMax = rows.Count - 1; }
        }
    }

    // Set equality, not sequence equality: a reorder leaves the span untouched and
    // is animated by the reflow instead.
    private static bool SameLines(List<int> built, IReadOnlyList<int> now)
    {
        if (built.Count != now.Count) return false;
        for (int i = 0; i < now.Count; i++)
            if (!built.Contains(now[i])) return false;
        return true;
    }

    private bool CoversWholeField(int maxRow, int maxCol)
    {
        int r = -1, c = -1;
        for (int i = 0; i < _sheets.Count; i++)
        {
            if (_sheets[i].rowMax > r) r = _sheets[i].rowMax;
            if (_sheets[i].colMax > c) c = _sheets[i].colMax;
        }
        return r == maxRow && c == maxCol;
    }

    private CreateSheet CreateDetachedPiece(string label)
    {
        EnsureRoot();

        GameObject go = sheetPrefab != null ? Instantiate(sheetPrefab, _root) : new GameObject(label);
        if (go.transform.parent != _root) go.transform.SetParent(_root, false);
        go.name = label;

        CreateSheet s = go.GetComponent<CreateSheet>();
        if (s == null) s = go.AddComponent<CreateSheet>();
        s.MarkDetached();
        s.sheetId = -1;
        s.SetGrabbable(false);
        s.SetPickable(false);
        return s;
    }

    public SheetLabelStyle LabelStyle() => new SheetLabelStyle
    {
        show = showLabels,
        color = Style.White,
        gap = labelGap
    };

    // The industries a piece holds, in row order. What the assistant needs to
    // answer "what is on this piece", and what a legend would read.
    public List<string> CategoriesIn(CreateSheet piece)
    {
        var found = new List<string>();
        if (piece == null || _bound == null || !_bound.HasRowCategories) return found;

        IReadOnlyList<int> rowOrder = _bound.RowOrder;
        var rows = new List<int>();
        for (int vr = piece.rowMin; vr <= piece.rowMax; vr++)
            if (vr >= 0 && vr < rowOrder.Count) rows.Add(rowOrder[vr]);

        return _bound.CategoriesInOrder(rows);
    }

    private void RebuildSheet(CreateSheet sheet)
    {
        sheet.Build(_bound, cubeMaterial, sheet.rowMin, sheet.rowMax, sheet.colMin, sheet.colMax,
            _cellSize, _groupGap, maximumHeight, _baseY, cubeSide, LabelStyle());
    }

    public CreateSheet SheetAt(int visRow, int visCol)
    {
        for (int i = 0; i < _sheets.Count; i++)
            if (_sheets[i].Contains(visRow, visCol)) return _sheets[i];
        return null;
    }

    public CreateCube CubeAt(int visRow, int visCol)
    {
        CreateSheet sheet = SheetAt(visRow, visCol);
        return sheet != null ? sheet.CubeAt(visRow, visCol) : null;
    }

    public enum UndoResult { Applied, Stale, Unreachable }

    public UndoResult Undo(Edit e)
    {
        if (e == null) return UndoResult.Stale;

        switch (e.kind)
        {
            case EditKind.Move:
            case EditKind.Rotate:
            case EditKind.Scale:
                Vector3 preScale = e.move.preScale.sqrMagnitude > 1e-6f ? e.move.preScale : Vector3.one;
                return RestoreSheetPose(e.move.sheetId, e.move.prePos, e.move.preRot, preScale)
                    ? UndoResult.Applied : UndoResult.Stale;

            case EditKind.Profile:
                return UndoResult.Applied;

            case EditKind.Filter:
                if (_bound == null) return UndoResult.Unreachable;
                if (e.filterIsRow) _bound.SetHiddenRows(e.filterPreHidden, out _);
                else _bound.SetHiddenGroups(e.filterPreHidden, out _);
                return UndoResult.Applied;

            case EditKind.Sort:
                if (_bound == null) return UndoResult.Unreachable;
                SuppressNextReflow();
                bool reordered = e.reorderIsColumn
                    ? _bound.SetColumnOrder(e.reorderPreOrder, e.reorderPreMode)
                    : _bound.SetRowOrder(e.reorderPreOrder, e.reorderPreMode);
                return reordered ? UndoResult.Applied : UndoResult.Stale;
        }

        return UndoResult.Stale;
    }

    public void ReplayEdits(IReadOnlyList<Edit> edits)
    {
        if (edits == null || _bound == null) return;

        for (int i = 0; i < edits.Count; i++)
        {
            Edit e = edits[i];
            switch (e.kind)
            {
                case EditKind.Move:
                case EditKind.Rotate:
                case EditKind.Scale:
                    Vector3 scale = e.move.postScale.sqrMagnitude > 1e-6f ? e.move.postScale : Vector3.one;
                    RestoreSheetPose(e.move.sheetId, e.move.postPos, e.move.postRot, scale);
                    break;

                case EditKind.Filter:
                    if (e.filterIsRow) _bound.SetHiddenRows(e.filterPostHidden, out _);
                    else _bound.SetHiddenGroups(e.filterPostHidden, out _);
                    break;

            }
        }
    }

    public void CubesInLine(bool columns, int visLine, List<CreateCube> into)
    {
        if (into == null) return;
        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            bool contains = columns
                ? visLine >= s.colMin && visLine <= s.colMax
                : visLine >= s.rowMin && visLine <= s.rowMax;
            if (!contains) continue;

            IReadOnlyList<CreateCube> cubes = s.CubesInLine(columns, visLine);
            for (int j = 0; j < cubes.Count; j++) into.Add(cubes[j]);
        }
    }

    public bool PushProjection(ProjectionRecord rec, EditKind kind)
    {
        if (_bound == null) return false;
        if (_hasProjection && SameRecord(_projection.rec, rec)) return false;

        ManageDatasets.ActiveEdits.PushProjection(rec, kind);
        SyncProjectionToStack(StateChannel.InAgentCall);
        return true;
    }

    public void SyncProjectionToStack(bool animateNew = false)
    {
        EditList edits = ManageDatasets.ActiveEdits;

        for (int i = edits.Count - 1; i >= 0; i--)
        {
            Edit e = edits[i];
            if (e.kind != EditKind.Profile) continue;

            if (_hasProjection && SameRecord(_projection.rec, e.projection)) return;

            ClearProjection();
            ShowProjection(e.projection, animateNew);
            return;
        }

        ClearProjection();
    }

    public void CollectProjections(List<ProjectionRecord> into)
    {
        into.Clear();
        if (_hasProjection) into.Add(_projection.rec);
    }

    public bool TryResolveProjection(ProjectionRecord rec, out int visRow, out int visCol)
    {
        visRow = visCol = -1;
        if (_bound == null) return false;

        visRow = _bound.VisIndexOf(false, rec.dataRow);
        visCol = _bound.VisIndexOf(true, rec.dataCol);
        return visRow >= 0 && visCol >= 0;
    }

    // A column strip on a grouped axis raises its whole metric, so any year of
    // that metric names the same strip.
    private bool SameRecord(ProjectionRecord a, ProjectionRecord b)
    {
        if (a.isColumn != b.isColumn) return false;
        if (!a.isColumn) return a.dataRow == b.dataRow;
        return _bound != null && _bound.IsGrouped(true)
            ? _bound.DataGroupOf(a.dataCol) == _bound.DataGroupOf(b.dataCol)
            : a.dataCol == b.dataCol;
    }

    private bool ShowProjection(ProjectionRecord rec, bool animate = false)
    {
        if (_bound == null) return false;

        CreateSheet view = CreateDetachedPiece("Projection_Strip");
        if (view == null) return false;

        Projection p = new Projection { rec = rec, view = view };
        if (!BuildProjection(ref p))
        {
            Destroy(view.gameObject);
            return false;
        }

        _projection = p;
        _hasProjection = true;
        if (animate) StartProjectionRise(rec.lift);
        return true;
    }

    private void StartProjectionRise(float lift)
    {
        if (_projectionRiseRoutine != null) { StopCoroutine(_projectionRiseRoutine); _projectionRiseRoutine = null; }
        _projectionRise = 0f;
        if (!isActiveAndEnabled || ActiveMotionSpeed <= 0f || AgentInstant) return;

        _projectionRise = maximumHeight + lift;
        float sourceScale = _projection.source != null
            ? Mathf.Abs(_projection.source.transform.lossyScale.y)
            : 1f;
        PlaceCurrentProjection();
        _projectionRiseRoutine = StartCoroutine(ProjectionRiseRoutine(_projectionRise, _projectionRise * sourceScale));
    }

    private IEnumerator ProjectionRiseRoutine(float total, float worldMeters)
    {
        float duration = MotionDuration(worldMeters);
        float t = 0f;
        while (t < duration && _hasProjection)
        {
            yield return null;
            t += Time.deltaTime;
            _projectionRise = Mathf.Lerp(total, 0f, Mathf.Clamp01(t / duration));
        }
        _projectionRise = 0f;
        _projectionRiseRoutine = null;
    }

    private void ClearProjection()
    {
        if (!_hasProjection) return;

        if (_projectionRiseRoutine != null) { StopCoroutine(_projectionRiseRoutine); _projectionRiseRoutine = null; }
        _projectionRise = 0f;
        if (_projection.view != null) Destroy(_projection.view.gameObject);
        _projection = default;
        _hasProjection = false;
    }

    private void RebuildProjection()
    {
        if (!_hasProjection) return;

        Projection p = _projection;
        if (p.view != null && BuildProjection(ref p)) { _projection = p; return; }

        ClearProjection();
    }

    private void PlaceCurrentProjection()
    {
        if (!_hasProjection) return;
        if (PlaceProjection(_projection)) return;

        Projection p = _projection;
        if (p.view != null && BuildProjection(ref p)) { _projection = p; return; }

        ClearProjection();
    }

    public bool TryStripPoint(CreateSheet source, bool column, int visLine, float lift, out Vector3 world)
    {
        world = Vector3.zero;
        if (source == null || !source.IsBuilt) return false;

        Vector3 originLocal = column
            ? new Vector3(ColumnStripOffset(source, visLine), 0f, 0f)
            : new Vector3(0f, 0f, source.LineOffset(false, visLine));
        world = source.transform.TransformPoint(originLocal + Vector3.up * (maximumHeight + lift));
        return true;
    }

    private float BarTopLocal(int dataRow, int dataCol)
    {
        if (_bound == null || !_bound.HasValue(dataRow, dataCol)) return _baseY;
        return Mathf.Max(_bound.GetHeightFraction(dataRow, dataCol) * maximumHeight, _baseY);
    }

    // The columns a column strip raises: the whole metric on a grouped axis,
    // clipped to the piece, or just the one column otherwise.
    public void ColumnStripSpan(CreateSheet source, int visCol, out int lo, out int hi)
    {
        lo = hi = visCol;
        if (_bound == null || !_bound.IsGrouped(true)) return;

        _bound.GroupSpan(true, _bound.GroupOf(true, visCol), out lo, out hi);
        if (source == null) return;
        lo = Mathf.Max(lo, source.colMin);
        hi = Mathf.Min(hi, source.colMax);
    }

    // Where a column strip's centre sits across the source piece.
    private float ColumnStripOffset(CreateSheet source, int visCol)
    {
        ColumnStripSpan(source, visCol, out int lo, out int hi);
        return (source.LineOffset(true, lo) + source.LineOffset(true, hi)) * 0.5f;
    }

    public bool TryStripTopPoint(CreateSheet source, bool column, int visLine, float lift, out Vector3 world)
    {
        if (!TryStripPoint(source, column, visLine, lift, out world)) return false;

        float top = _baseY;
        int lo = visLine, hi = visLine;
        if (column) ColumnStripSpan(source, visLine, out lo, out hi);
        for (int line = lo; line <= hi; line++)
        {
            IReadOnlyList<CreateCube> cubes = source.CubesInLine(column, line);
            for (int i = 0; i < cubes.Count; i++)
                if (cubes[i] != null) top = Mathf.Max(top, BarTopLocal(cubes[i].dataRow, cubes[i].dataCol));
        }

        world += source.transform.TransformVector(Vector3.up * top);
        return true;
    }

    private bool PlaceProjection(Projection p)
    {
        if (p.view == null || p.source == null || !p.source.IsBuilt) return false;

        if (!TryStripPoint(p.source, p.rec.isColumn, p.rec.isColumn ? p.visCol : p.visRow,
                p.rec.lift, out Vector3 world)) return false;

        if (_projectionRise > 0f)
            world -= p.source.transform.TransformVector(Vector3.up * _projectionRise);

        Transform src = p.source.transform;

        p.view.transform.localPosition = transform.InverseTransformPoint(world);
        p.view.transform.localRotation = src.localRotation;
        p.view.transform.localScale = src.localScale;
        return true;
    }

    private bool BuildProjection(ref Projection p)
    {
        if (_bound == null || p.view == null) return false;

        int visRow = _bound.VisIndexOf(false, p.rec.dataRow);
        int visCol = _bound.VisIndexOf(true, p.rec.dataCol);
        if (visRow < 0 || visCol < 0) return false;

        CreateSheet source = SheetAt(visRow, visCol);
        if (source == null) return false;

        p.source = source;
        p.visRow = visRow;
        p.visCol = visCol;

        return BuildStripProjection(ref p, source, visRow, visCol);
    }

    private bool BuildStripProjection(ref Projection p, CreateSheet source,
        int visRow, int visCol)
    {
        ProjectionRecord rec = p.rec;
        CreateSheet view = p.view;
        int rMin, rMax, cMin, cMax;

        if (rec.isColumn)
        {
            rMin = source.rowMin; rMax = source.rowMax;
            ColumnStripSpan(source, visCol, out cMin, out cMax);
        }
        else
        {
            rMin = rMax = visRow;
            cMin = source.colMin; cMax = source.colMax;
        }

        view.Build(_bound, cubeMaterial, rMin, rMax, cMin, cMax,
            _cellSize, _groupGap, maximumHeight, _baseY, cubeSide, LabelStyle());
        view.SetPickable(false);

        return PlaceProjection(p);
    }


    public void SetGrabbable(bool on)
    {
        _sheetsGrabbable = on;
        for (int i = 0; i < _sheets.Count; i++) _sheets[i].SetGrabbable(on);
    }

    private Func<CreateSheet, Oculus.Interaction.ITransformer> _twoGrabFor;

    public void SetOneGrab()
    {
        _twoGrabFor = null;
        for (int i = 0; i < _sheets.Count; i++) _sheets[i].SetOneGrab();
    }

    public void LogGrabState(string when)
    {
        Debug.Log($"[Grab] {when}: {_sheets.Count} piece(s), grabbable={_sheetsGrabbable}");
        for (int i = 0; i < _sheets.Count; i++)
            if (_sheets[i] != null) Debug.Log($"[Grab]   {_sheets[i].name} {_sheets[i].DescribeGrab()}");
    }

    public void SetTwoGrab(Func<CreateSheet, Oculus.Interaction.ITransformer> transformerFor)
    {
        if (transformerFor == null) return;

        _twoGrabFor = transformerFor;
        for (int i = 0; i < _sheets.Count; i++) ApplyTwoGrab(_sheets[i], transformerFor);
    }

    private void ReanchorGrab(CreateSheet sheet)
    {
        if (_twoGrabFor == null || sheet == null) return;
        ApplyTwoGrab(sheet, _twoGrabFor);
    }

    private static void ApplyTwoGrab(CreateSheet sheet, Func<CreateSheet, Oculus.Interaction.ITransformer> transformerFor)
    {
        if (sheet == null) return;

        try
        {
            sheet.SetTwoGrab(transformerFor(sheet));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ManageSheets] Sheet {sheet.sheetId} could not take the two-hand transformer, " +
                           $"so it stays on its previous grab behaviour: {e}");
        }
    }

    public void ResetGrabs()
    {
        _twoGrabFor = null;
        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            s.ForgetGrabLook();
            s.transform.localRotation = Quaternion.identity;
            s.transform.localScale = Vector3.one;
        }
    }

    public void NotifyMoveCommitted(CreateSheet sheet, Vector3 prePos, Quaternion preRot, Vector3 preScale)
    {
        if (sheet == null) return;
        OnSheetMoveCommitted?.Invoke(sheet, prePos, preRot, preScale);
    }

    private struct LineMove
    {
        public CreateSheet piece;
        public bool columns;
        public int line;
        public float from;
        public float to;
        public float duration;
    }

    private Coroutine _reflow;
    private List<LineMove> _reflowMoves;
    private bool _skipReflowOnce;

    private class GlideState
    {
        public Vector3 toPos;
        public Quaternion toRot;
        public Vector3 toScale;
        public Coroutine routine;
    }

    private class TransformGlide
    {
        public Coroutine routine;
        public Vector3 toPos;
        public Quaternion toRot;
    }

    private readonly Dictionary<CreateSheet, GlideState> _pieceGlides = new Dictionary<CreateSheet, GlideState>();
    private readonly Dictionary<Transform, TransformGlide> _transformGlides = new Dictionary<Transform, TransformGlide>();

    public void GetCommittedPose(CreateSheet piece, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        if (_pieceGlides.TryGetValue(piece, out GlideState g))
        {
            pos = g.toPos;
            rot = g.toRot;
            scale = g.toScale;
            return;
        }

        Transform t = piece.transform;
        pos = t.localPosition;
        rot = t.localRotation;
        scale = t.localScale;
    }

    public Vector3 CommittedPositionOf(Transform target)
    {
        return _transformGlides.TryGetValue(target, out TransformGlide g) ? g.toPos : target.position;
    }

    public void CompleteTransformMotion(Transform target)
    {
        if (target == null || !_transformGlides.TryGetValue(target, out TransformGlide g)) return;
        if (g.routine != null) StopCoroutine(g.routine);
        _transformGlides.Remove(target);
        target.SetPositionAndRotation(g.toPos, g.toRot);
    }

    public void SuppressNextReflow() => _skipReflowOnce = true;

    private float _agentMotionSpeed = -1f;
    private bool _agentMotionInstant;

    public void SetAgentMotion(float speed, bool instant)
    {
        _agentMotionSpeed = speed;
        _agentMotionInstant = instant;
    }

    public bool ForceAgentMotion { get; set; }

    private bool AsAgent => StateChannel.InAgentCall || ForceAgentMotion;

    public bool AgentMotionAnimates => AsAgent && !_agentMotionInstant;

    private float ActiveMotionSpeed =>
        AsAgent && _agentMotionSpeed > 0f ? _agentMotionSpeed : systemMotionSpeed;

    private bool AgentInstant => AsAgent && _agentMotionInstant;

    private float GrowDuration()
    {
        if (!isActiveAndEnabled || ActiveMotionSpeed <= 0f || AgentInstant) return 0f;
        return MotionDuration(maximumHeight * Mathf.Abs(transform.lossyScale.y));
    }

    private float MotionDuration(float meters) =>
        Mathf.Clamp(meters / Mathf.Max(ActiveMotionSpeed, 1e-3f), minMotionSeconds, maxMotionSeconds);

    private float PieceRadius(CreateSheet piece)
    {
        float halfCols = piece.ColCount * _cellSize * 0.5f;
        float halfRows = piece.RowCount * _cellSize * 0.5f;
        return Mathf.Sqrt(halfCols * halfCols + halfRows * halfRows);
    }

    private static float BoundsRadius(Transform target)
    {
        if (target is RectTransform rect)
        {
            Vector3 scale = rect.lossyScale;
            float w = rect.rect.width * Mathf.Abs(scale.x);
            float h = rect.rect.height * Mathf.Abs(scale.y);
            return 0.5f * Mathf.Sqrt(w * w + h * h);
        }

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return 0.3f;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b.extents.magnitude;
    }

    private List<Dictionary<int, float>[]> SnapshotLineCoords()
    {
        var snapshot = new List<Dictionary<int, float>[]>();
        for (int i = 0; i < _sheets.Count; i++)
        {
            var columns = new Dictionary<int, float>();
            var rows = new Dictionary<int, float>();
            if (_sheets[i] != null) _sheets[i].CollectLineCoords(columns, rows);
            snapshot.Add(new[] { columns, rows });
        }
        return snapshot;
    }

    private void StopReflow()
    {
        if (_reflow != null) { StopCoroutine(_reflow); _reflow = null; }
    }

    public IEnumerator WaitForReflow()
    {
        while (_reflow != null) yield return null;
    }

    private void StartReflow(List<Dictionary<int, float>[]> snapshot)
    {
        StopReflow();
        if (_bound == null) return;

        var moves = new List<LineMove>();
        int count = Mathf.Min(snapshot.Count, _sheets.Count);
        for (int i = 0; i < count; i++)
        {
            CreateSheet s = _sheets[i];
            if (s == null) continue;
            CollectMoves(s, true, _bound.ColumnOrder, snapshot[i][0], moves);
            CollectMoves(s, false, _bound.RowOrder, snapshot[i][1], moves);
        }
        if (moves.Count == 0) return;

        for (int i = 0; i < moves.Count; i++)
            moves[i].piece.LayoutLine(moves[i].columns, moves[i].line, moves[i].from);
        _reflowMoves = moves;
        _reflow = StartCoroutine(ReflowRoutine(moves));
    }

    private void CollectMoves(CreateSheet s, bool columns, IReadOnlyList<int> order,
        Dictionary<int, float> old, List<LineMove> moves)
    {
        int min = columns ? s.colMin : s.rowMin;
        int max = columns ? s.colMax : s.rowMax;
        for (int v = min; v <= max; v++)
        {
            if (v < 0 || v >= order.Count) continue;
            if (!old.TryGetValue(order[v], out float from)) continue;

            float to = s.LineCoord(columns, v);
            if (Mathf.Abs(from - to) < 1e-4f) continue;

            Vector3 lossy = s.transform.lossyScale;
            float axisScale = Mathf.Abs(columns ? lossy.x : lossy.z);
            moves.Add(new LineMove
            {
                piece = s,
                columns = columns,
                line = v,
                from = from,
                to = to,
                duration = MotionDuration(Mathf.Abs(from - to) * axisScale)
            });
        }
    }

    private IEnumerator ReflowRoutine(List<LineMove> moves)
    {
        float t = 0f;
        bool moving = true;
        while (moving)
        {
            yield return null;
            t += Time.deltaTime;
            moving = false;
            for (int i = 0; i < moves.Count; i++)
            {
                LineMove m = moves[i];
                if (m.piece == null) continue;
                float k = Mathf.Clamp01(t / m.duration);
                m.piece.LayoutLine(m.columns, m.line, Mathf.Lerp(m.from, m.to, k));
                if (k < 1f) moving = true;
            }
        }
        _reflow = null;
        _reflowMoves = null;
    }

    private void CompleteReflow()
    {
        if (_reflow != null) { StopCoroutine(_reflow); _reflow = null; }
        if (_reflowMoves == null) return;

        for (int i = 0; i < _reflowMoves.Count; i++)
        {
            LineMove m = _reflowMoves[i];
            if (m.piece != null) m.piece.LayoutLine(m.columns, m.line, m.to);
        }
        _reflowMoves = null;
    }

    public void CompletePieceMotion(CreateSheet piece)
    {
        if (piece == null || !_pieceGlides.TryGetValue(piece, out GlideState g)) return;
        if (g.routine != null) StopCoroutine(g.routine);
        _pieceGlides.Remove(piece);

        Transform t = piece.transform;
        t.localPosition = g.toPos;
        t.localRotation = g.toRot;
        t.localScale = g.toScale;
    }

    public void Interrupt()
    {
        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            if (s == null) continue;

            HaltPieceMotion(s);
            s.CompleteGrow();
        }

        foreach (KeyValuePair<Transform, TransformGlide> kv in _transformGlides)
            if (kv.Value.routine != null) StopCoroutine(kv.Value.routine);
        _transformGlides.Clear();

        CompleteReflow();

        if (_projectionRiseRoutine != null) { StopCoroutine(_projectionRiseRoutine); _projectionRiseRoutine = null; }
        if (_projectionRise > 0f)
        {
            _projectionRise = 0f;
            PlaceCurrentProjection();
        }
    }

    public void AmendReorderRecord()
    {
        EditList edits = ManageDatasets.ActiveEdits;
        if (edits == null || _bound == null) return;

        for (int i = edits.Count - 1; i >= 0; i--)
        {
            Edit e = edits[i];
            if (e.kind != EditKind.Sort || e.reorderPreOrder == null) continue;

            IReadOnlyList<int> live = e.reorderIsColumn ? _bound.ColumnOrder : _bound.RowOrder;
            int changed = 0;
            for (int v = 0; v < live.Count && v < e.reorderPreOrder.Count; v++)
                if (live[v] != e.reorderPreOrder[v]) changed++;

            e.reorderLines = changed;
            if (changed == 0) edits.DropAt(i);
            return;
        }
    }

    public void HaltPieceMotion(CreateSheet piece)
    {
        if (piece == null || !_pieceGlides.TryGetValue(piece, out GlideState g)) return;
        if (g.routine != null) StopCoroutine(g.routine);
        _pieceGlides.Remove(piece);
        AmendPoseRecord(piece);
    }

    private void AmendPoseRecord(CreateSheet piece)
    {
        EditList edits = ManageDatasets.ActiveEdits;
        if (edits == null || piece == null) return;

        for (int i = edits.Count - 1; i >= 0; i--)
        {
            Edit e = edits[i];
            if (e.kind != EditKind.Move && e.kind != EditKind.Rotate && e.kind != EditKind.Scale) continue;
            if (e.move.sheetId != piece.sheetId) continue;

            Transform t = piece.transform;
            MoveRecord m = e.move;
            m.postPos = t.localPosition;
            m.postRot = t.localRotation;
            m.postScale = t.localScale;
            m.distance = transform.TransformVector(m.postPos - m.prePos).magnitude;
            e.move = m;

            Vector3 preScale = m.preScale.sqrMagnitude > 1e-6f ? m.preScale : Vector3.one;
            if ((m.postPos - m.prePos).sqrMagnitude < 1e-8f &&
                Quaternion.Angle(m.postRot, m.preRot) < 0.01f &&
                (m.postScale - preScale).sqrMagnitude < 1e-8f)
                edits.DropAt(i);
            return;
        }
    }

    public void CancelPieceMotion(CreateSheet piece)
    {
        if (piece == null || !_pieceGlides.TryGetValue(piece, out GlideState g)) return;
        if (g.routine != null) StopCoroutine(g.routine);
        _pieceGlides.Remove(piece);
    }

    public void AnimatePieceFrom(CreateSheet piece, Vector3 prePos, Quaternion preRot, Vector3 preScale)
    {
        if (piece == null || !isActiveAndEnabled) return;
        if (AgentInstant) { CancelPieceMotion(piece); return; }
        CompletePieceMotion(piece);

        Transform t = piece.transform;
        var g = new GlideState { toPos = t.localPosition, toRot = t.localRotation, toScale = t.localScale };

        float rootScale = Mathf.Abs(transform.lossyScale.x);
        float radius = PieceRadius(piece) * rootScale;
        float atScale = radius * Mathf.Max(Mathf.Abs(preScale.x), Mathf.Abs(g.toScale.x));

        float moveMeters = transform.TransformVector(g.toPos - prePos).magnitude;
        float turnMeters = Quaternion.Angle(preRot, g.toRot) * Mathf.Deg2Rad * atScale;
        float scaleMeters = Mathf.Abs(g.toScale.x - preScale.x) * radius;
        float duration = MotionDuration(Mathf.Max(moveMeters, Mathf.Max(turnMeters, scaleMeters)));

        t.localPosition = prePos;
        t.localRotation = preRot;
        t.localScale = preScale;

        g.routine = StartCoroutine(PieceGlideRoutine(piece, g, prePos, preRot, preScale, duration));
        _pieceGlides[piece] = g;
    }

    private IEnumerator PieceGlideRoutine(CreateSheet piece, GlideState g,
        Vector3 fromPos, Quaternion fromRot, Vector3 fromScale, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            yield return null;
            if (piece == null) yield break;
            if (piece.IsGrabbed) { _pieceGlides.Remove(piece); yield break; }

            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            Transform tr = piece.transform;
            tr.localPosition = Vector3.Lerp(fromPos, g.toPos, k);
            tr.localRotation = Quaternion.Slerp(fromRot, g.toRot, k);
            tr.localScale = Vector3.Lerp(fromScale, g.toScale, k);
        }
        _pieceGlides.Remove(piece);
    }

    public void GlideTransformFrom(Transform target, Vector3 preWorldPos, Quaternion preWorldRot)
    {
        if (target == null || !isActiveAndEnabled) return;
        if (_transformGlides.TryGetValue(target, out TransformGlide running))
        {
            if (running.routine != null) StopCoroutine(running.routine);
            _transformGlides.Remove(target);
        }
        if (AgentInstant) return;

        Vector3 toPos = target.position;
        Quaternion toRot = target.rotation;
        float angle = Quaternion.Angle(preWorldRot, toRot);
        if ((toPos - preWorldPos).sqrMagnitude < 1e-8f && angle < 0.01f) return;

        float turnMeters = angle * Mathf.Deg2Rad * BoundsRadius(target);
        float duration = MotionDuration(Mathf.Max((toPos - preWorldPos).magnitude, turnMeters));

        target.SetPositionAndRotation(preWorldPos, preWorldRot);
        var glide = new TransformGlide { toPos = toPos, toRot = toRot };
        _transformGlides[target] = glide;
        glide.routine = StartCoroutine(
            TransformGlideRoutine(target, preWorldPos, preWorldRot, toPos, toRot, duration));
    }

    private IEnumerator TransformGlideRoutine(Transform target,
        Vector3 fromPos, Quaternion fromRot, Vector3 toPos, Quaternion toRot, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            yield return null;
            if (target == null) yield break;
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            target.SetPositionAndRotation(
                Vector3.Lerp(fromPos, toPos, k), Quaternion.Slerp(fromRot, toRot, k));
        }
        _transformGlides.Remove(target);
    }

    public void CommitPendingGrabs()
    {
        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            if (s != null && s.ForceGrabRelease(out Vector3 pos, out Quaternion rot, out Vector3 scale))
                NotifyMoveCommitted(s, pos, rot, scale);
        }
    }

    public bool RestoreSheetPose(int sheetId, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        CreateSheet sheet = Sheet;
        if (sheet == null || sheet.sheetId != sheetId) return false;
        CancelPieceMotion(sheet);
        sheet.ForgetGrabLook();
        sheet.transform.localPosition = pos;
        sheet.transform.localRotation = rot;
        sheet.transform.localScale = scale;
        ReanchorGrab(sheet);
        return true;
    }

    public void SetLineTint(CreateSheet target, int axis, int min, int max) =>
        SetLineTint(target, axis, min, max, Style.PreviewSwell);

    public void SetLineTint(CreateSheet target, int axis, int min, int max, float swell)
    {
        for (int i = 0; i < _sheets.Count; i++)
        {
            if (_sheets[i] == target) _sheets[i].SetHoverTint(axis, min, max, swell);
            else _sheets[i].ClearTint();
        }
    }

    public void ClearHoverTint()
    {
        for (int i = 0; i < _sheets.Count; i++) _sheets[i].ClearTint();
    }

    public void PlaySwitchGrow()
    {
        float duration = GrowDuration();
        if (duration <= 0f) return;
        if (_root == null || !_root.gameObject.activeSelf) return;
        for (int i = 0; i < _sheets.Count; i++)
            if (_sheets[i] != null) _sheets[i].PlayGrow(duration);
    }

    private void EnsureRoot()
    {
        if (_root != null) return;
        GameObject go = new GameObject("Sheets");
        _root = go.transform;
        _root.SetParent(transform, false);
    }

    private CreateSheet NewSheet()
    {
        EnsureRoot();
        GameObject go = sheetPrefab != null ? Instantiate(sheetPrefab, _root) : new GameObject("Sheet");
        if (go.transform.parent != _root) go.transform.SetParent(_root, false);

        CreateSheet s = go.GetComponent<CreateSheet>();
        if (s == null) s = go.AddComponent<CreateSheet>();
        s.SetGrabbable(_sheetsGrabbable);
        s.sheetId = NextFreeId();
        go.name = $"Sheet_{s.sheetId}";
        return s;
    }

    private int NextFreeId()
    {
        int next = FirstSheetId;
        for (int i = 0; i < _sheets.Count; i++)
        {
            CreateSheet s = _sheets[i];
            if (s != null && s.sheetId >= next) next = s.sheetId + 1;
        }
        return next;
    }

    private void ClearSheets()
    {
        if (_reflow != null) { StopCoroutine(_reflow); _reflow = null; }
        _pieceGlides.Clear();
        for (int i = 0; i < _sheets.Count; i++)
            if (_sheets[i] != null) Destroy(_sheets[i].gameObject);
        _sheets.Clear();
        _builtColumns.Clear();
        _builtRows.Clear();
    }

    private bool ApplyPlacement()
    {
        if (_rowCount == 0 || _colCount == 0) return false;
        if (!_anchored && !ResolveAnchor()) return false;

        transform.SetPositionAndRotation(_anchorCenter, _anchorYaw);

        return true;
    }

    private bool ResolveAnchor()
    {
        if (!TryGetCameraBasis(out Vector3 camPos, out Quaternion yaw)) return false;

        float halfDepth = Mathf.Max(_rowCount - 1, 0) * _cellSize * 0.5f;

        _anchorCenter = camPos + yaw * new Vector3(
            0f, 0f, Mathf.Max(minimumZOffsetFromCamera, 0f) + halfDepth);
        // Three quarters of the height the user's eyes are at, which the
        // floor-level tracking origin makes a real height above the floor rather
        // than an offset from the head: the sheet sits at about chest height for
        // whoever is wearing it, and a shorter or seated user gets it lower by
        // the same proportion. Anchoring it a fixed drop below the eye would
        // follow the head up and down and leave the sheet floating wherever the
        // user happened to be looking from.
        _anchorCenter.y = camPos.y * 0.75f;
        _anchorYaw = yaw;
        _anchored = true;
        return true;
    }

    public void RecenterToUser()
    {
        _anchored = false;
        _placementPending = true;
    }

    private static bool HeadPoseReady()
    {
        if (!OVRManager.OVRManagerinitialized) return true;
        return OVRPlugin.userPresent && OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.EyeCenter);
    }

    private static bool TryGetCameraBasis(out Vector3 position, out Quaternion yaw)
    {
        position = Vector3.zero;
        yaw = Quaternion.identity;

        if (!HeadPoseReady()) return false;

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return false;

        position = cam.position;

        Vector3 flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.ProjectOnPlane(cam.up, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return false;

        yaw = Quaternion.LookRotation(flat.normalized, Vector3.up);
        return true;
    }

}
