using System.Collections.Generic;
using TMPro;
using UnityEngine;

public struct SheetLabelStyle
{
    public bool Show;
    public Color Color;
    public float Gap;
}

public class SheetLabels
{
    private static readonly Vector2 LabelBox = new Vector2(20f, 4f);

    private const float FadeSeconds = 0.12f;

    private class Side
    {
        public bool High;
        public bool Flipping;
        public bool Want;
        public float Fade = 1f;
    }

    private readonly Transform _owner;
    private readonly List<TextMeshPro> _pool = new List<TextMeshPro>();
    private readonly Dictionary<int, TextMeshPro> _colLabel = new Dictionary<int, TextMeshPro>();
    private readonly Dictionary<int, TextMeshPro> _rowLabel = new Dictionary<int, TextMeshPro>();

    // On a grouped axis the column labels become small series ticks and the
    // metric name is drawn once, further out, riding the middle of its block.
    private readonly Dictionary<int, TextMeshPro> _colGroup = new Dictionary<int, TextMeshPro>();
    private int _colGroupSize = 1;
    private float _colGroupOut;

    private const float TickScale = 0.72f;

    // The cell pitch the fixed text scale was chosen against. A label is sized
    // relative to the column it stands under rather than in metres, so the sheet
    // can be a tabletop model or a wall and the titles stay the same size
    // against the bars instead of running into one another at the small end.
    private const float ReferenceCellSize = 0.1f;

    private float _textScale = 1f;
    private readonly Side _cols = new Side();
    private readonly Side _rows = new Side();
    private Transform _root;
    private int _used;
    private bool _placed;

    private float _lowZ;
    private float _highZ;
    private float _lowX;
    private float _highX;
    private float _margin;

    public SheetLabels(Transform owner) => _owner = owner;

    public void FaceViewer(float dt)
    {
        if (_used == 0 || _owner == null) return;

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;

        Vector3 local = _owner.InverseTransformPoint(cam.position);

        if (!_placed)
        {
            _placed = true;
            Snap(_cols, local.z > 0f, _colLabel, _lowZ, _highZ, true);
            Snap(_rows, local.x > 0f, _rowLabel, _lowX, _highX, false);
            return;
        }

        Step(_cols, Decide(local.z, _cols.High), dt, _colLabel, _lowZ, _highZ, true);
        Step(_rows, Decide(local.x, _rows.High), dt, _rowLabel, _lowX, _highX, false);
    }

    private void Snap(Side side, bool high, Dictionary<int, TextMeshPro> map,
        float lowEdge, float highEdge, bool columns)
    {
        side.High = high;
        side.Flipping = false;
        side.Fade = 1f;

        ApplyEdges(map, high ? highEdge : lowEdge, columns, high);
        ApplyFades(map, 1f, columns);
    }

    private bool Decide(float coord, bool high) =>
        high ? coord > -_margin : coord > _margin;

    private void Step(Side side, bool desired, float dt,
        Dictionary<int, TextMeshPro> map, float lowEdge, float highEdge, bool columns)
    {
        if (!side.Flipping && desired != side.High)
        {
            side.Flipping = true;
            side.Want = desired;
        }

        if (side.Flipping)
        {
            side.Fade -= dt / FadeSeconds;
            if (side.Fade <= 0f)
            {
                side.Fade = 0f;
                side.High = side.Want;
                side.Flipping = false;
                ApplyEdges(map, side.High ? highEdge : lowEdge, columns, side.High);
            }
        }
        else if (side.Fade < 1f)
        {
            side.Fade = Mathf.Min(1f, side.Fade + dt / FadeSeconds);
        }
        else return;

        ApplyFades(map, side.Fade, columns);
    }

    private const float UpwardTiltDegrees = 45f;

    private static Quaternion Facing(bool columns, bool high) =>
        (columns
            ? Quaternion.Euler(0f, high ? 180f : 0f, 0f)
            : Quaternion.Euler(0f, high ? -90f : 90f, 0f))
        * Quaternion.Euler(UpwardTiltDegrees, 0f, 0f);

    private void ApplyEdges(Dictionary<int, TextMeshPro> map, float edge, bool columns, bool high)
    {
        ApplyEdge(map, edge, columns, high);
        if (columns && _colGroup.Count > 0)
            ApplyEdge(_colGroup, edge + (high ? _colGroupOut : -_colGroupOut), columns, high);
    }

    private void ApplyFades(Dictionary<int, TextMeshPro> map, float alpha, bool columns)
    {
        ApplyFade(map, alpha);
        if (columns) ApplyFade(_colGroup, alpha);
    }

    private static void ApplyEdge(Dictionary<int, TextMeshPro> map, float edge, bool columns, bool high)
    {
        Quaternion facing = Facing(columns, high);

        foreach (KeyValuePair<int, TextMeshPro> pair in map)
        {
            if (pair.Value == null) continue;

            Vector3 p = pair.Value.transform.localPosition;
            if (columns) p.z = edge;
            else p.x = edge;
            pair.Value.transform.localPosition = p;
            pair.Value.transform.localRotation = facing;
        }
    }

    private static void ApplyFade(Dictionary<int, TextMeshPro> map, float alpha)
    {
        foreach (KeyValuePair<int, TextMeshPro> pair in map)
            if (pair.Value != null) pair.Value.alpha = alpha;
    }

    public void MoveLine(bool columns, int line, float coord)
    {
        Dictionary<int, TextMeshPro> map = columns ? _colLabel : _rowLabel;
        if (map.TryGetValue(line, out TextMeshPro label) && label != null)
        {
            Vector3 p = label.transform.localPosition;
            if (columns) p.x = coord;
            else p.z = coord;
            label.transform.localPosition = p;
        }

        if (columns && _colGroupSize > 1) CentreGroup(line / _colGroupSize);
    }

