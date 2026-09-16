using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;
using UnityEngine;

public static class FinancialsApi {

    // Where the database is. IndustryCatalog overwrites this at startup from
    // StreamingAssets/api.url, so this default only stands when that file is
    // missing — and on a headset it cannot answer, which is the point: an
    // unconfigured build fails loudly at launch rather than halfway through a
    // conversation.
    public static string BaseUrl = "http://127.0.0.1:8000";

    public const string Unreachable = "The financial database could not be reached.";

    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    // A reply the server chose to send: its status and the text of its 'detail'.
    public sealed class ApiError : Exception {
        public readonly int Status;
        public ApiError(int status, string detail) : base(detail) { Status = status; }
    }

    public static async Task<string> Get(string path) {
        HttpResponseMessage response = await http.GetAsync(BaseUrl.TrimEnd('/') + path).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ApiError((int)response.StatusCode, Detail(body));
        return body;
    }

    // What the model should be told about a failed call: the server's own
    // words when it rejected the request, one generic line for anything else.
    public static string Explain(Exception e) =>
        e is ApiError api && api.Status < 500 ? api.Message : Unreachable;

    // Runs one GET and hands the parsed body to fill, so every read-only tool
    // shares the same error path and the same 'ok' marker.
    public static async Task<Dictionary<string, object>> Fetch(string tag, string path,
        Action<JsonElement, Dictionary<string, object>> fill) {
        var result = new Dictionary<string, object>();
        try {
            string json = await Get(path).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            fill(doc.RootElement, result);
            result["ok"] = true;
        }
        catch (Exception e) {
            Debug.LogWarning($"[{tag}] {e.Message}");
            result["error"] = Explain(e);
        }
        return result;
    }

    public static List<object> Strings(JsonElement array) {
        var list = new List<object>();
        foreach (JsonElement item in array.EnumerateArray()) list.Add(item.GetString());
        return list;
    }

    // FastAPI wraps its error text as {"detail": ...}; anything else is passed on, cut short.
    private static string Detail(string body) {
        try {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out JsonElement detail) &&
                detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
        }
        catch (JsonException) { }
        return string.IsNullOrEmpty(body) ? "" : body.Length <= 200 ? body : body.Substring(0, 200);
    }
}

public sealed class ListIndustries : Function {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListIndustries",
        Description = "List the industries the financial database holds. There are ten, and they are the same " +
                      "ten the datasets in the room are drawn from, so an industry means one thing wherever " +
                      "it is named. Each " +
                      "comes with how many companies it holds and how many SIC codes it covers. These are not " +
                      "open datasets: call OpenIndustrySheet with an industry's name to bring one into the room " +
                      "as a sheet. Call this first when the user names an industry or asks what data there is."
    };

    protected override Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) =>
        FinancialsApi.Fetch("ListIndustries", "/industries", (root, result) => {
            var list = new List<object>();
            foreach (JsonElement row in root.GetProperty("industries").EnumerateArray())
                list.Add(new Dictionary<string, object> {
                    { "industry", row.GetProperty("division").GetString() },
                    { "companies", row.GetProperty("companies").GetInt32() },
                    { "sicCodes", row.GetProperty("sic_codes").GetInt32() }
                });
            result["industries"] = list;
        });
}

public sealed class OpenIndustrySheet : AgenticTool {

    public class Args {
        [Optional]
        [Doc("The industry to open, spelled as ListIndustries gives it. Leave it out, and leave out 'sic' " +
             "too, to open one sheet spanning every industry.")]
        public string industry;

        [Optional]
        [Doc("A SIC code, to open one narrower slice from inside an industry rather than the whole of it. " +
             "Give this or 'industry', not both.")]
        public int? sic;

        [Optional]
        [Doc("Which ratios to show, comma separated, from the list ListRatios gives. " +
             "Six or fewer keeps the sheet readable. Leave empty for a general set.")]
        public string metrics;

        [Optional]
        [Doc("Kinds of ratio to show instead of naming them one by one \u2014 liquidity, efficiency, " +
             "solvency, profitability or valuation, comma separated. Giving this decides the columns, " +
             "so do not also give 'metrics'.")]
        public string categories;

        [Optional]
        [Doc("On a sheet spanning every industry, how many companies each industry contributes. " +
             "Defaults to three. Ignored when one industry is named.")]
        public int? per;

        [Optional]
        [Doc("Filters on reported figures, each written as field:comparison:number, for example " +
             "'revenues:gt:1000'. Call ListFields for the field names, the comparisons and the units. " +
             "Several filters all have to hold. They choose which companies reach the sheet; they never " +
             "change which ratios it shows.")]
        public string[] where;

