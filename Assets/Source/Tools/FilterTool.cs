using System.Collections.Generic;
using UnityEngine;

// Chooses what stands on the sheet, on either axis: which companies and which
// metrics. A metric is one column group, so hiding one takes both of its years
// off together, and a company is one row. The arrangement underneath is
// untouched either way, so showing something again puts it back where the sort
// left it.
//
// The panel shows one axis at a time. Neither is open to begin with, which is
// not the same as nothing being filtered: the filter stands whether or not the
// list that sets it is on screen.
public class FilterTool : Tool
{
    protected override ToolType Kind => ToolType.Filter;

    // A fingertip on a bar takes that bar's line off the sheet. Which line it is
    // comes from the axis the panel has open, because one poke gives a point and
    // not a direction: with the metrics open a poke takes the metric, with the
    // companies open it takes the company.
    protected override bool UsesSheetEvents => true;

    public enum Axis { None, Company, Metric }

    private Axis _open = Axis.None;

    // One entry per metric: the data-space group it stands for, and the row that
    // shows it. Kept together so the two can never fall out of step.
    private readonly List<(int group, UIButton.Handle handle)> _metrics =
        new List<(int, UIButton.Handle)>();

    // The same for companies, by data-space row.
    private readonly List<(int row, UIButton.Handle handle)> _companies =
        new List<(int, UIButton.Handle)>();

    private DataSource _watched;
    private DataSource _builtFrom;

    private static DataSource Data => Scene.Data;

    protected override void OnToolStart()
    {
        if (ManageDatasets.Instance != null)
            ManageDatasets.Instance.OnActiveDatasetChanged += OnDatasetChanged;
    }

    protected override void OnToolDestroy()
    {
        if (ManageDatasets.Instance != null)
            ManageDatasets.Instance.OnActiveDatasetChanged -= OnDatasetChanged;
        Watch(null);
    }

    protected override void BuildPanelContent() => Refresh();

    protected override void OnActiveChanged(bool active)
    {
        if (active) Refresh();
    }

    // Undo All resets every tool: the filter it clears is the whole of this
    // tool's state, so the sheet comes back whole.
    protected override void OnResetTool()
    {
        DataSource data = Data;
        if (data == null) { Light(); return; }

        bool metrics = data.ClearHiddenGroups();
        bool companies = data.ClearHiddenRows();

        if (metrics && companies) Report("put every company and every metric back on the sheet");
        else if (metrics) Report("showed every metric again");
        else if (companies) Report("showed every company again");

        Light();
    }

    private void OnDatasetChanged(int index) => Refresh();

    private void Watch(DataSource data)
    {
        if (_watched == data) return;

        if (_watched != null)
        {
            _watched.OnDataLoaded -= Refresh;
            _watched.OnOrderChanged -= Light;
        }
        _watched = data;
        if (_watched != null)
        {
            _watched.OnDataLoaded += Refresh;
            _watched.OnOrderChanged += Light;
        }
    }

    // The rows follow the dataset; their squares follow the filter, which an undo
    // or the assistant can change without going through this tool.
    private void Refresh()
    {
        DataSource data = Data;
        Watch(data);

        // Two datasets can hold the same number of metrics under different names,
        // so the lists are rebuilt for a new source even when the counts match.
        // A build that produced nothing counts as no build at all: the panel was
        // not ready, and a later Refresh has to try again rather than read the
        // empty lists as up to date.
        bool built = _axisRow != null;
        if (built && data == _builtFrom && _openBuilt == _open) { Light(); return; }

        _builtFrom = data;
        _openBuilt = _open;
        _metrics.Clear();
        _companies.Clear();

        if (toolPanelUI == null) { _axisRow = null; return; }

        // The two axes read as one choice, so they sit side by side above
        // whatever they open. Neither starts lit: the list is what a press is
        // for, and an unopened list is not an unfiltered one.
        _axisRow = toolPanelUI.AddToggleRow(Kind, AxisRowName,
            (CompanyButton, "By Company", () => OnAxisClicked(Axis.Company)),
            (MetricButton, "By Metric", () => OnAxisClicked(Axis.Metric)));

        BuildList(data);
        LightAxis();
        Light();

        toolPanelUI.ContentChanged();
    }

    private void BuildList(DataSource data)
    {
        if (_open == Axis.None || data == null || !data.IsLoaded)
        {
            toolPanelUI.RemoveContent(Kind, ListName);
            return;
        }

        ButtonList list = toolPanelUI.AddCheckList(Kind, ListHeight, ListName);
        if (list == null) return;

        if (_open == Axis.Metric)
            foreach (int group in data.DataGroupsInOrder())
                _metrics.Add((group, list.Add($"Metric_{group}",
                    DataSource.GroupLabelOfData(data, group), () => OnLineClicked(false, group))));
        else
            foreach (int row in data.DataRowsInOrder())
                _companies.Add((row, list.Add($"Company_{row}",
                    DataSource.RowLabelOfData(data, row), () => OnLineClicked(true, row))));
    }

