using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class CallFilterTool : AgenticTool<CallFilterTool.Args> {

    public class Args {
        [Doc("Metrics to take off the sheet, by name, by 1-based position among the metrics, or by the " +
             "kind of ratio they are \u2014 naming a kind takes all of its metrics off together."), Optional]
        public string[] hide;
        [Doc("Metrics to bring back onto the sheet, by name, by position, or by kind."), Optional]
        public string[] show;
        [Doc("Show only these metrics and hide every other one. Use this for 'just show me X and Y'; " +
             "it replaces the filter rather than adding to it. A kind of ratio stands for all of its " +
             "metrics here too."), Optional]
        public string[] only;
        [Doc("Clear the filter and put every metric back on the sheet."), Optional]
        public bool? showAll;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallFilterTool",
        Description = "Choose which metrics stand on the sheet. A hidden metric leaves with both its years and " +
                      "keeps its place in the arrangement, so bringing it back does not disturb a sort. " +
                      "Give 'hide' and 'show' to change particular metrics, 'only' to leave just the ones named, " +
                      "or 'showAll' to clear the filter. Anywhere a metric can be named, so can a kind of ratio " +
                      "\u2014 liquidity, efficiency, solvency, profitability or valuation \u2014 which stands for " +
                      "every metric of that kind on the sheet; ListRatios gives the grouping. " +
                      "Everything in one call is one edit on the undo timeline. " +
                      "The metrics that are off the sheet are not gone: they come back with this tool, and no other " +
                      "tool can read them while they are hidden. At least one metric always stays on the sheet.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Filter, result)) return;

        var filter = Scene.Filter;
        if (filter == null) { result["error"] = "Filter tool not found in scene."; return; }

        var data = Scene.Data;
        if (data == null || !data.IsLoaded) { result["error"] = "No dataset is open."; return; }

        bool showAll = args.showAll ?? false;
        bool only = args.only != null && args.only.Length > 0;
        bool named = (args.hide != null && args.hide.Length > 0) || (args.show != null && args.show.Length > 0);

        if (!showAll && !only && !named) {
            NeedChoice(result, "metrics", filter.MetricNames(), "Say which metrics to hide or show.");
            return;
        }

        if (only && (named || showAll)) {
            result["error"] = "'only' already says what the sheet should hold; do not send 'hide', 'show' or 'showAll' with it.";
            return;
        }

        var hidden = new HashSet<int>(showAll ? new List<int>() : data.HiddenGroupsInOrder());

        if (only) {
            List<int> keep = new List<int>();
            if (!Resolve(filter, args.only, result, keep)) return;

            hidden.Clear();
            List<int> all = data.DataGroupsInOrder();
            for (int i = 0; i < all.Count; i++)
                if (!keep.Contains(all[i])) hidden.Add(all[i]);
        }
        else {
            var toHide = new List<int>();
            var toShow = new List<int>();
            if (!Resolve(filter, args.hide, result, toHide)) return;
            if (!Resolve(filter, args.show, result, toShow)) return;

            for (int i = 0; i < toHide.Count; i++) hidden.Add(toHide[i]);
            for (int i = 0; i < toShow.Count; i++) hidden.Remove(toShow[i]);
        }

        // A false return with nothing to say is a filter that was already in
        // force; the timeline shows that as 'changed' false and reads back below.
        if (!filter.Apply(new List<int>(hidden), out string refusal) && refusal != null) {
            result["error"] = refusal;
            return;
        }

        Report(data, result);
    }

    private static bool Resolve(FilterTool filter, string[] wanted,
        Dictionary<string, object> result, List<int> into) {

        if (wanted == null) return true;

        for (int i = 0; i < wanted.Length; i++) {
            string name = wanted[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            // A kind of ratio first: it stands for several metrics at once, and
            // no category shares a name with a metric, so there is nothing to
            // disambiguate between them.
            List<int> kind = filter.ResolveCategory(name);
            if (kind != null) {
                for (int k = 0; k < kind.Count; k++)
                    if (!into.Contains(kind[k])) into.Add(kind[k]);
                continue;
            }

            if (!filter.TryResolveMetric(name, out int group)) {
                result["error"] = $"No single metric or kind of ratio matches '{name.Trim()}'.";
                result["metrics"] = new List<object>(filter.MetricNames());
                result["kinds"] = new List<object>(filter.CategoryNames());
                return false;
            }
            if (!into.Contains(group)) into.Add(group);
        }
        return true;
    }

    private static void Report(DataSource data, Dictionary<string, object> result) {
        var showing = new List<object>();
        var off = new List<object>();

        foreach (int group in data.DataGroupsInOrder())
            (data.IsDataGroupHidden(group) ? off : showing).Add(DataSource.GroupLabelOfData(data, group));

        result["showing"] = showing;
        result["hidden"] = off;
        result["note"] = off.Count == 0
            ? "Every metric is on the sheet."
            : "Positions have shifted; the metrics on the sheet are the ones listed in 'showing', in that order.";
    }

}
