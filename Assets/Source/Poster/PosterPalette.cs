using UnityEngine;

// The ten division colours the pipeline paints rows with, restated here so a
// poster figure and a sheet in the headset name the same industry with the same
// colour. The source of truth is pipeline/metrics.py (DIVISION_COLORS); this is
// a copy kept in the order that file lists them.
public static class PosterPalette
{
    public static readonly string[] Names =
    {
        "Agriculture",
        "Mining",
        "Construction",
        "Manufacturing",
        "Transportation & Public Utilities",
        "Wholesale Trade",
        "Retail Trade",
        "Finance, Insurance, Real Estate",
        "Services",
        "Public Administration",
    };

    private static readonly string[] Hex =
    {
        "#2a78d6", "#eb6834", "#12a3b4", "#e34948", "#9b4dca",
        "#1baf7a", "#eda100", "#4a3aa7", "#008300", "#e87ba4",
    };

    public static readonly Color[] Colors = Build();

    public static int Count => Colors.Length;

    public static Color At(int index) =>
        Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

    // The same ten colours, reordered darkest to lightest.
    //
    // The catalogue order is arbitrary -- it names industries, and nothing about
    // blue-then-orange-then-teal says which is more. Used raw as a depth scale
    // it gives a viewer no way to tell a deep band from a shallow one without
    // reading a legend. Sorted by luminance the same ten read as one ramp, so
    // dark is low and light is high before anything is labelled.
    public static readonly Color[] Ramp = BuildRamp();

    // A continuous 0..1 reading quantised onto the ten colours, so a surface
    // reads as bands a viewer can count rather than as a gradient they must
    // eyeball. Dark at 0, light at 1.
    public static Color Band(float t)
    {
        int index = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(t) * Ramp.Length), 0, Ramp.Length - 1);
        return Ramp[index];
    }

    // Rec. 709 luminance on the sRGB values, which is what the eye sorts these
    // by on a printed page.
    private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    private static Color[] BuildRamp()
    {
        var ramp = (Color[])Colors.Clone();
        System.Array.Sort(ramp, (a, b) => Luminance(a).CompareTo(Luminance(b)));
        return ramp;
    }

    private static Color[] Build()
    {
        var colors = new Color[Hex.Length];
        for (int i = 0; i < Hex.Length; i++)
            colors[i] = ColorUtility.TryParseHtmlString(Hex[i], out Color c) ? c : Color.magenta;
        return colors;
    }
}
