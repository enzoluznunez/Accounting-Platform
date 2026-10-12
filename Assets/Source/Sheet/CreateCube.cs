using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider))]
public class CreateCube : MonoBehaviour
{
    public int visRow = -1;
    public int visCol = -1;
    public int dataRow = -1;
    public int dataCol = -1;

    public CreateSheet Sheet { get; private set; }

    public void SetSheet(CreateSheet sheet) => Sheet = sheet;

    private MeshFilter _filter;
    private MeshRenderer _renderer;
    private BoxCollider _collider;
    private Mesh _mesh;

    private readonly Color[] _colors = new Color[24];
    private Color _baseColor = Color.white;
    private Vector3 _baseScale = Vector3.one;
    private float _swell;
    private bool _painted;
    private bool _visible = true;

    public BoxCollider Collider => _collider;

    public bool IsVisible => _visible;

    private static readonly Vector3[] Verts =
    {
        new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
        new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),

        new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
        new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f),

        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
        new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),

        new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
        new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f),

        new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
        new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f,  0.5f),

        new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
        new Vector3( 0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
    };

    private static readonly int[] Tris =
    {
         0,  1,  2,  0,  2,  3,
         4,  5,  6,  4,  6,  7,
         8,  9, 10,  8, 10, 11,
        12, 13, 14, 12, 14, 15,
        16, 17, 18, 16, 18, 19,
        20, 21, 22, 20, 22, 23,
    };

    public void Init(Material material)
    {
        if (_filter == null) _filter = GetComponent<MeshFilter>();
        if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
        if (_collider == null) _collider = GetComponent<BoxCollider>();

        _collider.center = Vector3.zero;
        _collider.size = Vector3.one;
        transform.localRotation = Quaternion.identity;

        if (material != null) _renderer.sharedMaterial = material;

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "CreateCube" };
            _mesh.SetVertices(Verts);
            _mesh.SetTriangles(Tris, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            _filter.sharedMesh = _mesh;
        }
    }

    public void SetCell(int vRow, int vCol, int dRow, int dCol)
    {
        visRow = vRow;
        visCol = vCol;
        dataRow = dRow;
        dataCol = dCol;
    }

    public void SetBox(Vector3 localCenter, Vector3 size)
    {
        transform.localPosition = localCenter;
        transform.localRotation = Quaternion.identity;
        _baseScale = new Vector3(
            Mathf.Max(size.x, 1e-4f),
            Mathf.Max(size.y, 1e-4f),
            Mathf.Max(size.z, 1e-4f));
        ApplyScale();
    }

    public void SetColor(Color color)
    {
        if (_painted && _baseColor == color) return;
        _baseColor = color;
        Paint(color);
    }

    public void SetHighlight(float swell)
    {
        if (Mathf.Approximately(_swell, swell)) return;
        _swell = swell;
        ApplyScale();
    }

    public void ClearHighlight() => SetHighlight(0f);

    private void ApplyScale()
    {
        float spread = 1f + Mathf.Max(_swell, 0f);
        transform.localScale = new Vector3(
            _baseScale.x * spread, _baseScale.y, _baseScale.z * spread);
    }

    private void Paint(Color color)
    {
        if (_mesh == null) return;
        for (int i = 0; i < _colors.Length; i++) _colors[i] = color;
        _mesh.SetColors(_colors);
        _painted = true;
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (_renderer != null) _renderer.enabled = visible;
        if (_collider != null) _collider.enabled = visible;
    }

    private void OnDestroy()
    {
        if (_mesh != null) Destroy(_mesh);
    }
}
