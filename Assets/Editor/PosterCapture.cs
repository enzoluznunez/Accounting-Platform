using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Renders the prototype poster views to PNG files.
//
// Both views are ordinary scene objects, so a capture is just a camera pointed
// at one: no play mode, no build. The objects are made and destroyed inside the
// call, which leaves whatever scene is open untouched.
//
// Anti-aliasing is supersampling rather than MSAA: the image is drawn at a
// multiple of the requested size and box-filtered down. It costs a little
// memory and is the same on every machine, which matters more here than speed
// because the output is a file that goes on a printed poster.
public static class PosterCapture
{
    private const string OutputDirectory = "poster";
    private const int Supersample = 3;

    // Transparent, so the figure drops onto whatever the poster's background
    // is; the white copies are there for previewing and for slides.
    private static readonly Color Transparent = new Color(1f, 1f, 1f, 0f);
    private static readonly Color White = Color.white;

    [MenuItem("Tools/Poster/Capture Prototype Views")]
    public static void CaptureAll()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(directory);

        CaptureNetwork(directory);
        CaptureSurface(directory);

        Debug.Log($"[PosterCapture] Wrote the prototype views to {directory}.");
    }

    // Called by the batch-mode invocation in the header comment of this file's
    // companion README line; kept separate so a failure exits non-zero.
    public static void CaptureAllBatch()
    {
        try
        {
            CaptureAll();
            EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PosterCapture] {e}");
            EditorApplication.Exit(1);
        }
    }

    // Square, as asked: the ring of ten nodes has no long axis, so any other
    // ratio would only pad it.
    private static void CaptureNetwork(string directory)
    {
        var host = new GameObject("PosterNetworkGraph");
        Material material = Material();
        try
        {
            var view = host.AddComponent<NetworkGraphView>();
            view.Build(material);

            // Perspective, unlike the surface: an orthographic camera draws a
            // near node and a far node the same size, which throws away the
            // depth the layout is carrying. A long lens keeps the convergence
            // mild enough that the ring still reads as a ring.
            Render(directory, "network-graph", 2048, 2048,
                   pivot: Vector3.zero,
                   euler: Vector3.zero,
                   orthographicSize: 1.38f,
                   fieldOfView: 30f);
        }
        finally
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(material);
        }
    }

    // Square, to match the network graph: the two hang together on the poster,
    // and a pair of figures in different ratios reads as two separate exhibits.
    // Viewed from three-quarters above, since a scoop read head-on loses the
    // very depth the figure is about.
    private static void CaptureSurface(string directory)
    {
        var host = new GameObject("PosterSurfaceGraph");
        Material material = Material();
        try
        {
            var view = host.AddComponent<SurfaceGraphView>();
            view.Build(material);

            Render(directory, "surface-graph", 2048, 2048,
                   pivot: new Vector3(0f, 0.86f, 0f),
                   euler: new Vector3(44f, -30f, 0f),
                   orthographicSize: 1.72f);
        }
        finally
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(material);
        }
    }

    // The app's own sheet material, on a throwaway copy with culling off: the
    // headset only ever sees a cube from outside, but a poster camera can catch
    // the underside of a surface, and a figure with holes in it is worse than
    // one that draws a few faces twice.
    private static Material Material()
    {
        var material = Resources.Load<Material>("Materials/SheetMaterial");
        if (material == null)
            throw new FileNotFoundException("Resources/Materials/SheetMaterial is missing.");

        var copy = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
        copy.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        return copy;
    }

    private static void Render(string directory, string name,
                               int width, int height, Vector3 pivot, Vector3 euler,
                               float orthographicSize, float fieldOfView = 0f)
    {
        Write(Path.Combine(directory, name + ".png"),
              Draw(width, height, pivot, euler, orthographicSize, fieldOfView, Transparent));
        Write(Path.Combine(directory, name + "-white.png"),
              Draw(width, height, pivot, euler, orthographicSize, fieldOfView, White));
    }

    // The camera is placed by backing away from the pivot along its own view
    // direction, so changing the angle reframes nothing: an orthographic camera
    // keeps the pivot dead centre whatever distance it sits at.
    // A field of view of zero means an orthographic camera; anything else is a
    // perspective one, framed so that the pivot plane spans what the same
    // orthographic size would have.
    private static Texture2D Draw(int width, int height,
                                  Vector3 pivot, Vector3 euler,
                                  float orthographicSize, float fieldOfView,
                                  Color background)
    {
        var cameraObject = new GameObject("PosterCamera");
        RenderTexture target = null;
        Texture2D full = null;

        try
        {
            bool perspective = fieldOfView > 0f;
            float distance = perspective
                ? orthographicSize / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad)
                : 10f;

            Quaternion facing = Quaternion.Euler(euler);
            cameraObject.transform.SetPositionAndRotation(pivot - facing * Vector3.forward * distance, facing);

            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = !perspective;
            camera.orthographicSize = orthographicSize;
            camera.fieldOfView = perspective ? fieldOfView : 60f;
            camera.aspect = (float)width / height;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 200f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.cullingMask = ~0;

            var data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.antialiasing = AntialiasingMode.None;

            target = new RenderTexture(width * Supersample, height * Supersample, 24,
                                       RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.Render();

            full = ReadBack(target);
            return Downsample(full, width, height);
        }
        finally
        {
            if (full != null) Object.DestroyImmediate(full);
            if (target != null)
            {
                RenderTexture.active = null;
                target.Release();
                Object.DestroyImmediate(target);
            }
            Object.DestroyImmediate(cameraObject);
        }
    }

    private static Texture2D ReadBack(RenderTexture target)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;

        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
        texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        texture.Apply();

        RenderTexture.active = previous;
        return texture;
    }

    // A box filter over each supersample block. Colour is averaged weighted by
    // alpha, so the transparent copy's edge pixels do not pick up the colour of
    // the cleared background and fringe on a dark poster.
    private static Texture2D Downsample(Texture2D source, int width, int height)
    {
        Color[] pixels = source.GetPixels();
        var result = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        var output = new Color[width * height];

        int block = Supersample;
        float count = block * block;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f;

                for (int sy = 0; sy < block; sy++)
                {
                    int row = (y * block + sy) * source.width;
                    for (int sx = 0; sx < block; sx++)
                    {
                        Color p = pixels[row + x * block + sx];
                        r += p.r * p.a; g += p.g * p.a; b += p.b * p.a; a += p.a;
                    }
                }

                output[y * width + x] = a > 0f
                    ? new Color(r / a, g / a, b / a, a / count)
                    : new Color(0f, 0f, 0f, 0f);
            }
        }

        result.SetPixels(output);
        result.Apply();
        return result;
    }

    private static void Write(string path, Texture2D texture)
    {
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
    }
}