    // The metric name sits at the mean of its ticks, so it follows however they move.
    private void CentreGroup(int group)
    {
        if (!_colGroup.TryGetValue(group, out TextMeshPro label) || label == null) return;

        float sum = 0f;
        int found = 0;
        for (int s = 0; s < _colGroupSize; s++)
            if (_colLabel.TryGetValue(group * _colGroupSize + s, out TextMeshPro tick) && tick != null)
            {
                sum += tick.transform.localPosition.x;
                found++;
            }
        if (found == 0) return;

        Vector3 p = label.transform.localPosition;
        p.x = sum / found;
        label.transform.localPosition = p;
    }

    // "2019" reads better as "'19" once the metric name carries the meaning.
    private static string Tick(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string t = title.Trim();
        if (t.Length != 4) return t;
        for (int i = 0; i < 4; i++)
            if (!char.IsDigit(t[i])) return t;
        return "'" + t.Substring(2);
    }

    public void Rebuild(DataSource data, int rowMin, int rowMax, int colMin, int colMax,
        float cellSize, float groupGap, float cubeSide, float baseY, SheetLabelStyle style)
    {
        _used = 0;
        _colLabel.Clear();
        _rowLabel.Clear();
        _colGroup.Clear();
        _colGroupSize = data != null ? data.ColumnGroupSize : 1;
        if (_colGroupSize <= 1) groupGap = 0f;
        _colGroupOut = cellSize * 0.85f;
        _textScale = cellSize > 0f ? cellSize / ReferenceCellSize : 1f;

        _cols.Fade = 1f;
        _cols.Flipping = false;
        _rows.Fade = 1f;
        _rows.Flipping = false;

        if (data != null && style.Show && cellSize > 0f)
        {
            float centerX = SheetPiece.Center(colMin, colMax, cellSize, _colGroupSize, groupGap);
            float centerZ = SheetPiece.Center(rowMin, rowMax, cellSize, 1, 0f);
            float half = cubeSide * 0.5f;
            float y = baseY;

            float ColX(int vc) => SheetPiece.Raw(vc, cellSize, _colGroupSize, groupGap) - centerX;
            float RowZ(int vr) => SheetPiece.Raw(vr, cellSize, 1, 0f) - centerZ;

            _margin = cellSize;
            _lowZ = RowZ(rowMin) - half - style.Gap;
            _highZ = RowZ(rowMax) + half + style.Gap;
            _lowX = ColX(colMin) - half - style.Gap;
            _highX = ColX(colMax) + half + style.Gap;

            IReadOnlyList<int> colOrder = data.ColumnOrder;
            IReadOnlyList<string> colTitles = data.ColumnTitles;
            float edgeZ = _cols.High ? _highZ : _lowZ;
            bool grouped = _colGroupSize > 1;
            for (int vc = colMin; vc <= colMax; vc++)
            {
                if (vc < 0 || vc >= colOrder.Count) continue;
                int dCol = colOrder[vc];
                if (dCol < 0 || dCol >= colTitles.Count) continue;

                string text = grouped ? Tick(data.SeriesTitleAt(true, vc)) : colTitles[dCol];
                TextMeshPro label = Place(text,
                    new Vector3(ColX(vc), y, edgeZ), style.Color,
                    Facing(true, _cols.High), grouped ? TickScale : 1f);
                if (label != null) _colLabel[vc] = label;
            }

            if (grouped)
            {
                float groupZ = edgeZ + (_cols.High ? _colGroupOut : -_colGroupOut);
                for (int g = 0; g <= colMax / _colGroupSize; g++)
                {
                    int lo = g * _colGroupSize;
                    if (lo < colMin || lo > colMax) continue;
                    TextMeshPro label = Place(data.GroupTitleAt(true, g),
                        new Vector3(ColX(lo), y, groupZ), style.Color,
                        Facing(true, _cols.High));
                    if (label == null) continue;
                    _colGroup[g] = label;
                    CentreGroup(g);
                }
            }

            float edgeX = _rows.High ? _highX : _lowX;
            for (int vr = rowMin; vr <= rowMax; vr++)
            {
                TextMeshPro label = Place(data.TitleAt(false, vr),
                    new Vector3(edgeX, y, RowZ(vr)), style.Color,
                    Facing(false, _rows.High));
                if (label != null) _rowLabel[vr] = label;
            }
        }

        for (int i = _used; i < _pool.Count; i++)
            if (_pool[i] != null) _pool[i].gameObject.SetActive(false);
    }

    private TextMeshPro Place(string text, Vector3 localPosition, Color color, Quaternion facing,
        float scale = 1f)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        TextMeshPro label = Acquire(_used++);
        if (label == null) return null;

        label.text = text;
        label.color = color;

        Transform t = label.transform;
        t.localPosition = localPosition;
        t.localRotation = facing;
        t.localScale = Vector3.one * (Style.WorldTextScale * scale * _textScale);

        label.gameObject.SetActive(true);
        return label;
    }

    private TextMeshPro Acquire(int index)
    {
        EnsureRoot();
        if (_root == null) return null;

        while (_pool.Count <= index)
        {
            GameObject go = new GameObject($"Label_{_pool.Count}");
            go.transform.SetParent(_root, false);

            TextMeshPro label = go.AddComponent<TextMeshPro>();
            Style.ApplyBody(label);
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.rectTransform.sizeDelta = LabelBox;
            _pool.Add(label);
        }

        return _pool[index];
    }

    private void EnsureRoot()
    {
        if (_root != null || _owner == null) return;
        GameObject go = new GameObject("Labels");
        _root = go.transform;
        _root.SetParent(_owner, false);
    }
}
