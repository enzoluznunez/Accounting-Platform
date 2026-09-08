using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class Parser : DataSource
{
    public Action<bool, string> onLoadResult;

    private void ReportResult(bool ok, string reason) => onLoadResult?.Invoke(ok, reason);

    private enum SourceKind { StreamingAsset, Url, Inline }

    public void Load(string source)
    {
        switch (Classify(source))
        {
            case SourceKind.Url:    StartCoroutine(LoadFromWebRequest(source)); break;
            case SourceKind.Inline: LoadFromCsvText(StripDataUri(source)); break;
            default:                StartCoroutine(LoadFromStreamingAssets(source)); break;
        }
    }

    private static SourceKind Classify(string source)
    {
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SourceKind.Url;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            source.IndexOf('\n') >= 0)
            return SourceKind.Inline;
        return SourceKind.StreamingAsset;
    }

    private static string StripDataUri(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return source;
        int comma = source.IndexOf(',');
        return comma >= 0 ? source.Substring(comma + 1) : source;
    }

    private System.Collections.IEnumerator LoadFromStreamingAssets(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        string url = path.Contains("://") ? path : "file://" + path;
        return LoadFromWebRequest(url);
    }

    private System.Collections.IEnumerator LoadFromWebRequest(string url)
    {
        using (UnityEngine.Networking.UnityWebRequest www = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Parser:{name}] Failed to load CSV from {url}: {www.error}");
                Fail("The QR code's link could not be reached.");
                yield break;
            }

            LoadFromCsvText(www.downloadHandler.text);
        }
    }

    public void LoadFromCsvText(string csvText)
    {
        ClearGrid();
        _rawText = csvText;

        if (LooksLikeHtml(csvText))
        {
            Fail("The QR code's link returned a web page, not a dataset.");
            return;
        }

        string[] lines = csvText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        int groupSize = 1;
        List<List<string>> grid = new List<List<string>>(lines.Length);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (grid.Count == 0 && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                ReadDirective(line, ref groupSize);
                continue;
            }
            grid.Add(ParseCSVLine(line));
        }

        if (grid.Count < 2)
        {
            Fail("The QR code's data has no data rows.");
            return;
        }

        List<string> header = grid[0];
        if (header.Count > 0) SetAxisTitles(header[0]);
        for (int c = 1; c < header.Count; c++)
            _columnTitles.Add(header[c].Trim());

        if (groupSize > 1 && _columnTitles.Count % groupSize != 0)
        {
            Debug.LogWarning($"[Parser:{name}] '#group {groupSize}' does not divide the " +
                             $"{_columnTitles.Count} columns; loading them ungrouped.");
            groupSize = 1;
        }
        SetColumnGroupSize(groupSize);

        int skipped = 0;
        for (int g = grid.Count - 1; g >= 1; g--)
        {
            if (grid[g].Count >= header.Count) continue;
            grid.RemoveAt(g);
            skipped++;
        }
        if (skipped > 0)
            Debug.LogWarning($"[Parser:{name}] Skipped {skipped} row(s) shorter than the {header.Count}-column header.");

        int rowCount = grid.Count - 1;
        int colCount = _columnTitles.Count;

        if (rowCount == 0 || colCount == 0)
        {
            Fail("The QR code's data has no rows or columns.");
            return;
        }

        for (int r = 0; r < rowCount; r++)
        {
            List<string> fields = grid[r + 1];
            _rowTitles.Add(fields.Count > 0 ? fields[0].Trim() : "");
        }

        int numericCells = FillGrid(rowCount, colCount, (r, c) =>
        {
            List<string> fields = grid[r + 1];
            string field = (c + 1 < fields.Count) ? fields[c + 1] : null;
            return TryParseFloat(field, out float v) ? v : (float?)null;
        });

        if (numericCells == 0)
        {
            Fail("The QR code's data has no numeric values.");
            return;
        }

        EnsureOrders();

        Debug.Log($"[Parser:{name}] Loaded grid {rowCount} rows x {colCount} cols " +
                  $"({ColumnGroupSize} col(s) per group, normalised range [{_globalMin}, {_globalMax}])");
        RaiseDataLoaded();
        ReportResult(true, null);
    }

    private void Fail(string reason)
    {
        Debug.LogWarning($"[Parser:{name}] {reason}");
        ClearGrid();
        EnsureOrders();
        RaiseLoadFailed();
        ReportResult(false, reason);
    }

    private static bool LooksLikeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length && text[i] == '<';
    }

    // Directive lines sit above the header and configure the grid. Only
    // '#group N' is understood; anything else is ignored so a file may carry
    // comments without becoming a row.
    private void ReadDirective(string line, ref int groupSize)
    {
        string body = line.TrimStart().TrimStart('#').Trim();
        if (!body.StartsWith("group", StringComparison.OrdinalIgnoreCase)) return;

        string value = body.Substring("group".Length).Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size > 1)
        {
            groupSize = size;
            return;
        }
        Debug.LogWarning($"[Parser:{name}] Ignored unreadable directive '{line.Trim()}'.");
    }

    private void SetAxisTitles(string corner)
    {
        if (string.IsNullOrWhiteSpace(corner)) return;

        int sep = corner.IndexOf('\\');
        if (sep < 0) sep = corner.IndexOf('/');

        if (sep >= 0)
        {
            _rowAxisTitle = CleanAxisTitle(corner.Substring(0, sep));
            _columnAxisTitle = CleanAxisTitle(corner.Substring(sep + 1));
        }
        else
        {
            _rowAxisTitle = CleanAxisTitle(corner);
        }
    }

    private static string CleanAxisTitle(string value)
    {
        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private void ClearGrid()
    {
        _columnTitles.Clear();
        _rowTitles.Clear();
        _columnAxisTitle = null;
        _rowAxisTitle = null;
        _values = new float[0, 0];
        _valid = new bool[0, 0];
        _columnOrder.Clear();
        _rowOrder.Clear();
        _rawText = null;
        _globalMin = 0f;
        _globalMax = 1f;
        SetColumnGroupSize(1);
    }

    private static List<string> ParseCSVLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var sb = new System.Text.StringBuilder(64);

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static bool TryParseFloat(string value, out float result)
    {
        result = 0f;
        return !string.IsNullOrEmpty(value) &&
               float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
