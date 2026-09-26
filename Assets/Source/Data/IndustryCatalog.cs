using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Networking;

// Lists what the financial database holds, at startup, without reading any of
// it. The app ships no sheets: /industries states which industries exist, and
// each becomes a listed dataset whose payload is the /sheet URL that draws it,
// fetched the first time it is opened. So a listed industry is always the
// database as it stands now rather than an export of how it once stood.
public class IndustryCatalog : MonoBehaviour
{
    public ManageDatasets manageDatasets;

    [Tooltip("One-line file inside StreamingAssets holding the API's base URL. " +
             "Absent or empty, the built-in default is used.")]
    public string apiUrlFile = "api.url";

    [Tooltip("Label for the one sheet that spans every industry.")]
    public string allIndustriesLabel = "All Industries";

    // Every ratio, named as its five categories rather than as eighteen ratios,
    // so a ratio added server-side arrives on these sheets without an edit here.
    private const string AllCategories = "liquidity,efficiency,solvency,profitability,valuation";

    // The shape of the sheets this lists. Both go into the URL and into the
    // company count reported for an unopened dataset, so the count cannot
    // disagree with what opening it draws. The limit comes from the generated
    // contract, so the server's default is the single place it is set. The
    // per-industry share is the app's own: the sheet spanning every industry is
    // what the app opens on, and it opens on a sample — two from each — rather
    // than the server's default, which the assistant's own queries still get.
    private const int SheetLimit = FinancialsContract.LimitDefault;
    private const int PerIndustry = 2;

    private void Start()
    {
        if (manageDatasets == null) manageDatasets = GetComponent<ManageDatasets>();
        if (manageDatasets == null) manageDatasets = FindAnyObjectByType<ManageDatasets>();

        if (manageDatasets == null)
        {
            Debug.LogError("[IndustryCatalog] No ManageDatasets in the scene; no industry will be listed.");
            return;
        }

        StartCoroutine(Bootstrap());
    }

    private IEnumerator Bootstrap()
    {
        yield return ReadApiUrl();
        yield return ListIndustries();
    }

    // Where the database is, is configuration and not data: one line in
    // StreamingAssets, so a user points the app at their own machine without
    // editing a source file and rebuilding.
    private IEnumerator ReadApiUrl()
    {
        if (string.IsNullOrEmpty(apiUrlFile)) yield break;

        string path = Path.Combine(Application.streamingAssetsPath, apiUrlFile);
        string url = path.Contains("://") ? path : "file://" + path;

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[IndustryCatalog] No '{apiUrlFile}' in StreamingAssets; " +
                                 $"falling back to {FinancialsApi.BaseUrl}.");
                yield break;
            }

            string configured = www.downloadHandler.text.Trim();
            if (configured.Length == 0)
            {
                Debug.LogWarning($"[IndustryCatalog] '{apiUrlFile}' holds no URL; " +
                                 $"falling back to {FinancialsApi.BaseUrl}.");
                yield break;
            }

            FinancialsApi.BaseUrl = configured;
        }

        Debug.Log($"[IndustryCatalog] Financial database at {FinancialsApi.BaseUrl}.");
    }

    private IEnumerator ListIndustries()
    {
        string url = FinancialsApi.BaseUrl.TrimEnd('/') + "/industries";

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[IndustryCatalog] The financial database at {url} could not be reached " +
                               $"({www.error}); the app starts with nothing listed.");
                Notices.Show(this, "No Data", FinancialsApi.Unreachable);
                yield break;
            }

            Register(www.downloadHandler.text);
        }
    }

    private void Register(string json)
    {
        List<Row> rows;
        try
        {
            rows = Read(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[IndustryCatalog] /industries answered with something unreadable: {e.Message}");
            Notices.Show(this, "No Data", FinancialsApi.Unreachable);
            return;
        }

        if (rows.Count == 0)
        {
            Debug.LogWarning("[IndustryCatalog] The database holds no industries; nothing is listed.");
            Notices.Show(this, "No Data", "The financial database holds no industries.");
            return;
        }

        // The rail draws the newest entry at the top, so the reply — biggest
        // industry first — is walked backwards to arrive in that order, and the
        // sheet spanning every industry is added last to sit above them all. The
        // whole catalogue goes in as one batch: the rail rebuilds itself on
        // every change.
        var entries = new List<(string, string, int)>(rows.Count + 1);
        int spanning = 0;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            entries.Add((SheetUrl(rows[i].division), rows[i].division,
                         Math.Min(rows[i].companies, SheetLimit)));

            // An industry with fewer companies than the per-industry share
            // contributes all it has and no more, so the sheet spanning them is
            // shorter than the share times the count.
            spanning += Math.Min(rows[i].companies, PerIndustry);
        }
        string spanningUrl = SheetUrl(null);
        entries.Add((spanningUrl, allIndustriesLabel, Math.Min(spanning, SheetLimit)));

        manageDatasets.AddCatalogEntries(entries);

        Debug.Log($"[IndustryCatalog] Listed {rows.Count} industries and one sheet spanning them; " +
                  "opening the spanning sheet, the rest are fetched when asked for.");

        OpenAtStartup(spanningUrl);
    }

    // The app opens on the sheet spanning every industry, a few companies from
    // each, so there is something to look at before anyone asks for anything.
    // Switching to an unread entry fetches it and switches once it is read.
    private void OpenAtStartup(string url)
    {
        if (manageDatasets.ActiveIndex >= 0) return;

        var datasets = manageDatasets.Datasets;
        for (int i = 0; i < datasets.Count; i++)
        {
            if (datasets[i].payload != url) continue;
            manageDatasets.SwitchDataset(i);
            return;
        }
    }

    // The request that draws one industry, or every industry when none is named.
    // A cross-industry sheet takes a few companies from each rather than the
    // largest overall, so that no industry is missing from it.
    private static string SheetUrl(string division)
    {
        string query = $"/sheet?categories={AllCategories}&limit={SheetLimit}";
        return FinancialsApi.BaseUrl.TrimEnd('/') + (division == null
            ? query + $"&per={PerIndustry}"
            : query + $"&division={Uri.EscapeDataString(division)}");
    }

    private struct Row
    {
        public string division;
        public int companies;
    }

    private static List<Row> Read(string json)
    {
        var rows = new List<Row>();
        if (string.IsNullOrEmpty(json)) return rows;

        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement row in doc.RootElement.GetProperty("industries").EnumerateArray())
        {
            string division = row.GetProperty("division").GetString();
            if (string.IsNullOrEmpty(division)) continue;

            rows.Add(new Row
            {
                division = division,
                companies = row.GetProperty("companies").GetInt32()
            });
        }
        return rows;
    }
}
