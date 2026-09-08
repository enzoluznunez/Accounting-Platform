using System;
using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class ListDatasets : AgenticTool {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListDatasets",
        Description = "List the open datasets and what stands on each: 'name', 'active' (whether it is the dataset " +
                      "currently open), 'sheets' (the ids of its sheet pieces) and 'edits' (the tool edits that " +
                      "currently stand on it, newest first). " +
                      "This is cheap and carries no row, column or value data; call DescribeSheet with an id for a " +
                      "sheet's titles, ranges, position and color, GetNumbers for its numbers, or DescribeDataset for the " +
                      "open dataset's raw source text. " +
                      "Call it for current sheet ids, especially after slicing or switching dataset."
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
                list.Add(new Dictionary<string, object> {
                    { "name", string.IsNullOrEmpty(dataset.label) ? "dataset" : dataset.label },
                    { "active", active },
                    { "sheets", active ? ActiveSheetIds() : SheetIdsFromEdits(dataset.Edits) },
                    { "edits", DescribeStack(dataset.Edits, dataset.source, active) }
                });
            }
        }
        else {
            list.Add(new Dictionary<string, object> {
                { "name", Scene.DatasetLabel },
                { "active", true },
                { "sheets", ActiveSheetIds() },
                { "edits", DescribeStack(ManageDatasets.ActiveEdits, Scene.Data, true) }
            });
        }
        return list;
    }

    private static List<object> ActiveSheetIds() {
        var ids = new List<object>();
        var mgr = Scene.Sheets;
        if (mgr == null || !mgr.IsBuilt) return ids;

        var list = mgr.Sheets;
        for (int i = 0; i < list.Count; i++) ids.Add(list[i].sheetId);
        return ids;
    }

    private static List<object> SheetIdsFromEdits(IReadOnlyList<Edit> stack) {
        var ids = new List<object> { ManageSheets.FirstSheetId };
        for (int i = 0; i < stack.Count; i++)
            if (stack[i].kind == EditKind.Slice) ids.Add(stack[i].slice.bId);
        return ids;
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
        if (stack[last].kind == EditKind.Slice)
        {
            var cuts = new List<object>();
            var pieces = new List<object>();
            for (int i = first; i <= last; i++)
            {
                if (stack[i].kind != EditKind.Slice) continue;
                cuts.Add(stack[i].slice.boundary);
                pieces.Add(stack[i].slice.bId);
            }
            e["direction"] = stack[last].slice.axis == SliceAxis.Column ? "column" : "row";
            e["cuts"] = cuts;
            e["newPieces"] = pieces;
            e["note"] = $"one slice instruction, {cuts.Count} cuts; undo reverts them together";
            return;
        }

        DescribeEdit(stack[last], e, data, active);
        if (!e.ContainsKey("note"))
            e["note"] = $"one instruction, {last - first + 1} steps; undo reverts them together";
    }

    private static void DescribeEdit(Edit r, Dictionary<string, object> e, DataSource data, bool active)
    {
        switch (r.kind)
        {
            case EditKind.Color:
                e["color"] = string.IsNullOrEmpty(r.colorName) ? "unknown" : r.colorName;
                if (!string.IsNullOrEmpty(r.colorHex)) e["hex"] = r.colorHex;
                e["cells"] = r.colorStroke != null ? r.colorStroke.Count : 0;
                break;
            case EditKind.Move:
            case EditKind.Rotate:
            case EditKind.Scale:
                e["sheet"] = r.move.sheetId;
                CreateSheet moved = active && Scene.Sheets != null ? Scene.Sheets.SheetById(r.move.sheetId) : null;
                if (moved != null) {
                    e["rowRange"] = new List<object> { moved.rowMin + 1, moved.rowMax + 1 };
                    e["colRange"] = new List<object> { moved.colMin + 1, moved.colMax + 1 };
                }
                e["distanceMeters"] = Math.Round(r.move.distance, 3);
                break;
            case EditKind.Slice:
                e["direction"] = r.slice.axis == SliceAxis.Column ? "column" : "row";
                e["boundary"] = r.slice.boundary;
                e["sheets"] = new List<object> { r.slice.aId, r.slice.bId };
                e["cellsSeparated"] = SmallerPieceCells(r.slice);
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
            case EditKind.Detail:
                e["row"] = DataTitle(data, false, r.projection.dataRow);
                e["column"] = DataTitle(data, true, r.projection.dataCol);
                break;
            case EditKind.Profile:
                e["direction"] = r.projection.isColumn ? "column" : "row";
                e[r.projection.isColumn ? "column" : "row"] = DataTitle(data, r.projection.isColumn,
                    r.projection.isColumn ? r.projection.dataCol : r.projection.dataRow);
                break;
        }
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

    private static int SmallerPieceCells(SliceRecord s)
    {
        int rows = s.pRowMax - s.pRowMin + 1;
        int cols = s.pColMax - s.pColMin + 1;

        int a, b;
        if (s.axis == SliceAxis.Column) {
            int aCols = s.boundary - s.pColMin + 1;
            a = rows * aCols;
            b = rows * (cols - aCols);
        }
        else {
            int aRows = s.boundary - s.pRowMin + 1;
            a = aRows * cols;
            b = (rows - aRows) * cols;
        }
        return a < b ? a : b;
    }
}