    // Pressing the open axis closes it again, which leaves the filter exactly as
    // it was: the list is a way of setting the filter, not the filter itself.
    private void OnAxisClicked(Axis axis)
    {
        _open = _open == axis ? Axis.None : axis;
        ClearTint();
        Refresh();
        Report(_open == Axis.None
            ? "closed the filter list"
            : $"opened the list of {(_open == Axis.Company ? "companies" : "metrics")}");
    }

    // The line the finger is over, lit the way the Profile tool lights the line
    // it is about to raise, so a poke says what it will take before it takes it.
    protected override void OnSheetHover(ReadSheets.Reading reading)
    {
        if (!Active || sheetManager == null || _open == Axis.None || !reading.valid)
        {
            ClearTint();
            return;
        }

        TintLine(reading, _open == Axis.Metric);
    }

    protected override void OnSheetCleared() => ClearTint();

    // Leaving the tool leaves no line lit behind it.
    protected override void ClearToolState() => ClearTint();

    protected override void OnSheetRelease(ReadSheets.Reading reading) => ClearTint();

    protected override void OnSheetCommit(ReadSheets.Reading reading)
    {
        ClearTint();
        if (!Active || !reading.valid) return;

        // Without an axis open a poke has no line to mean, so it says so once
        // rather than guessing at the metric and taking the wrong thing off.
        if (_open == Axis.None)
        {
            Notices.Show(this, "Filter",
                "Open By Company or By Metric to choose what a poke takes off the sheet.");
            return;
        }

        DataSource data = Data;
        if (data == null || !data.IsLoaded) return;

        string refusal;
        bool done = _open == Axis.Metric
            ? Toggle(false, data.DataGroupOf(reading.dataCol), out refusal)
            : Toggle(true, reading.dataRow, out refusal);

        if (!done && refusal != null) Notices.Show(this, "Filter", refusal);
    }

    private void LightAxis()
    {
        if (_axisRow == null) return;
        UIButton.SetSelected(_axisRow.At(0), _open == Axis.Company);
        UIButton.SetSelected(_axisRow.At(1), _open == Axis.Metric);
    }

    // The categories with at least one metric on this sheet, in contract order,
    // each with the data groups it covers. A sheet opened with only the
    // liquidity ratios shows one category button, not five.
    private static List<KeyValuePair<string, List<int>>> CategoriesOnSheet(DataSource data)
    {
        var found = new List<KeyValuePair<string, List<int>>>();
        if (data == null || !data.IsLoaded) return found;

        List<int> groups = data.DataGroupsInOrder();

        // Indexed by label once rather than rescanned per ratio: every category
        // would otherwise walk every group, which is five passes over the sheet
        // on each dataset switch.
        var byTitle = new Dictionary<string, List<int>>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < groups.Count; i++)
        {
            string label = DataSource.GroupLabelOfData(data, groups[i]);
            if (string.IsNullOrEmpty(label)) continue;
            if (!byTitle.TryGetValue(label, out List<int> at))
                byTitle[label] = at = new List<int>();
            at.Add(groups[i]);
        }