        [Optional]
        [Doc("Whether the filters must hold in every year ('all') or in at least one of them ('any'). " +
             "Defaults to 'any'.")]
        public string match;

        [Optional,
         Limits(FinancialsContract.LimitMinimum, FinancialsContract.LimitMaximum),
         DefaultsTo(FinancialsContract.LimitDefault)]
        [Doc("How many companies to show, largest first.")]
        public int? limit;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "OpenIndustrySheet",
        Description = "Open one industry from the financial database as a new sheet in the room, with every " +
                      "company's ratios for " + string.Join(" and ", FinancialsContract.Years) +
                      " side by side so the change across COVID is visible. " +
                      "Each ratio is one metric holding two bars, one per year. " +
                      "Name an industry to open that one, or name none to open a sheet spanning every " +
                      "industry, a few companies from each. Call ListIndustries first for the names. " +
                      "Give 'where' to bring in only the companies that meet a condition, and 'categories' to " +
                      "show one kind of ratio. This adds a dataset; it does not replace the open one, " +
                      "and ListDatasets will show it afterwards.",
        Parameters = ParametersFor(typeof(Args))
    };

    // The download happens off the main thread in Execute; Run, on the main
    // thread, only hands the finished sheet to the scene.
    private string csv;
    private string label;

    protected override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var bound = ToolArguments.Bind(typeof(Args), args, out string bindError) as Args;
        if (bound == null) return new Dictionary<string, object> { { "error", bindError } };

        bool named = !string.IsNullOrWhiteSpace(bound.industry);
        if (named && bound.sic.HasValue)
            return Fail("Name one industry, not two: 'industry' as ListIndustries gives it, " +
                        "or 'sic' for a narrower slice inside one, or neither for every industry.");

        if (!string.IsNullOrWhiteSpace(bound.categories) && !string.IsNullOrWhiteSpace(bound.metrics))
            return Fail("Choose the columns one way: 'categories' for kinds of ratio, " +
                        "or 'metrics' for particular ones.");

        string groups = "";
        if (!string.IsNullOrWhiteSpace(bound.categories))
            foreach (string requested in bound.categories.Split(',')) {
                string kind = requested.Trim();
                if (kind.Length == 0) continue;
                if (!FinancialsContract.MetricCategories.ContainsKey(kind))
                    return Fail($"'{kind}' is not a kind of ratio; ListRatios groups them by kind.");
                groups += (groups.Length == 0 ? "" : ",") + kind;
            }

        // The generated contract holds the same names the server validates
        // against, so a bad ratio or a bad filter is caught here instead of
        // costing a round trip.
        if (!string.IsNullOrWhiteSpace(bound.metrics))
            foreach (string requested in bound.metrics.Split(',')) {
                string name = requested.Trim();
                if (name.Length == 0) continue;
                if (Array.IndexOf(FinancialsContract.Metrics, name) < 0)
                    return Fail($"'{name}' is not a ratio this database holds; ListRatios has the names.");
            }

        string filters = "";
        if (bound.where != null)
            foreach (string clause in bound.where) {
                if (string.IsNullOrWhiteSpace(clause)) continue;
                string bad = Reject(clause.Trim());
                if (bad != null) return Fail(bad);
                filters += "&where=" + Uri.EscapeDataString(clause.Trim());
            }

        if (!string.IsNullOrWhiteSpace(bound.match) && bound.match != "any" && bound.match != "all")
            return Fail($"'{bound.match}' is not a way to match; use 'any' or 'all'.");

        label = named ? bound.industry.Trim()
              : bound.sic.HasValue ? $"SIC {bound.sic.Value}"
              : "All Industries";

        string query = named
            ? "/sheet?division=" + Uri.EscapeDataString(bound.industry.Trim())
            : bound.sic.HasValue ? $"/sheet?sic={bound.sic.Value}"
            : "/sheet?";
        query += $"&limit={bound.limit ?? FinancialsContract.LimitDefault}";
        if (groups.Length > 0) query += "&categories=" + Uri.EscapeDataString(groups);
        else if (!string.IsNullOrWhiteSpace(bound.metrics))
            query += "&metrics=" + Uri.EscapeDataString(bound.metrics.Trim());
        if (!named && !bound.sic.HasValue && bound.per.HasValue) query += $"&per={bound.per.Value}";
        query += filters;
        if (!string.IsNullOrWhiteSpace(bound.match)) query += "&match=" + bound.match;

        // A sheet spanning every industry names no scope, so the first
        // parameter would otherwise follow the '?' with a stray separator.
        query = query.Replace("?&", "?");

