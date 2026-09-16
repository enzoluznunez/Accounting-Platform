using System.Collections.Generic;
using UnityEngine;

// Chooses which metrics stand on the sheet. A metric is one column group, so
// hiding one takes both of its years off together; the arrangement underneath is
// untouched, and showing it again puts it back where the sort left it.
public class FilterTool : Tool
{
    protected override ToolType Kind => ToolType.Filter;

    // One entry per metric: the data-space group it stands for, and the button
    // that shows it. Kept together so the two can never fall out of step.
    private readonly List<(int group, UIButton.Handle handle)> _metrics =
        new List<(int, UIButton.Handle)>();

    // One entry per category that has any metric on this sheet, with the data
    // groups it stands for. Categories come from the generated contract, so the
    // app and the database agree on what "the liquidity ratios" means.
    private readonly List<(string name, List<int> groups, UIButton.Handle handle)> _categories =
        new List<(string, List<int>, UIButton.Handle)>();

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
        if (data != null && data.ClearHiddenGroups())
            Report("showed every metric again");
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

    // The buttons follow the dataset; their lit state follows the filter, which
    // an undo or the assistant can change without going through this tool.
    private void Refresh()
    {
        DataSource data = Data;
        Watch(data);

        // Two datasets can hold the same number of metrics under different names,
        // so the list is rebuilt for a new source even when the count matches.
        int count = data != null && data.IsLoaded ? data.DataGroupCount : 0;
        if (data == _builtFrom && count == _metrics.Count) { Light(); return; }

        _builtFrom = data;
        _metrics.Clear();
        _categories.Clear();

        if (toolPanelUI == null) return;

        // Categories first: eighteen metrics is a long list to read one button
        // at a time, and most requests are for a kind of ratio rather than a
        // particular one.
        ButtonList groupList = toolPanelUI.AddOptionList(Kind, CategoryListHeight, "CategoryList");
        ButtonList list = toolPanelUI.AddOptionList(Kind, ListHeight);
        if (list == null || count == 0) return;

        if (groupList != null)
            foreach (var category in CategoriesOnSheet(data))
            {
                List<int> members = category.Value;
                _categories.Add((category.Key, members, groupList.Add($"Category_{category.Key}",
                    Pretty(category.Key), () => OnCategoryClicked(category.Key))));
            }

        foreach (int group in data.DataGroupsInOrder())
            _metrics.Add((group, list.Add($"Metric_{group}",
                DataSource.GroupLabelOfData(data, group), () => OnMetricClicked(group))));

        Light();
    }

