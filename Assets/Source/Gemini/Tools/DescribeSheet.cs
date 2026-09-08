using System;
using System.Collections.Generic;
using Google.GenAI.Types;
using UnityEngine;

public sealed class DescribeSheet : AgenticTool<DescribeSheet.Args> {

    public class Args {
        [Doc("The sheet id, from ListDatasets.")]
        public int sheet;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "DescribeSheet",
        Description = "Read one sheet's shape and placement by its id, from the ids ListDatasets lists for the open " +
                      "dataset. Returns 'rows' and 'columns', the titles that sheet covers in display order — or " +
                      "'metrics' and 'columnsPerMetric' when the sheet pairs its columns, one metric holding a cell " +
                      "per year — its " +
                      "'rowRange' and 'colRange' (the 1-based numbers those titles start and end at, so a title's " +
                      "number is its position within that range), 'rowCategory' and 'columnCategory' (what the rows " +
                      "and columns represent, when the data says), its 'position' when it has one, its 'color' (a " +
                      "single name when every cell shares one, 'mixed' when they differ, absent when uncolored), and " +
                      "'projections' (the cells and strips the Detail and Profile tools have raised above this " +
                      "sheet). This carries no cell values; call GetNumbers for the numbers.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var data = Scene.Data;
        var mgr = Scene.Sheets;
        if (data == null || mgr == null || !mgr.IsBuilt) { result["error"] = "No sheet in scene."; return; }

        bool whole = args.sheet == ManageSheets.WholeSheetId;
        CreateSheet piece = whole ? null : mgr.SheetById(args.sheet);

        if (!whole && piece == null) {
            result["error"] = $"There is no sheet #{args.sheet} on the open dataset; call ListDatasets for current ids.";
            return;
        }

        int rowMin = piece != null ? piece.rowMin : 0;
        int rowMax = piece != null ? piece.rowMax : mgr.RowCount - 1;
        int colMin = piece != null ? piece.colMin : 0;
        int colMax = piece != null ? piece.colMax : mgr.ColCount - 1;

        result["rows"] = Titles(data.RowOrder, data.RowTitles, rowMin, rowMax);
        if (rowMin == 0 && rowMax == mgr.RowCount - 1) NoteRefreshed(false);
        if (colMin == 0 && colMax == mgr.ColCount - 1) NoteRefreshed(true);

        result["rowRange"] = new List<object> { rowMin + 1, rowMax + 1 };

        if (data.IsGrouped(true)) {
            // The columns come in pairs, so name the metrics once and say which
            // cells sit under each; listing raw column titles would double the
            // positions the model then has to address.
            int first = data.GroupOf(true, colMin);
            int last = data.GroupOf(true, colMax);
            var metrics = new List<object>();
            for (int b = first; b <= last; b++) metrics.Add(DataSource.GroupLabelAt(data, true, b));

            result["metrics"] = metrics;
            result["columnsPerMetric"] = new List<object>(data.SeriesTitles);
            result["colRange"] = new List<object> { 1, last - first + 1 };
            result["note"] = "Each metric holds one cell per entry in 'columnsPerMetric', drawn side by side. " +
                             "Positions on this axis count metrics, not cells, and a metric's two bars cannot be " +
                             "separated. Bar heights are comparable within a metric but not between metrics.";
        }
        else {
            result["columns"] = Titles(data.ColumnOrder, data.ColumnTitles, colMin, colMax);
            result["colRange"] = new List<object> { colMin + 1, colMax + 1 };
        }
        if (!string.IsNullOrEmpty(data.RowAxisTitle)) result["rowCategory"] = data.RowAxisTitle;
        if (!string.IsNullOrEmpty(data.ColumnAxisTitle)) result["columnCategory"] = data.ColumnAxisTitle;

        if (piece != null) {
            mgr.GetCommittedPose(piece, out Vector3 p, out _, out _);
            result["position"] = new Dictionary<string, object> {
                { "columns", Math.Round(p.x, 3) },
                { "up", Math.Round(p.y, 3) },
                { "rows", Math.Round(p.z, 3) }
            };

            if (mgr.TryGetPieceColor(piece, out Color c, out bool mixed)) result["color"] = NearestColorName(c);
            else if (mixed) result["color"] = "mixed";
        }

        result["projections"] = Projections(mgr, data, rowMin, rowMax, colMin, colMax);
    }

    private static List<object> Projections(ManageSheets mgr, DataSource data,
        int rowMin, int rowMax, int colMin, int colMax) {
        var list = new List<object>();
        var recs = new List<ProjectionRecord>();
        mgr.CollectProjections(recs);

        for (int i = 0; i < recs.Count; i++) {
            ProjectionRecord rec = recs[i];
            if (!mgr.TryResolveProjection(rec, out int vr, out int vc)) continue;
            if (vr < rowMin || vr > rowMax || vc < colMin || vc > colMax) continue;

            var entry = new Dictionary<string, object> { { "kind", rec.isStrip ? "strip" : "cell" } };

            if (rec.isStrip) {
                entry["direction"] = rec.isColumn ? "column" : "row";
                entry[rec.isColumn ? "column" : "row"] =
                    Title(data, rec.isColumn, rec.isColumn ? rec.dataCol : rec.dataRow);
                if (rec.isColumn && data.IsGrouped(true))
                    entry["metric"] = data.GroupTitleOfData(true, rec.dataCol);
            }
            else {
                entry["row"] = Title(data, false, rec.dataRow);
                entry["column"] = Title(data, true, rec.dataCol);
            }

            list.Add(entry);
        }
        return list;
    }

    private static string Title(DataSource data, bool columns, int dataIndex) {
        IReadOnlyList<string> titles = columns ? data.ColumnTitles : data.RowTitles;
        return dataIndex >= 0 && dataIndex < titles.Count ? titles[dataIndex] : null;
    }

    private static List<object> Titles(IReadOnlyList<int> order, IReadOnlyList<string> src, int min, int max) {
        var titles = new List<object>();
        for (int v = min; v <= max; v++) {
            int idx = v >= 0 && v < order.Count ? order[v] : -1;
            titles.Add(idx >= 0 && idx < src.Count ? src[idx] : "");
        }
        return titles;
    }

    private static string NearestColorName(Color c) {
        var tool = Scene.Color;
        if (tool == null || tool.palette == null || tool.palette.Length == 0)
            return "#" + ColorUtility.ToHtmlStringRGB(c);

        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < tool.palette.Length; i++) {
            Color p = tool.palette[i];
            float d = (p.r - c.r) * (p.r - c.r) + (p.g - c.g) * (p.g - c.g) + (p.b - c.b) * (p.b - c.b);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best < tool.Options.Count ? tool.Options[best] : "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
