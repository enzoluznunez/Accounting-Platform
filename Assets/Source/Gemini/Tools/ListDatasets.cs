using System;
using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class ListDatasets : AgenticTool {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListDatasets",
        Description = "List the datasets and what stands on each: 'name', 'active' (whether it is the dataset " +
                      "currently open), 'read' (false for an industry that is listed but has not been read yet; " +
                      "SetDataset reads it, and 'companies' says how many it would bring) and 'edits' (the tool " +
                      "edits that currently stand on it, newest first). " +
                      "This is cheap and carries no row, column or value data; call DescribeSheet for the sheet's " +
                      "titles, ranges, position and industries, GetNumbers for its numbers, or DescribeDataset for " +
                      "the open dataset's raw source text. " +
                      "Call it when you need to know which datasets exist or which one is open."
    };

    protected override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        result["datasets"] = Datasets();
    }

    private static List<object> Datasets() {
        var list = new List<object>();
        var datasets = Scene.Datasets;

        if (datasets != null && datasets.DatasetCount > 0) {
            for (int i = datasets.DatasetCount - 1; i >= 0; i--) {
                var dataset = datasets.Datasets[i];
                bool active = i == datasets.ActiveIndex;

                // An industry that has not been read yet has no sheet and no
                // edits; saying so is the whole of what there is to report.
                if (!dataset.loaded) {
                    list.Add(new Dictionary<string, object> {
                        { "name", string.IsNullOrEmpty(dataset.label) ? "dataset" : dataset.label },
                        { "active", false },
                        { "read", false },
                        { "companies", dataset.companies }
                    });
                    continue;
                }

                list.Add(new Dictionary<string, object> {
                    { "name", string.IsNullOrEmpty(dataset.label) ? "dataset" : dataset.label },
                    { "active", active },
                    { "read", true },
                    { "edits", DescribeStack(dataset.Edits, dataset.source, active) }
                });
            }
        }
        else {
            list.Add(new Dictionary<string, object> {
                { "name", Scene.DatasetLabel },
                { "active", true },
                { "edits", DescribeStack(ManageDatasets.ActiveEdits, Scene.Data, true) }
            });
        }
        return list;
    }

    private const int MaxEdits = 10;

    private static List<object> DescribeStack(IReadOnlyList<Edit> stack, DataSource data, bool active)
    {
        var edits = new List<object>();
        int position = 0;
        int i = stack.Count - 1;

        while (i >= 0 && edits.Count < MaxEdits)
        {
            Edit top = stack[i];
            int first = i;
            if (top.group != 0)
                while (first - 1 >= 0 && stack[first - 1].group == top.group) first--;
            int steps = i - first + 1;

            position++;
            var e = new Dictionary<string, object> {
                { "position", position },
                { "kind", Edit.KindName(top.kind) }
            };

            if (steps > 1)
            {
                e["steps"] = steps;
                DescribeGroup(stack, first, i, e, data, active);
            }
            else DescribeEdit(top, e, data, active);

            edits.Add(e);
            i = first - 1;
        }

        if (i >= 0)
            edits.Add(new Dictionary<string, object> {
                { "older", i + 1 },
                { "note", $"{i + 1} older records are not listed; they are still on the undo timeline." }
            });

        return edits;
    }

    private static void DescribeGroup(IReadOnlyList<Edit> stack, int first, int last,
        Dictionary<string, object> e, DataSource data, bool active)
    {
        DescribeEdit(stack[last], e, data, active);
        if (!e.ContainsKey("note"))
            e["note"] = $"one instruction, {last - first + 1} steps; undo reverts them together";
    }

    private static void DescribeEdit(Edit r, Dictionary<string, object> e, DataSource data, bool active)
    {
        switch (r.kind)
        {
            case EditKind.Move:
            case EditKind.Rotate:
            case EditKind.Scale:
                e["distanceMeters"] = Math.Round(r.move.distance, 3);
                break;
            case EditKind.Sort:
                e["direction"] = r.reorderIsColumn ? "column" : "row";
                if (r.reorderFrom < 0) {
                    e["reordered"] = r.reorderLines;
                    e["note"] = $"set the order of {r.reorderLines} {(r.reorderIsColumn ? "columns" : "rows")} at once";
                }
                else {
                    e["from"] = r.reorderFrom + 1;
                    e["to"] = r.reorderTarget + 1;
                    e["positionsMoved"] = Math.Abs(r.reorderTarget - r.reorderFrom);
                }
                break;
            case EditKind.Filter:
                e["axis"] = r.filterIsRow ? "company" : "metric";
                e["hidden"] = FilterTitles(data, r.filterIsRow, r.filterPostHidden);
                e["wasHidden"] = FilterTitles(data, r.filterIsRow, r.filterPreHidden);
                break;
            case EditKind.Profile:
                e["direction"] = r.projection.isColumn ? "column" : "row";
                e[r.projection.isColumn ? "column" : "row"] = DataTitle(data, r.projection.isColumn,
                    r.projection.isColumn ? r.projection.dataCol : r.projection.dataRow);
                break;
        }
    }

    // A company filter hides rows by data index; a metric filter hides column groups.
    private static List<object> FilterTitles(DataSource data, bool rows, List<int> hidden)
    {
        var names = new List<object>();
        if (data == null || hidden == null) return names;

        for (int i = 0; i < hidden.Count; i++)
            names.Add(rows ? DataSource.RowLabelOfData(data, hidden[i]) : data.DataGroupTitleAt(hidden[i]));
        return names;
    }

    private static string DataTitle(DataSource data, bool columns, int dataIndex)
    {
        if (data == null) return null;

        // On a grouped axis the metric is what the model addresses, so edits read
        // back in the same words they were asked for.
        if (data.IsGrouped(columns)) return data.GroupTitleOfData(columns, dataIndex);

        IReadOnlyList<string> titles = columns ? data.ColumnTitles : data.RowTitles;
        return dataIndex >= 0 && dataIndex < titles.Count ? titles[dataIndex] : null;
    }

}

