using UnityEngine;

// Prototype poster figure: a scooped surface over a two-variable floor.
//
// The floor's two axes are the first two variables and depth is the third. The
// fourth is colour, and here it is depth itself: the ten division colours are
// used as contour steps, so a band edge is a level line and the eye reads how
// far the scoop falls by counting bands rather than by trusting the shading
// alone. A bowl seen from above foreshortens badly, and colour is what keeps it
// from flattening into a disc on a printed page.
//
// Each cell is flat-shaded from its own centre, which keeps the band edges
// crisp instead of smearing one band into the next across a quad.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SurfaceGraphView : MonoBehaviour
{
    [Tooltip("Cells along each axis of the floor.")]
    public int cells = 288;

    [Tooltip("Floor extent in world units, along x and along z.")]
    public Vector2 extent = new Vector2(2.4f, 2.0f);

    [Tooltip("World height from the floor of the scoop to its highest lip.")]
    public float height = 1.05f;

    public void Build(Material material)
    {
        var mesh = new PosterMesh();
        int n = Mathf.Max(cells, 2);

        for (int ix = 0; ix < n; ix++)
        {
            for (int iz = 0; iz < n; iz++)
            {
                float u0 = (float)ix / n, u1 = (float)(ix + 1) / n;
                float v0 = (float)iz / n, v1 = (float)(iz + 1) / n;

                Vector3 a = Point(u0, v0);
                Vector3 b = Point(u1, v0);
                Vector3 c = Point(u1, v1);
                Vector3 d = Point(u0, v1);

                Vector3 normal = Vector3.Cross(c - a, d - b).normalized;
                if (normal.y < 0f) normal = -normal;

                float depth = Depth((u0 + u1) * 0.5f, (v0 + v1) * 0.5f);
                Color color = PosterPalette.Band(depth);
                mesh.AddQuad(a, b, c, d, PosterMesh.Shade(color, normal, Exposure(depth)));
            }
        }

        var filter = GetComponent<MeshFilter>();
        filter.sharedMesh = mesh.Build("SurfaceGraph");
        GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    private Vector3 Point(float u, float v) =>
        new Vector3((u - 0.5f) * extent.x, Depth(u, v) * height, (v - 0.5f) * extent.y);

    // The scoop: a crater rather than a parabola. A bowl whose walls are a
    // simple square curve has no floor and no lip, so every contour looks the
    // same and the eye has nothing to hang depth on. Smoothstep gives the three
    // parts a scoop actually has -- a flat floor, a steep wall, a flattening
    // lip -- and equal steps in depth then land as wide rings on the floor,
    // tight rings up the wall and wide rings again at the lip, which is itself
    // a reading of how fast the surface is falling.
    //
    // The tilt lifts one lip above the other so the figure has a direction to
    // be read down. Returns 0 at the floor, 1 at the highest lip.
    private static float Depth(float u, float v)
    {
        float dx = (u - 0.5f) * 2f;
        float dz = (v - 0.5f) * 2f;

        float r = Mathf.Sqrt(dx * dx + 0.9f * dz * dz) / 1.34f;
        float wall = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(r));
        return Mathf.Clamp01(0.90f * wall + 0.05f * dx + 0.02f * dz);
    }

    // How open to the sky a point on the scoop is. The floor sees least of it,
    // so it is darkest; the lip sees all of it. Crude next to a real occlusion
    // term, and enough: it is the cue that says hollow rather than mound.
    private static float Exposure(float depth) => Mathf.Lerp(0.46f, 1f, depth);
}
