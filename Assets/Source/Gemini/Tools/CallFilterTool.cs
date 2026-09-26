using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class CallFilterTool : AgenticTool<CallFilterTool.Args> {

    public class Args {
        [Doc("Which axis to filter: 'metric' for the columns, 'company' for the rows. " +
             "Defaults to 'metric'. One call filters one axis; send a second call for the other."), Optional]
        public string axis;
        [Doc("Things to take off the sheet, by name, by 1-based position among those showing, or \u2014 on the " +
             "metric axis \u2014 by the kind of ratio they are, which takes all of its metrics off together."), Optional]
        public string[] hide;
        [Doc("Things to bring back onto the sheet, by name or by kind; hidden ones have no position."), Optional]
        public string[] show;
        [Doc("Show only these and hide every other one on the axis. Use this for 'just show me X and Y'; " +
             "it replaces the filter rather than adding to it. A kind of ratio stands for all of its " +
             "metrics here too."), Optional]
        public string[] only;
        [Doc("Clear the filter on this axis and put everything back on the sheet."), Optional]
        public bool? showAll;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallFilterTool",
Description = "Choose what stands on the sheet: which metrics, or which companies. A hidden metric leaves with both its years and " +
                      "keeps its place in the arrangement, so bringing it back does not disturb a sort. " +
                      "The same call filters the company axis when 'axis' is 'company': a hidden company is one " +
                      "row off the sheet, and it keeps its place in the arrangement too. " +
                      "Give 'hide' and 'show' to change particular ones, 'only' to leave just the ones named, " +
                      "or 'showAll' to clear the filter on that axis. Where a metric can be named, so can a kind of ratio " +
                      "\u2014 liquidity, efficiency, solvency, profitability or valuation \u2014 which stands for " +
                      "every metric of that kind on the sheet; ListRatios gives the grouping. " +
                      "Everything in one call is one edit on the undo timeline. " +
                      "What is off the sheet is not gone: it comes back with this tool, and no other " +
                      "tool can read it while it is hidden. At least one metric and one company always stay on the sheet.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Filter, result)) return;

        var filter = Scene.Filter;
        if (filter == null) { result["error"] = "Filter tool not found in scene."; return; }

        var data = Scene.Data;
        if (data == null || !data.IsLoaded) { result["error"] = "No dataset is open."; return; }

        bool rows;
        if (!ReadAxis(args.axis, out rows, result)) return;

        bool showAll = args.showAll ?? false;
        bool only = args.only != null && args.only.Length > 0;
        bool named = (args.hide != null && args.hide.Length > 0) || (args.show != null && args.show.Length > 0);

        if (!showAll && !only && !named) {
            NeedChoice(result, rows ? "companies" : "metrics",
                filter.Names(rows),
                $"Say which {(rows ? "companies" : "metrics")} to hide or show.");
            return;
        }

        if (only && (named || showAll)) {
            result["error"] = "'only' already says what the sheet should hold; do not send 'hide', 'show' or 'showAll' with it.";
            return;
        }

        var hidden = new HashSet<int>(showAll
            ? new List<int>()
            : rows ? data.HiddenRowsInOrder() : data.HiddenGroupsInOrder());

        if (only) {
            List<int> keep = new List<int>();
            if (!Resolve(filter, rows, args.only, result, keep)) return;

            hidden.Clear();
            List<int> all = rows ? data.DataRowsInOrder() : data.DataGroupsInOrder();
            for (int i = 0; i < all.Count; i++)
                if (!keep.Contains(all[i])) hidden.Add(all[i]);
        }
        else {
            var toHide = new List<int>();
            var toShow = new List<int>();
            if (!Resolve(filter, rows, args.hide, result, toHide)) return;
            if (!Resolve(filter, rows, args.show, result, toShow)) return;

            for (int i = 0; i < toHide.Count; i++) hidden.Add(toHide[i]);
            for (int i = 0; i < toShow.Count; i++) hidden.Remove(toShow[i]);
        }

        // A false return with nothing to say is a filter that was already in
        // force; the timeline shows that as 'changed' false and reads back below.
        bool applied = filter.Apply(rows, new List<int>(hidden), out string refusal);

        if (!applied && refusal != null) {
            result["error"] = refusal;
            return;
        }

        Report(data, rows, result);
    }

    // 'metric' unless the caller says otherwise, which is what a filter meant
    // before the tool could reach the other axis.
    private static bool ReadAxis(string axis, out bool rows, Dictionary<string, object> result) {
        rows = false;
        if (string.IsNullOrWhiteSpace(axis)) return true;

        string wanted = axis.Trim().ToLowerInvariant();
        switch (wanted) {
            case "company": case "companies": case "row": case "rows":
                rows = true;
                return true;
            case "metric": case "metrics": case "column": case "columns":
                return true;
            default:
                result["error"] = $"'{axis.Trim()}' is not an axis; say 'metric' or 'company'.";
                return false;
        }
    }

    private static bool Resolve(FilterTool filter, bool rows, string[] wanted,
        Dictionary<string, object> result, List<int> into) {

        if (wanted == null) return true;

        for (int i = 0; i < wanted.Length; i++) {
            string name = wanted[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (rows) {
                if (!filter.TryResolve(true, name, out int row)) {
                    result["error"] = $"No single company matches '{name.Trim()}'.";
                    result["companies"] = new List<object>(filter.Names(true));
                    return false;
                }
                if (!into.Contains(row)) into.Add(row);
                continue;
            }

            // A kind of ratio first: it stands for several metrics at once, and
            // no category shares a name with a metric, so there is nothing to
            // disambiguate between them.
            List<int> kind = filter.ResolveCategory(name);
            if (kind != null) {
                for (int k = 0; k < kind.Count; k++)
                    if (!into.Contains(kind[k])) into.Add(kind[k]);
                continue;
            }

            if (!filter.TryResolve(false, name, out int group)) {
                result["error"] = $"No single metric or kind of ratio matches '{name.Trim()}'.";
                result["metrics"] = new List<object>(filter.Names(false));
                result["kinds"] = new List<object>(filter.CategoryNames());
                return false;
            }
            if (!into.Contains(group)) into.Add(group);
        }
        return true;
    }

    private static void Report(DataSource data, bool rows, Dictionary<string, object> result) {
        var showing = new List<object>();
        var off = new List<object>();

        if (rows)
            foreach (int row in data.DataRowsInOrder())
                (data.IsRowHidden(row) ? off : showing).Add(DataSource.RowLabelOfData(data, row));
        else
            foreach (int group in data.DataGroupsInOrder())
                (data.IsDataGroupHidden(group) ? off : showing).Add(DataSource.GroupLabelOfData(data, group));

        string noun = rows ? "company" : "metric";
        result["axis"] = noun;
        result["showing"] = showing;
        result["hidden"] = off;
        result["note"] = off.Count == 0
            ? $"Every {noun} is on the sheet."
            : $"Positions have shifted; the {noun}s on the sheet are the ones listed in 'showing', in that order.";
    }

}
