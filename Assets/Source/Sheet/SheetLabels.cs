using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

public struct SheetLabelStyle
{
    public bool show;
    public Color color;
    public float gap;
}

public class SheetLabels
{
    private static readonly Vector2 LabelBox = new Vector2(20f, 4f);

    private const float FadeSeconds = 0.12f;

    private class Side
    {
        public bool high;
        public bool flipping;
        public bool want;
        public float fade = 1f;
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

        Step(_cols, Decide(local.z, _cols.high), dt, _colLabel, _lowZ, _highZ, true);
        Step(_rows, Decide(local.x, _rows.high), dt, _rowLabel, _lowX, _highX, false);
    }

    private void Snap(Side side, bool high, Dictionary<int, TextMeshPro> map,
        float lowEdge, float highEdge, bool columns)
    {
        side.high = high;
        side.flipping = false;
        side.fade = 1f;

        ApplyEdges(map, high ? highEdge : lowEdge, columns, high);
        ApplyFades(map, 1f, columns);
    }

    private bool Decide(float coord, bool high) =>
        high ? coord > -_margin : coord > _margin;

    private void Step(Side side, bool desired, float dt,
        Dictionary<int, TextMeshPro> map, float lowEdge, float highEdge, bool columns)
    {
        if (!side.flipping && desired != side.high)
        {
            side.flipping = true;
            side.want = desired;
        }

        if (side.flipping)
        {
            side.fade -= dt / FadeSeconds;
            if (side.fade <= 0f)
            {
                side.fade = 0f;
                side.high = side.want;
                side.flipping = false;
                ApplyEdges(map, side.high ? highEdge : lowEdge, columns, side.high);
            }
        }
        else if (side.fade < 1f)
        {
            side.fade = Mathf.Min(1f, side.fade + dt / FadeSeconds);
        }
        else return;

        ApplyFades(map, side.fade, columns);
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

        _cols.fade = 1f;
        _cols.flipping = false;
        _rows.fade = 1f;
        _rows.flipping = false;

        if (data != null && style.show && cellSize > 0f)
        {
            float centerX = CreateSheet.Center(colMin, colMax, cellSize, _colGroupSize, groupGap);
            float centerZ = CreateSheet.Center(rowMin, rowMax, cellSize, 1, 0f);
            float half = cubeSide * 0.5f;
            float y = baseY;

            float ColX(int vc) => CreateSheet.Raw(vc, cellSize, _colGroupSize, groupGap) - centerX;
            float RowZ(int vr) => CreateSheet.Raw(vr, cellSize, 1, 0f) - centerZ;

            _margin = cellSize;
            _lowZ = RowZ(rowMin) - half - style.gap;
            _highZ = RowZ(rowMax) + half + style.gap;
            _lowX = ColX(colMin) - half - style.gap;
            _highX = ColX(colMax) + half + style.gap;

            IReadOnlyList<int> colOrder = data.ColumnOrder;
            IReadOnlyList<string> colTitles = data.ColumnTitles;
            float edgeZ = _cols.high ? _highZ : _lowZ;
            bool grouped = _colGroupSize > 1;
            for (int vc = colMin; vc <= colMax; vc++)
            {
                if (vc < 0 || vc >= colOrder.Count) continue;
                int dCol = colOrder[vc];
                if (dCol < 0 || dCol >= colTitles.Count) continue;

                string text = grouped ? Tick(data.SeriesTitleAt(true, vc)) : colTitles[dCol];
                TextMeshPro label = Place(text,
                    new Vector3(ColX(vc), y, edgeZ), style.color,
                    Facing(true, _cols.high), grouped ? TickScale : 1f);
                if (label != null) _colLabel[vc] = label;
            }

            if (grouped)
            {
                float groupZ = edgeZ + (_cols.high ? _colGroupOut : -_colGroupOut);
                for (int g = 0; g <= colMax / _colGroupSize; g++)
                {
                    int lo = g * _colGroupSize;
                    if (lo < colMin || lo > colMax) continue;
                    TextMeshPro label = Place(data.GroupTitleAt(true, g),
                        new Vector3(ColX(lo), y, groupZ), style.color,
                        Facing(true, _cols.high));
                    if (label == null) continue;
                    _colGroup[g] = label;
                    CentreGroup(g);
                }
            }

            IReadOnlyList<int> rowOrder = data.RowOrder;
            IReadOnlyList<string> rowTitles = data.RowTitles;
            float edgeX = _rows.high ? _highX : _lowX;
            for (int vr = rowMin; vr <= rowMax; vr++)
            {
                if (vr < 0 || vr >= rowOrder.Count) continue;
                int dRow = rowOrder[vr];
                if (dRow < 0 || dRow >= rowTitles.Count) continue;
                TextMeshPro label = Place(rowTitles[dRow],
                    new Vector3(edgeX, y, RowZ(vr)), style.color,
                    Facing(false, _rows.high));
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
        t.localScale = Vector3.one * (Style.WorldTextScale * scale);

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
