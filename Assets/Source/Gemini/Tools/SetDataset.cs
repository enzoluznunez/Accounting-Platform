using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI.Types;
using Type = Google.GenAI.Types.Type;

public sealed class SetDataset : AgenticTool<SetDataset.Args> {

    public class Args {
        [Doc("The dataset's name, or its position in the list ListDatasets returns where 1 is the newest. " +
             "Prefer the name.")]
        public string dataset;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetDataset",
        Description = "Open one of the listed datasets (switch to it); each keeps its own edits and undo " +
                      "history. They are the industries the database holds, listed from the start and fetched the " +
                      "first time they are opened, so this is how an industry gets on screen and it works whether " +
                      "or not that one has been read before. Nothing else in the app changes datasets: there is no " +
                      "button for it, so a user who wants a different industry is asking you.",
        Parameters = ParametersFor(typeof(Args))
    };

    // Most industries are listed but unread until someone opens one. Reading takes
    // a frame or two, so the wait happens here, before Run reports on the dataset:
    // otherwise the reply would describe the dataset being left rather than the one
    // being opened.
    protected override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var bound = ToolArguments.Bind(typeof(Args), args, out _) as Args;

        Task<bool> reading = null;
        await MainThread.Run(() => {
            var datasets = Scene.Datasets;
            if (datasets == null || bound == null) return;
            if (!TryResolveIndex(datasets, bound.dataset?.Trim(), out int index)) return;
            if (datasets.Datasets[index].loaded) return;
            reading = datasets.EnsureLoaded(index);
        }).ConfigureAwait(false);

        if (reading != null) await reading.ConfigureAwait(false);

        return await base.Execute(args).ConfigureAwait(false);
    }

    protected override void Run(Args args, Dictionary<string, object> result) {
        var datasets = Scene.Datasets;
        if (datasets == null || datasets.DatasetCount == 0) { result["error"] = "No datasets are open."; return; }

        string query = args.dataset?.Trim();

        if (!TryResolveIndex(datasets, query, out int index)) {
            result["error"] = $"No single open dataset matches '{query}'; if several match, ask the user which one.";
            result["available"] = ListLabels(datasets);
            return;
        }

        if (!datasets.Datasets[index].loaded) {
            result["error"] = $"{datasets.Datasets[index].label} could not be read, so it is not open.";
            result["available"] = ListLabels(datasets);
            return;
        }

        bool alreadyActive = index == datasets.ActiveIndex;

        if (!alreadyActive) datasets.SwitchDataset(index);

        result["switched"] = datasets.Datasets[index].label;
        result["fromTop"] = datasets.DatasetCount - index;
        if (alreadyActive) result["alreadyActive"] = true;

        var data = datasets.Active;
        if (data != null && data.IsLoaded) {
            // The rows on the sheet, not the rows in the dataset: the metric
            // count beside it is already a visible count, and a filter can hold
            // companies back too now, so the two have to be read the same way.
            result["rowCount"] = data.RowOrder.Count;
            if (data.IsGrouped(true)) {
                result["metricCount"] = data.GroupCount(true);
                result["columnsPerMetric"] = new List<object>(data.SeriesTitles);
            }
            else result["columnCount"] = data.ColumnCount;
        }
        if (!alreadyActive)
            result["note"] = "Row and column numbers now refer to this dataset; read it with DescribeSheet before using numbers.";
    }

    private static bool TryResolveIndex(ManageDatasets datasets, string query, out int index) {
        index = -1;
        if (string.IsNullOrWhiteSpace(query)) return false;
        query = query.Trim();

        var list = datasets.Datasets;
        int exact = -1, exactHits = 0;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].label, query, StringComparison.OrdinalIgnoreCase)) { exact = i; exactHits++; }
        if (exactHits == 1) { index = exact; return true; }
        if (exactHits > 1) return false;

        if (int.TryParse(query, out int number)) {
            index = datasets.DatasetCount - number;
            return index >= 0 && index < datasets.DatasetCount;
        }

        int only = -1, hits = 0;
        for (int i = 0; i < list.Count; i++)
        {
            string label = list[i].label;
            if (string.IsNullOrEmpty(label)) continue;
            if (label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                query.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0) continue;
            only = i;
            hits++;
        }
        if (hits == 1) { index = only; return true; }
        return false;
    }

    private static List<object> ListLabels(ManageDatasets datasets) {
        var list = new List<object>();
        var all = datasets.Datasets;
        for (int i = all.Count - 1; i >= 0; i--)
            list.Add(new Dictionary<string, object> { { "name", all[i].label }, { "fromTop", all.Count - i } });
        return list;
    }
}
