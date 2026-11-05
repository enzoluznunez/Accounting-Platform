using System.IO;
using UnityEngine;

public static class StreamingAssets
{
    // On Android the folder is already a jar:// URL inside the APK; elsewhere it
    // is a plain path that UnityWebRequest needs as a file:// URL.
    public static string Url(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        return path.Contains("://") ? path : "file://" + path;
    }
}
