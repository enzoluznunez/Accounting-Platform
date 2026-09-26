using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;

[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public class CreateSheet : MonoBehaviour
{

    private const float ZeroPlateFraction = 0.008f;

    private static readonly Color NoData = new Color(0.85f, 0.60f, 0.25f);

    private static readonly float[] Lightness =
        { 0f, 0.127f, 0.233f, 0.346f, 0.466f, 0.593f, 0.724f, 0.860f, 1f };

    private static readonly List<CreateSheet> _all = new List<CreateSheet>();
    public static IReadOnlyList<CreateSheet> All => _all;

    public int sheetId = -1;
    public int rowMin, rowMax, colMin, colMax;

    private readonly List<CreateCube> _pool = new List<CreateCube>();
    private readonly List<CreateCube> _live = new List<CreateCube>();
    private readonly Dictionary<int, CreateCube> _byCell = new Dictionary<int, CreateCube>();
    private readonly Dictionary<int, List<CreateCube>> _byCol = new Dictionary<int, List<CreateCube>>();
    private readonly Dictionary<int, List<CreateCube>> _byRow = new Dictionary<int, List<CreateCube>>();

    private BoxCollider _bounds;
    private Rigidbody _body;
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrab;
    private OneGrabTranslateTransformer _slide;

    private Material _material;
    private SheetLabels _labels;

    private struct BarTarget
    {
        public CreateCube cube;
        public Vector3 center;
        public Vector3 size;
    }

    private readonly List<BarTarget> _bars = new List<BarTarget>();
    private Coroutine _grow;
    private bool _detached;
    private float _cellSize;
    private float _groupGap;
    private int _groupSize = 1;
    private float _height;
    private float _baseY;

    private bool _wasGrabbed;
    private Vector3 _grabPos;
    private Quaternion _grabRot;
    private Vector3 _grabScale;

    public IReadOnlyList<CreateCube> Cubes => _live;
    public int RowCount => rowMax - rowMin + 1;
    public int ColCount => colMax - colMin + 1;
    public float CellSize => _cellSize;
    public float BaseY => _baseY;
    public bool IsBuilt => _live.Count > 0;

    // A grouped axis draws its lines in tight blocks — one metric's years sitting
    // together — with an extra gap between blocks. Every index-to-position
    // conversion in the app runs through these, so spacing lives in one place and
    // LineFraction stays exactly LineCoord's inverse.
    public static float Raw(int line, float cellSize, int groupSize, float groupGap) =>
        line * cellSize + (groupSize > 1 ? (line / groupSize) * groupGap : 0f);

    public static float Center(int min, int max, float cellSize, int groupSize, float groupGap) =>
        (Raw(min, cellSize, groupSize, groupGap) + Raw(max, cellSize, groupSize, groupGap)) * 0.5f;

    public static float Center(int min, int max, float cellSize) => Center(min, max, cellSize, 1, 0f);

    public int GroupSizeOn(bool columns) => columns ? _groupSize : 1;
    public float GroupGapOn(bool columns) => columns ? _groupGap : 0f;

    public float CenterX => Center(colMin, colMax, _cellSize, _groupSize, _groupGap);
    public float CenterZ => Center(rowMin, rowMax, _cellSize, 1, 0f);

    public float LineCoord(bool columns, int line) =>
        Raw(line, _cellSize, GroupSizeOn(columns), GroupGapOn(columns)) - (columns ? CenterX : CenterZ);

    public float LineFraction(bool columns, float coord)
    {
        if (_cellSize <= 1e-6f) return 0f;

        float p = coord + (columns ? CenterX : CenterZ);
        int size = GroupSizeOn(columns);
        float gap = GroupGapOn(columns);
        if (size <= 1 || gap <= 0f) return p / _cellSize;

        float pitch = size * _cellSize + gap;
        float block = Mathf.Floor(p / pitch);
        float within = p - block * pitch;

        // Inside a block the lines sit one cell apart; across the gap the fraction
        // runs on smoothly to the next block's first line, so a drag never jumps.
        float edge = (size - 1) * _cellSize;
        float step = within <= edge
            ? within / _cellSize
            : (size - 1) + (within - edge) / (_cellSize + gap);

        return block * size + step;
    }

    // A block is one addressable unit along an axis: a whole metric on a grouped
    // axis, a single line otherwise. Blocks are evenly pitched even when the lines
    // inside them are not, so anything that drags or reorders can work in block
    // space and stay the simple uniform problem it always was.
    public float BlockPitch(bool columns) => GroupSizeOn(columns) * _cellSize + GroupGapOn(columns);

    public int BlockMin(bool columns) => (columns ? colMin : rowMin) / GroupSizeOn(columns);
    public int BlockMax(bool columns) => (columns ? colMax : rowMax) / GroupSizeOn(columns);

    private float BlockInset(bool columns) => (GroupSizeOn(columns) - 1) * _cellSize * 0.5f;

    public float BlockCoord(bool columns, int block) =>
        LineCoord(columns, block * GroupSizeOn(columns)) + BlockInset(columns);

    public float BlockFraction(bool columns, float coord)
    {
        float pitch = BlockPitch(columns);
        if (pitch <= 1e-6f) return 0f;
        return (coord + (columns ? CenterX : CenterZ) - BlockInset(columns)) / pitch;
    }

    public void LayoutBlock(bool columns, int block, float coord)
    {
        int size = GroupSizeOn(columns);
        float first = coord - BlockInset(columns);
        for (int s = 0; s < size; s++)
            LayoutLine(columns, block * size + s, first + s * _cellSize);
    }

    public float BlockOffset(bool columns, int block)
    {
        int size = GroupSizeOn(columns);
        return LineOffset(columns, block * size) + BlockInset(columns);
    }

    public Vector3 LocalOf(int visRow, int visCol) =>
        new Vector3(LineCoord(true, visCol), 0f, LineCoord(false, visRow));

    public bool Contains(int visRow, int visCol) =>
        visRow >= rowMin && visRow <= rowMax && visCol >= colMin && visCol <= colMax;

    public CreateCube CubeAt(int visRow, int visCol) =>
        _byCell.TryGetValue(Key(visRow, visCol), out CreateCube c) ? c : null;

    public void Build(DataSource data, Material material,
        int rMin, int rMax, int cMin, int cMax,
        float cellSize, float groupGap, float height, float baseY, float cubeSide,
        SheetLabelStyle labels)
    {
        if (data == null) return;

        if (_grow != null) { StopCoroutine(_grow); _grow = null; }
        _pendingGrow = -1f;
        _bars.Clear();

        rowMin = rMin; rowMax = rMax; colMin = cMin; colMax = cMax;
        _material = material;
        _cellSize = cellSize;
        _groupSize = data.ColumnGroupSize;
        _groupGap = _groupSize > 1 ? groupGap : 0f;
        _height = height;
        _baseY = baseY;

        IReadOnlyList<int> rowOrder = data.RowOrder;
        IReadOnlyList<int> colOrder = data.ColumnOrder;

        float half = cubeSide * 0.5f;

        int used = 0;
        _byCell.Clear();
        foreach (List<CreateCube> line in _byCol.Values) line.Clear();
        foreach (List<CreateCube> line in _byRow.Values) line.Clear();

        for (int vr = rMin; vr <= rMax; vr++)
        {
            if (vr < 0 || vr >= rowOrder.Count) continue;
            int dRow = rowOrder[vr];
            float z = LineCoord(false, vr);

            for (int vc = cMin; vc <= cMax; vc++)
            {
                if (vc < 0 || vc >= colOrder.Count) continue;
                int dCol = colOrder[vc];

                bool has = data.HasValue(dRow, dCol);
                float topY = has ? data.GetHeightFraction(dRow, dCol) * height : baseY;
                float lo = Mathf.Min(topY, baseY);
                float hi = Mathf.Max(topY, baseY);

                if (hi - lo <= 1e-6f)
                {
                    float plate = height * ZeroPlateFraction * 0.5f;
                    lo = baseY - plate;
                    hi = baseY + plate;
                }

                CreateCube cube = Acquire(used++);
                cube.SetCell(vr, vc, dRow, dCol, data.GetValue(dRow, dCol));
                Vector3 center = new Vector3(LineCoord(true, vc), (lo + hi) * 0.5f, z);
                Vector3 size = new Vector3(half * 2f, hi - lo, half * 2f);
                cube.SetBox(center, size);
                _bars.Add(new BarTarget { cube = cube, center = center, size = size });

                cube.SetColor(ColorFor(data, has, dRow, dCol));
                cube.SetVisible(true);

                _byCell[Key(vr, vc)] = cube;
                LineBucket(_byCol, vc).Add(cube);
                LineBucket(_byRow, vr).Add(cube);
            }
        }

        _live.Clear();
        for (int i = 0; i < used; i++) _live.Add(_pool[i]);
        _tint = null;
        for (int i = used; i < _pool.Count; i++) _pool[i].SetVisible(false);

        FitBounds();

        if (_labels == null) _labels = new SheetLabels(transform);
        _labels.Rebuild(data, rMin, rMax, cMin, cMax, cellSize, _groupGap, cubeSide, baseY, labels);
    }

    public IReadOnlyList<CreateCube> CubesInLine(bool columns, int line) =>
        LineCubes(columns, line) ?? (IReadOnlyList<CreateCube>)Array.Empty<CreateCube>();

    private void LateUpdate()
    {
        if (_labels != null) _labels.FaceViewer(Time.unscaledDeltaTime);
    }

    private float _pendingGrow = -1f;

    public void PlayGrow(float duration)
    {
        if (duration <= 0f || _bars.Count == 0) return;

        if (_grow != null) { StopCoroutine(_grow); _grow = null; }
        ApplyGrow(0f);
        if (!isActiveAndEnabled)
        {
            _pendingGrow = duration;
            return;
        }
        _grow = StartCoroutine(GrowRoutine(duration));
    }

    private void StartPendingGrow()
    {
        if (_pendingGrow <= 0f) return;
        float duration = _pendingGrow;
        _pendingGrow = -1f;
        _grow = StartCoroutine(GrowRoutine(duration));
    }

    public void CompleteGrow()
    {
        bool pending = _pendingGrow > 0f;
        _pendingGrow = -1f;
        if (_grow == null && !pending) return;

        if (_grow != null)
        {
            StopCoroutine(_grow);
            _grow = null;
        }
        ApplyGrow(1f);
    }

    private IEnumerator GrowRoutine(float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            yield return null;
            t += Time.deltaTime;
            ApplyGrow(Mathf.Clamp01(t / duration));
        }

        ApplyGrow(1f);
        _grow = null;
    }

    private void ApplyGrow(float k)
    {
        for (int i = 0; i < _bars.Count; i++)
        {
            BarTarget b = _bars[i];
            if (b.cube == null) continue;

            float half = b.size.y * 0.5f;
            float lo = Mathf.Lerp(_baseY, b.center.y - half, k);
            float hi = Mathf.Lerp(_baseY, b.center.y + half, k);

            b.cube.SetBox(new Vector3(b.center.x, (lo + hi) * 0.5f, b.center.z),
                          new Vector3(b.size.x, hi - lo, b.size.z));
        }
    }

    public void CollectLineCoords(Dictionary<int, float> columns, Dictionary<int, float> rows)
    {
        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube cube = _live[i];
            if (cube == null) continue;
            Vector3 p = cube.transform.localPosition;
            columns[cube.dataCol] = p.x;
            rows[cube.dataRow] = p.z;
        }
    }

    // A bar is drawn in the colour of the industry its row belongs to, which
    // the sheet states per row, so reordering or filtering never repaints one.
    private static Color ColorFor(DataSource data, bool has, int dRow, int dCol) =>
        has ? Shade(data.RowColorAt(dRow), data.GetColorFraction(dRow, dCol)) : NoData;

    private static Color Shade(Color top, float t) =>
        Color.Lerp(Color.black, top, PerceptualFraction(t));

    private static float PerceptualFraction(float t)
    {
        t = Mathf.Clamp01(t);
        float scaled = t * (Lightness.Length - 1);
        int lower = Mathf.FloorToInt(scaled);
        int upper = Mathf.Min(lower + 1, Lightness.Length - 1);
        return Mathf.Lerp(Lightness[lower], Lightness[upper], scaled - lower);
    }

    private static List<CreateCube> LineBucket(Dictionary<int, List<CreateCube>> map, int line)
    {
        if (!map.TryGetValue(line, out List<CreateCube> bucket))
        {
            bucket = new List<CreateCube>();
            map[line] = bucket;
        }
        return bucket;
    }

    private List<CreateCube> LineCubes(bool columns, int line) =>
        (columns ? _byCol : _byRow).TryGetValue(line, out List<CreateCube> bucket) ? bucket : null;

    public void LayoutLine(bool columns, int line, float coord)
    {
        List<CreateCube> cubes = LineCubes(columns, line);
        if (cubes != null)
        {
            for (int i = 0; i < cubes.Count; i++)
            {
                CreateCube cube = cubes[i];
                if (cube == null) continue;

                Vector3 p = cube.transform.localPosition;
                if (columns) p.x = coord;
                else p.z = coord;
                cube.transform.localPosition = p;
            }
        }

        if (_labels != null) _labels.MoveLine(columns, line, coord);
    }

    public float LineOffset(bool columns, int line)
    {
        List<CreateCube> cubes = LineCubes(columns, line);
        if (cubes != null)
        {
            for (int i = 0; i < cubes.Count; i++)
            {
                CreateCube cube = cubes[i];
                if (cube == null) continue;

                Vector3 p = cube.transform.localPosition;
                return columns ? p.x : p.z;
            }
        }
        return LineCoord(columns, line);
    }

    public void RestLines()
    {
        for (int vc = colMin; vc <= colMax; vc++) LayoutLine(true, vc, LineCoord(true, vc));
        for (int vr = rowMin; vr <= rowMax; vr++) LayoutLine(false, vr, LineCoord(false, vr));

        for (int i = 0; i < _live.Count; i++)
            if (_live[i] != null) _live[i].transform.localRotation = Quaternion.identity;
    }

    public void SetHoverTint(int axis, int min, int max) =>
        SetHoverTint(axis, min, max, Style.PreviewSwell);

    // What the bars are lit with now, or null when that is not known (after a
    // build). A tool re-sends its tint every frame the finger moves, so an
    // unchanged one is not walked across every bar again.
    private (int axis, int min, int max, float swell)? _tint;
    private static readonly (int, int, int, float) Untinted = (0, 0, 0, 0f);

    public void SetHoverTint(int axis, int min, int max, float swell)
    {
        var tint = (axis, min, max, swell);
        if (_tint == tint) return;
        _tint = tint;

        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube cube = _live[i];
            int v = axis == 1 ? cube.visCol : cube.visRow;
            bool hit = axis == 3 || (axis > 0 && axis < 3 && v >= min && v <= max);
            if (hit) cube.SetHighlight(swell);
            else cube.ClearHighlight();
        }
    }

    public void ClearTint()
    {
        if (_tint == Untinted) return;
        _tint = Untinted;

        for (int i = 0; i < _live.Count; i++) _live[i].ClearHighlight();
    }

    private bool _grabbedLook;
    private Vector3 _restScale = Vector3.one;

    public void SetGrabLook(bool on)
    {
        if (_grabbedLook == on) return;

        if (on)
        {
            _restScale = transform.localScale;
            transform.localScale = _restScale * Style.EngageScale;
        }
        else
        {
            transform.localScale = _restScale;
        }

        _grabbedLook = on;
    }

    public void ForgetGrabLook()
    {
        _grabbedLook = false;
        _restScale = transform.localScale;
    }

    public void SetCubeColliders(bool on)
    {
        for (int i = 0; i < _live.Count; i++)
            if (_live[i].Collider != null) _live[i].Collider.enabled = on && _live[i].IsVisible;
    }

    public void SetPickable(bool on)
    {
        EnsureComponents();
        if (_bounds != null) _bounds.enabled = on;
        SetCubeColliders(on);
    }

    private void FitBounds()
    {
        EnsureComponents();
        if (_live.Count == 0) { _bounds.size = Vector3.one * 1e-3f; return; }

        Bounds b = new Bounds(_live[0].transform.localPosition, Vector3.zero);
        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube c = _live[i];
            b.Encapsulate(new Bounds(c.transform.localPosition, c.transform.localScale));
        }
        _bounds.center = b.center;
        _bounds.size = b.size;
    }

    private void EnsureComponents()
    {
        if (_bounds == null) _bounds = GetComponent<BoxCollider>();
        if (_body == null)
        {
            _body = GetComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.useGravity = false;
        }
        if (_grabbable == null) _grabbable = GetComponentInChildren<Grabbable>(true);
        if (_grabbable != null && _grabbable.Transform == null)
            _grabbable.InjectOptionalTargetTransform(_grabbable.transform);
        if (_handGrab == null) _handGrab = GetComponentInChildren<HandGrabInteractable>(true);
        if (_slide == null) _slide = GetComponent<OneGrabTranslateTransformer>();
    }

    public bool IsGrabbed
    {
        get
        {
            EnsureComponents();
            return _grabbable != null && _grabbable.SelectingPointsCount > 0;
        }
    }

    public bool PollGrabRelease(out Vector3 prePos, out Quaternion preRot, out Vector3 preScale)
    {
        prePos = Vector3.zero;
        preRot = Quaternion.identity;
        preScale = Vector3.one;

        bool grabbed = IsGrabbed;
        if (grabbed == _wasGrabbed) return false;
        _wasGrabbed = grabbed;

        if (grabbed)
        {
            _grabPos = transform.localPosition;
            _grabRot = transform.localRotation;
            _grabScale = transform.localScale;
            return false;
        }

        prePos = _grabPos;
        preRot = _grabRot;
        preScale = _grabScale;
        return true;
    }

    public bool ForceGrabRelease(out Vector3 prePos, out Quaternion preRot, out Vector3 preScale)
    {
        prePos = Vector3.zero;
        preRot = Quaternion.identity;
        preScale = Vector3.one;

        if (!_wasGrabbed) return false;
        _wasGrabbed = false;

        prePos = _grabPos;
        preRot = _grabRot;
        preScale = _grabScale;
        return true;
    }

    public void SetGrabbable(bool on)
    {
        EnsureComponents();
        if (_grabbable != null) _grabbable.enabled = on;
        if (_handGrab != null) _handGrab.enabled = on;
    }

    public string DescribeGrab()
    {
        EnsureComponents();
        string g = _grabbable == null ? "null" : _grabbable.enabled.ToString();
        string h = _handGrab == null ? "null" : _handGrab.enabled.ToString();
        string b = _bounds == null ? "null" : $"{_bounds.enabled} {_bounds.size:F2}";
        return $"id={sheetId} rows={rowMin}-{rowMax} cols={colMin}-{colMax} " +
               $"grabbable={g} handGrab={h} slide={_slide != null} " +
               $"points={(_grabbable == null ? -1 : _grabbable.MaxGrabPoints)} " +
               $"bounds={b} kinematic={(_body == null ? "null" : _body.isKinematic.ToString())} " +
               $"active={gameObject.activeInHierarchy} layer={gameObject.layer}";
    }

    public void SetOneGrab()
    {
        EnsureComponents();
        if (_grabbable == null) return;

        _grabbable.MaxGrabPoints = 1;
        if (_slide != null) _grabbable.InjectOptionalOneGrabTransformer(_slide);
    }

    public void SetTwoGrab(ITransformer transformer)
    {
        EnsureComponents();
        if (_grabbable == null || transformer == null) return;

        _grabbable.MaxGrabPoints = 2;
        _grabbable.InjectOptionalOneGrabTransformer(null);
        _grabbable.InjectOptionalTwoGrabTransformer(transformer);
        transformer.Initialize(_grabbable);
    }

    private CreateCube Acquire(int index)
    {
        while (_pool.Count <= index)
        {
            GameObject go = new GameObject($"Cube_{_pool.Count}");
            go.transform.SetParent(transform, false);
            CreateCube cube = go.AddComponent<CreateCube>();
            cube.Init(_material);
            cube.SetSheet(this);
            _pool.Add(cube);
        }
        return _pool[index];
    }

    private void OnEnable()
    {
        if (!_detached && !_all.Contains(this)) _all.Add(this);
        StartPendingGrow();
    }

    private void OnDisable() => _all.Remove(this);

    public void MarkDetached()
    {
        _detached = true;
        _all.Remove(this);
    }

    private static int Key(int visRow, int visCol) => (visRow << 16) | (visCol & 0xFFFF);
}