        try {
            csv = await FinancialsApi.Get(query).ConfigureAwait(false);
        }
        catch (Exception e) {
            Debug.LogWarning($"[OpenIndustrySheet] {e.Message}");
            string why = e is FinancialsApi.ApiError { Status: 404 }
                ? (bound.where != null && bound.where.Length > 0
                    ? e.Message
                    : $"The database holds no companies for {label}. Call ListIndustries for the industries that exist.")
                : FinancialsApi.Explain(e);
            return Fail(why);
        }

        return await base.Execute(args).ConfigureAwait(false);
    }

    private static Dictionary<string, object> Fail(string why) =>
        new Dictionary<string, object> { { "error", why } };

    // Why a filter cannot be sent, or null when it can. The field names and the
    // comparisons are the generated ones, so this refuses exactly what the
    // server would have refused.
    private static string Reject(string clause) {
        string[] parts = clause.Split(':');
        if (parts.Length != 3)
            return $"'{clause}' is not a filter; write it as field:comparison:number, " +
                   "like revenues:gt:1000. ListFields has the field names.";

        string field = parts[0].Trim(), op = parts[1].Trim(), value = parts[2].Trim();
        if (Array.IndexOf(FinancialsContract.FilterFields, field) < 0)
            return Array.IndexOf(FinancialsContract.Metrics, field) >= 0
                ? $"'{field}' is a ratio the sheet draws, not a figure a filter can name. " +
                  "ListFields has the ones that can be filtered on."
                : $"'{field}' is not a field this database can filter on; ListFields has the names.";
        if (Array.IndexOf(FinancialsContract.FilterOperators, op) < 0)
            return $"'{op}' is not a comparison; use one of {string.Join(", ", FinancialsContract.FilterOperators)}.";
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out _))
            return $"'{value}' is not a number, so {field} cannot be compared to it.";
        return null;
    }

    protected override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        // The tool is a long-lived singleton, so the download is handed over and
        // let go of here rather than pinned until the next call replaces it.
        string sheet = csv, name = label;
        csv = null;
        label = null;

        var datasets = Scene.Datasets;
        if (datasets == null) { result["error"] = "No dataset manager in the scene."; return; }

        datasets.AddDataset(sheet, name);

        // Inline CSV parses synchronously and a successful load switches to the
        // new dataset, so the parser's own shape is ready to report.
        DataSource data = datasets.Active;
        if (data == null || !data.IsLoaded || datasets.ActiveDataset.label != name) {
            result["error"] = "The sheet came back but could not be read as a dataset.";
            return;
        }

        result["opened"] = name;
        result["companies"] = data.RowCount;
        result["metrics"] = data.GroupCount(true);
        result["years"] = new List<object>(data.SeriesTitles);
        result["note"] = "Its rows are companies and each metric holds one column per year.";
    }
}

public sealed class ListRatios : Function {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListRatios",
        Description = "List the financial ratios the database can put on a sheet, grouped by what they measure, " +
                      "and which ones OpenIndustrySheet shows when you name none. Call it when the user asks for " +
                      "a ratio by name and you need its exact spelling, or asks for a kind of ratio such as the " +
                      "liquidity or profitability ones."
    };

    protected override Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) =>
        FinancialsApi.Fetch("ListRatios", "/ratios", (root, result) => {
            result["ratios"] = FinancialsApi.Strings(root.GetProperty("ratios"));
            result["default"] = FinancialsApi.Strings(root.GetProperty("default"));

            var groups = new Dictionary<string, object>();
            foreach (JsonProperty category in root.GetProperty("categories").EnumerateObject())
                groups[category.Name] = FinancialsApi.Strings(category.Value);
            result["categories"] = groups;
        });
}

public sealed class ListFields : Function {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListFields",
        Description = "List the reported figures OpenIndustrySheet's 'where' can filter on \u2014 assets, " +
                      "liabilities, revenues and the rest \u2014 with the unit each is in and the range the " +
                      "database actually holds. These are the figures the ratios were computed from; a sheet " +
                      "never draws them. Call this before writing a filter, because " + FinancialsContract.FilterUnits
    };

    protected override Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) =>
        FinancialsApi.Fetch("ListFields", "/fields", (root, result) => {
            var list = new List<object>();
            foreach (JsonElement field in root.GetProperty("fields").EnumerateArray())
                list.Add(new Dictionary<string, object> {
                    { "field", field.GetProperty("name").GetString() },
                    { "unit", field.GetProperty("unit").GetString() },
                    { "lowest", Number(field, "minimum") },
                    { "highest", Number(field, "maximum") }
                });
            result["fields"] = list;
            result["comparisons"] = FinancialsApi.Strings(root.GetProperty("operators"));
            result["units"] = root.GetProperty("note").GetString();
            result["example"] = root.GetProperty("example").GetString();
        });

    private static object Number(JsonElement field, string name) =>
        field.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (object)value.GetDouble()
            : null;
}