        foreach (var category in FinancialsContract.MetricCategories)
        {
            var members = new List<int>();
            foreach (string ratio in category.Value)
                if (byTitle.TryGetValue(TitleOf(ratio), out List<int> at))
                    members.AddRange(at);
            if (members.Count > 0)
                found.Add(new KeyValuePair<string, List<int>>(category.Key, members));
        }
        return found;
    }

    // 'working_capital' as the sheet spells it: 'Working Capital'. The same
    // transform the server applies when it writes the header.
    private static string TitleOf(string ratio)
    {
        string[] words = ratio.Split('_');
        for (int i = 0; i < words.Length; i++)
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
        return string.Join(" ", words);
    }

    private const float ListHeight = 130f;
    private const string AxisRowName = "AxisRow";
    private const string ListName = "FilterList";
    private const string CompanyButton = "ByCompany";
    private const string MetricButton = "ByMetric";

    private ButtonList _axisRow;
    private Axis _openBuilt = Axis.None;

    // A square is filled while the thing it names is on the sheet, so the list
    // reads as what the sheet is showing rather than what has been taken off it.
    private void Light()
    {
        DataSource data = Data;

        foreach ((int group, UIButton.Handle handle) in _metrics)
            UIButton.SetChecked(handle, data == null || !data.IsDataGroupHidden(group));

        foreach ((int row, UIButton.Handle handle) in _companies)
            UIButton.SetChecked(handle, data == null || !data.IsRowHidden(row));
    }

    // A company is one row and a metric one column group; past that the two
    // axes filter the same way, so everything below takes the axis as 'rows'.
    private static List<int> InOrder(DataSource data, bool rows) =>
        rows ? data.DataRowsInOrder() : data.DataGroupsInOrder();

    private static List<int> HiddenOf(DataSource data, bool rows) =>
        rows ? data.HiddenRowsInOrder() : data.HiddenGroupsInOrder();

    private static bool IsHidden(DataSource data, bool rows, int id) =>
        rows ? data.IsRowHidden(id) : data.IsDataGroupHidden(id);

    private void OnLineClicked(bool rows, int id)
    {
        if (!Toggle(rows, id, out string refusal) && refusal != null)
            Notices.Show(this, "Filter", refusal);
    }

    public bool Toggle(bool rows, int id, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> hidden = HiddenOf(data, rows);
        if (IsHidden(data, rows, id)) hidden.Remove(id);
        else hidden.Add(id);

        return Apply(rows, hidden, out refusal);
    }

    // The whole hidden set at once: one edit on the timeline however many lines
    // it covers, so undo puts the sheet back the way one action found it.
    public bool Apply(bool rows, IReadOnlyList<int> hidden, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> before = HiddenOf(data, rows);
        bool changed = rows
            ? data.SetHiddenRows(hidden, out refusal)
            : data.SetHiddenGroups(hidden, out refusal);
        if (!changed) return false;

        List<int> after = HiddenOf(data, rows);
        ManageDatasets.ActiveEdits.PushFilter(before, after, rows);

        Light();
        Report(Describe(data, before, after, rows));
        return true;
    }

    private static string Describe(DataSource data, List<int> before, List<int> after, bool rows)
    {
        var gone = new List<string>();
        var back = new List<string>();

        for (int i = 0; i < after.Count; i++)
            if (!before.Contains(after[i])) gone.Add(Label(data, after[i], rows));
        for (int i = 0; i < before.Count; i++)
            if (!after.Contains(before[i])) back.Add(Label(data, before[i], rows));

        string noun = rows ? DataSource.RowNoun(data) : DataSource.GroupNoun(data, true);
        var parts = new List<string>(2);
        if (gone.Count > 0) parts.Add($"took {Summarize(gone, noun)} off the sheet");
        if (back.Count > 0) parts.Add($"put {Summarize(back, noun)} back on the sheet");

        string what = parts.Count > 0
            ? string.Join(" and ", parts)
            : $"left the same {noun}s on the sheet";

        int showing = rows ? data.RowOrder.Count : data.GroupCount(true);
        int total = rows ? data.RowCount : data.DataGroupCount;
        return $"{what}, leaving {showing} of {total} showing";
    }

    private static string Label(DataSource data, int index, bool rows) =>
        rows ? DataSource.RowLabelOfData(data, index) : DataSource.GroupLabelOfData(data, index);

    private static string Summarize(List<string> names, string noun)
    {
        if (names.Count == 1) return names[0];
        if (names.Count <= 3) return string.Join(", ", names);
        return $"{names.Count} {noun}s";
    }

    // The data groups a category covers, or null when the name is not one. The
    // assistant resolves 'the liquidity ratios' through this before it falls
    // back to matching one metric by name.
    public List<int> ResolveCategory(string query)
    {
        DataSource data = Data;
        if (data == null || !data.IsLoaded || string.IsNullOrWhiteSpace(query)) return null;

        string wanted = query.Trim();
        foreach (var category in CategoriesOnSheet(data))
            if (string.Equals(category.Key, wanted, System.StringComparison.OrdinalIgnoreCase))
                return category.Value;
        return null;
    }

    public List<string> CategoryNames()
    {
        var names = new List<string>();
        foreach (var category in CategoriesOnSheet(Data)) names.Add(category.Key);
        return names;
    }


    // A company or metric by the name the user says, or by its 1-based place
    // among those on the sheet. Hidden ones answer to their names: they are what
    // the tool exists to bring back. Numbers count only what is showing, the way
    // DescribeSheet and every other tool count them, so a hidden one has none.
    public bool TryResolve(bool rows, string query, out int id)
    {
        id = -1;

        DataSource data = Data;
        if (data == null || !data.IsLoaded || string.IsNullOrWhiteSpace(query)) return false;

        string wanted = query.Trim();
        List<int> all = InOrder(data, rows);

        for (int i = 0; i < all.Count; i++)
            if (string.Equals(Label(data, all[i], rows), wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                id = all[i];
                return true;
            }

        int only = -1, hits = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (Label(data, all[i], rows).IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            only = all[i];
            hits++;
        }
        if (hits == 1) { id = only; return true; }
        if (hits > 1) return false;

        if (!int.TryParse(wanted, out int position) || position < 1) return false;
        for (int i = 0; i < all.Count; i++)
        {
            if (IsHidden(data, rows, all[i])) continue;
            if (--position == 0) { id = all[i]; return true; }
        }
        return false;
    }

    public List<string> Names(bool rows)
    {
        DataSource data = Data;
        var names = new List<string>();
        if (data == null || !data.IsLoaded) return names;

        foreach (int id in InOrder(data, rows)) names.Add(Label(data, id, rows));
        return names;
    }
}