    // The categories with at least one metric on this sheet, in contract order,
    // each with the data groups it covers. A sheet opened with only the
    // liquidity ratios shows one category button, not five.
    private static List<KeyValuePair<string, List<int>>> CategoriesOnSheet(DataSource data)
    {
        var found = new List<KeyValuePair<string, List<int>>>();
        if (data == null || !data.IsLoaded) return found;

        List<int> groups = data.DataGroupsInOrder();
        foreach (var category in FinancialsContract.MetricCategories)
        {
            var members = new List<int>();
            foreach (string ratio in category.Value)
            {
                string title = TitleOf(ratio);
                for (int i = 0; i < groups.Count; i++)
                    if (string.Equals(DataSource.GroupLabelOfData(data, groups[i]), title,
                                      System.StringComparison.OrdinalIgnoreCase))
                        members.Add(groups[i]);
            }
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

    private static string Pretty(string category) =>
        category.Length == 0 ? category : char.ToUpperInvariant(category[0]) + category.Substring(1);

    private const float ListHeight = 110f;
    private const float CategoryListHeight = 64f;

    private void Light()
    {
        DataSource data = Data;
        foreach ((int group, UIButton.Handle handle) in _metrics)
            UIButton.SetSelected(handle, data == null || !data.IsDataGroupHidden(group));

        // A category reads as on only while every metric under it is on, so a
        // half-hidden group does not claim to be showing.
        foreach ((string _, List<int> groups, UIButton.Handle handle) in _categories)
        {
            bool whole = true;
            if (data != null)
                for (int i = 0; i < groups.Count && whole; i++)
                    whole = !data.IsDataGroupHidden(groups[i]);
            UIButton.SetSelected(handle, whole);
        }
    }

    private void OnCategoryClicked(string category)
    {
        if (!ToggleCategory(category, out string refusal) && refusal != null)
            Notices.Show(this, "Filter", refusal);
    }

    // Takes a whole category off the sheet, or puts all of it back. Half-hidden
    // counts as off, so one press always brings the whole group back first.
    public bool ToggleCategory(string category, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> members = ResolveCategory(category);
        if (members == null) { refusal = $"'{category}' is not a kind of ratio."; return false; }

        bool whole = true;
        for (int i = 0; i < members.Count && whole; i++) whole = !data.IsDataGroupHidden(members[i]);

        List<int> hidden = data.HiddenGroupsInOrder();
        for (int i = 0; i < members.Count; i++)
        {
            if (whole) { if (!hidden.Contains(members[i])) hidden.Add(members[i]); }
            else hidden.Remove(members[i]);
        }
        return Apply(hidden, out refusal);
    }

    private void OnMetricClicked(int group)
    {
        if (!Toggle(group, out string refusal) && refusal != null)
            Notices.Show(this, "Filter", refusal);
    }

    public bool Toggle(int dataGroup, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> hidden = data.HiddenGroupsInOrder();
        if (data.IsDataGroupHidden(dataGroup)) hidden.Remove(dataGroup);
        else hidden.Add(dataGroup);

        return Apply(hidden, out refusal);
    }

    // The whole hidden set at once: one edit on the timeline however many metrics
    // it covers, so undo puts the sheet back the way one action found it.
    public bool Apply(IReadOnlyList<int> hidden, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> before = data.HiddenGroupsInOrder();
        if (!data.SetHiddenGroups(hidden, out refusal)) return false;

        List<int> after = data.HiddenGroupsInOrder();
        ManageDatasets.ActiveEdits.PushFilter(before, after);

        Light();
        Report(Describe(data, before, after));
        return true;
    }

    private static string Describe(DataSource data, List<int> before, List<int> after)
    {
        var gone = new List<string>();
        var back = new List<string>();

        for (int i = 0; i < after.Count; i++)
            if (!before.Contains(after[i])) gone.Add(DataSource.GroupLabelOfData(data, after[i]));
        for (int i = 0; i < before.Count; i++)
            if (!after.Contains(before[i])) back.Add(DataSource.GroupLabelOfData(data, before[i]));

        string noun = DataSource.GroupNoun(data, true);
        var parts = new List<string>(2);
        if (gone.Count > 0) parts.Add($"took {Names(gone, noun)} off the sheet");
        if (back.Count > 0) parts.Add($"put {Names(back, noun)} back on the sheet");

        string what = parts.Count > 0 ? string.Join(" and ", parts) : "left the same metrics on the sheet";
        return $"{what}, leaving {data.GroupCount(true)} of {data.DataGroupCount} showing";
    }

    private static string Names(List<string> names, string noun)
    {
        if (names.Count == 1) return names[0];
        if (names.Count <= 3) return string.Join(", ", names);
        return $"{names.Count} {noun}s";
    }

    // A metric by the name the user says, or by its 1-based place among the
    // metrics. Hidden ones answer too: they are what the tool exists to bring back.
    public bool TryResolveMetric(string query, out int dataGroup)
    {
        dataGroup = -1;

        DataSource data = Data;
        if (data == null || !data.IsLoaded || string.IsNullOrWhiteSpace(query)) return false;

        string wanted = query.Trim();
        List<int> groups = data.DataGroupsInOrder();

        for (int i = 0; i < groups.Count; i++)
            if (string.Equals(DataSource.GroupLabelOfData(data, groups[i]), wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                dataGroup = groups[i];
                return true;
            }

        int only = -1, hits = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            string name = DataSource.GroupLabelOfData(data, groups[i]);
            if (name.IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            only = groups[i];
            hits++;
        }
        if (hits == 1) { dataGroup = only; return true; }
        if (hits > 1) return false;

        if (int.TryParse(wanted, out int position) && position >= 1 && position <= groups.Count)
        {
            dataGroup = groups[position - 1];
            return true;
        }
        return false;
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

    public List<string> MetricNames()
    {
        DataSource data = Data;
        var names = new List<string>();
        if (data == null || !data.IsLoaded) return names;

        List<int> groups = data.DataGroupsInOrder();
        for (int i = 0; i < groups.Count; i++) names.Add(DataSource.GroupLabelOfData(data, groups[i]));
        return names;
    }
}
