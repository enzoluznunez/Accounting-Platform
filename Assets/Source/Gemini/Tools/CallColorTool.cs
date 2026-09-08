using System.Collections.Generic;
using Google.GenAI.Types;
using UnityEngine;

public sealed class CallColorTool : AgenticTool<CallColorTool.Args> {

    public class Args {
        [Doc("Row to color: its name, or its 1-based position across the whole dataset."), Optional]
        public string row;
        [Doc("Column to color: its name, or its 1-based position across the whole dataset."), Optional]
        public string column;
        [Doc("Which color to use. Give it whenever the user named a color; leave it out to use whatever is already chosen."), Optional]
        public string color;
        [Doc("Color several targets in one go: each with a row, a column, or both. Use this instead of calling repeatedly."), Optional]
        public Target[] targets;
        [Doc("Color by value instead of by name: the tool reads the numbers itself and paints the cells that match. " +
             "Use this for requests like 'color the cells above 200' rather than reading the values first."), Optional]
        public Where where;
    }

    public class Where {
        [Doc("Paint cells greater than this. Give it with 'below' for a range."), Optional]
        public double? above;
        [Doc("Paint cells less than this."), Optional]
        public double? below;
        [Doc("Count cells equal to 'above' or 'below' as matches too. Defaults to false, strictly above or below."), Optional]
        public bool? inclusive;
        [Doc("Paint the highest N cells. With 'each', the highest N of every row or column."), Limits(1, 1000), Optional]
        public int? topN;
        [Doc("Paint the lowest N cells. With 'each', the lowest N of every row or column."), Limits(1, 1000), Optional]
        public int? bottomN;
        [Doc("Apply 'topN' or 'bottomN' within every row or every column separately instead of across the whole scope: " +
             "'row' paints each row's own extremes, 'column' each column's."), Values("row", "column"), Optional]
        public string each;
        [Doc("Limit the search to these rows, by name or 1-based position. Leave out to search every row."), Optional]
        public string[] rows;
        [Doc("Limit the search to these columns, by name or 1-based position. Leave out to search every column."), Optional]
        public string[] columns;
        [Doc("Pick the scope lines by their numbers instead of by name: the tool ranks the lines itself and searches " +
             "only the winners. Alone, it paints the winning lines whole."), Optional]
        public OfLine ofLine;
    }

    public class OfLine {
        [Doc("Which axis to pick lines from."), Values("rows", "columns")]
        public string axis;
        [Doc("Which number to judge each line by, worked out across the other axis."),
         Values("sum", "average", "max", "min")]
        public string measure;
        [Doc("'highest' keeps the biggest, 'lowest' the smallest. Defaults to highest."),
         Values("highest", "lowest"), Optional]
        public string pick;
        [Doc("How many winning lines to keep. Defaults to 1."), Limits(1, 100), Optional]
        public int? count;
    }

    public class Target {
        [Doc("Row to color: its name, or its 1-based position. Give with 'column' for one cell, alone for the whole row."), Optional]
        public string row;
        [Doc("Column to color: its name, or its 1-based position. Give with 'row' for one cell, alone for the whole column."), Optional]
        public string column;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallColorTool",
        Description = "Color cells with the Color tool's chosen color for the user, as if they painted them by hand. " +
                      "Pass 'color' when the user named one; the tool arms it for you. " +
                      "Give both 'row' and 'column' to color one cell, or only one of them to color that whole row or " +
                      "column. Rows and columns go by name or by 1-based position across the whole dataset, not " +
                      "within one sliced piece. " +
                      "Pass 'where' to paint by value instead: the tool reads the numbers itself, so " +
                      "'color the cells above 200' is one call and needs no separate read. " +
                      "'where' also takes 'rows' or 'columns' to search only those lines, so the highest or " +
                      "lowest cell of one row is a single call, not a read and a paint. " +
                      "'where.ofLine' picks the scope lines by rank, so the worst month of the best-selling " +
                      "item is one call, and 'ofLine' alone paints the winning lines whole. " +
                      "'where.each' applies topN or bottomN within every row or column, so each item's best " +
                      "month is one call. " +
                      "Coloring an already colored cell replaces its color. This edit joins " +
                      "the undo timeline (Undo reverses it).",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Color, result)) return;

        var color = Scene.Color;
        if (color == null) { result["error"] = "Color tool not found in scene."; return; }

        if (!string.IsNullOrEmpty(args.color) && !color.SetOption(args.color.Trim())) {
            result["error"] = $"There is no color called '{args.color}'.";
            result["options"] = new List<object>(color.Options);
            return;
        }

        if (color.CurrentColorName == "none") {
            NeedChoice(result, "color", color.Options,
                "The Color tool is ready but no color is chosen. Ask the user which color, then call this again with 'color'.");
            return;
        }

        var mgr = Scene.Sheets;
        if (mgr == null || !mgr.IsBuilt) { result["error"] = "The sheet is not ready to color; is a sheet shown?"; return; }
        if (!mgr.IsPresented) {
            Refuse(result, "visible sheet",
                "The sheet is hidden because the dataset is collapsed. Reopen it with SetDataset, then call this again.");
            return;
        }

        var data = Scene.Data;

        var wanted = new List<ResolvedTarget>();
        if (args.where != null) {
            if (!Select(args.where, mgr, data, result, wanted)) return;
            if (wanted.Count == 0) {
                result["cells"] = 0;
                result["matched"] = 0;
                result["note"] = "No cell matched, so nothing was painted.";
                return;
            }
            result["matched"] = wanted.Count;
        }
        else {
            var targets = new List<Target>();
            if (args.targets != null && args.targets.Length > 0) targets.AddRange(args.targets);
            else targets.Add(new Target { row = args.row, column = args.column });

            foreach (Target t in targets)
                if (!ResolveTarget(t, mgr, result, wanted)) return;
        }

        int total = 0;
        var painted = new List<object>();
        bool ok = RunGrouped(wanted.Count, i => {
            ResolvedTarget t = wanted[i];
            int cells;
            if (t.hasRow && t.hasCol) {
                if (!color.PaintCell(t.visRow, t.visCol)) { result["error"] = "That cell could not be colored."; return false; }
                cells = 1;
                painted.Add(new Dictionary<string, object> {
                    { "row", LineLabel(data, false, t.visRow) }, { "column", LineLabel(data, true, t.visCol) } });
            }
            else {
                bool columns = t.hasCol;
                int line = columns ? t.visCol : t.visRow;
                cells = color.PaintLine(columns, line);
                if (cells == 0) { result["error"] = $"That {(columns ? "column" : "row")} could not be colored."; return false; }
                painted.Add(new Dictionary<string, object> { { columns ? "column" : "row", LineLabel(data, columns, line) } });
            }
            total += cells;
            return true;
        });
        if (!ok) return;

        result["cells"] = total;
        result["painted"] = painted;
        result["colored"] = color.CurrentColorName;
        result["undoable"] = true;
    }

    private static bool ResolveTarget(Target t, ManageSheets mgr,
        Dictionary<string, object> result, List<ResolvedTarget> into) {

        string row = t.row, column = t.column;
        bool hasRow = !string.IsNullOrEmpty(row);
        bool hasCol = !string.IsNullOrEmpty(column);
        if (!hasRow && !hasCol) {
            result["error"] = "Each target needs a row, a column, or both.";
            return false;
        }

        if (hasRow && !hasCol && !TitleExistsOnAxis(row, false) && TitleExistsOnAxis(row, true)) {
            result["note"] = $"'{row}' is a column, not a row, so that column was colored.";
            column = row; row = null;
            hasRow = false; hasCol = true;
        }
        else if (hasCol && !hasRow && !TitleExistsOnAxis(column, true) && TitleExistsOnAxis(column, false)) {
            result["note"] = $"'{column}' is a row, not a column, so that row was colored.";
            row = column; column = null;
            hasCol = false; hasRow = true;
        }

        int visRow = -1;
        if (hasRow && !TryResolveLine(row, false, 0, mgr.RowCount - 1, result, out visRow)) return false;

        if (!hasCol) {
            into.Add(new ResolvedTarget { visRow = visRow, hasRow = true });
            return true;
        }

        if (!TryResolveLine(column, true, 0, mgr.ColCount - 1, result, out int block)) return false;
        BlockSpan(true, block, 0, mgr.ColCount - 1, out int lo, out int hi);

        // A named metric covers each of its years.
        for (int c = lo; c <= hi; c++)
            into.Add(new ResolvedTarget { visRow = visRow, visCol = c, hasRow = hasRow, hasCol = true });
        return true;
    }

    private static bool Select(Where w, ManageSheets mgr, DataSource data,
        Dictionary<string, object> result, List<ResolvedTarget> into) {

        if (data == null) { result["error"] = "No data source found in scene."; return false; }

        bool hasRange = w.above.HasValue || w.below.HasValue;
        bool hasRank = w.topN.HasValue || w.bottomN.HasValue;
        bool hasLine = w.ofLine != null;
        if (!hasRange && !hasRank && !hasLine) {
            result["error"] = "'where' needs one of 'above', 'below', 'topN', 'bottomN' or 'ofLine'.";
            return false;
        }
        if (w.topN.HasValue && w.bottomN.HasValue) {
            result["error"] = "Give either 'topN' or 'bottomN', not both.";
            return false;
        }
        string each = w.each?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(each) && each != "row" && each != "column") {
            result["error"] = "'each' must be 'row' or 'column'.";
            return false;
        }
        if (!string.IsNullOrEmpty(each) && !hasRank) {
            result["error"] = "'each' needs 'topN' or 'bottomN' to apply within every line.";
            return false;
        }

        if (!TryResolveScope(w.rows, false, 0, mgr.RowCount - 1, result, out List<int> rows)) return false;
        if (!TryResolveScope(w.columns, true, 0, mgr.ColCount - 1, result, out List<int> colBlocks)) return false;
        List<int> cols = ExpandBlocks(true, colBlocks, 0, mgr.ColCount - 1);

        bool columnsScoped = w.columns != null && w.columns.Length > 0;

        List<int> pickedLines = null;
        bool pickedColumns = false;
        if (hasLine) {
            if (!PickScopeLines(w.ofLine, data, rows, cols, columnsScoped, result, out pickedColumns, out pickedLines)) return false;
            if (pickedColumns) cols = pickedLines;
            else rows = pickedLines;
        }

        if (!hasRange && !hasRank) {
            foreach (int line in pickedLines)
                into.Add(pickedColumns
                    ? new ResolvedTarget { visCol = line, hasCol = true }
                    : new ResolvedTarget { visRow = line, hasRow = true });
            return true;
        }

        bool inclusive = w.inclusive ?? false;
        var hits = new List<KeyValuePair<double, ResolvedTarget>>();
        foreach (int r in rows)
            foreach (int c in cols) {
                if (!TryCellValue(data, r, c, out double v)) continue;
                if (w.above.HasValue && (inclusive ? v < w.above.Value : v <= w.above.Value)) continue;
                if (w.below.HasValue && (inclusive ? v > w.below.Value : v >= w.below.Value)) continue;
                hits.Add(new KeyValuePair<double, ResolvedTarget>(v, new ResolvedTarget {
                    visRow = r, visCol = c, hasRow = true, hasCol = true }));
            }

        if (hasRank) {
            bool top = w.topN.HasValue;
            int take = w.topN ?? w.bottomN.Value;
            System.Comparison<KeyValuePair<double, ResolvedTarget>> byRank =
                (a, b) => top ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key);

            if (each == "row" || each == "column") {
                bool byRow = each == "row";
                var groups = new Dictionary<int, List<KeyValuePair<double, ResolvedTarget>>>();
                var groupOrder = new List<int>();
                foreach (var hit in hits) {
                    int key = byRow ? hit.Value.visRow : hit.Value.visCol;
                    if (!groups.TryGetValue(key, out var group)) {
                        group = new List<KeyValuePair<double, ResolvedTarget>>();
                        groups[key] = group;
                        groupOrder.Add(key);
                    }
                    group.Add(hit);
                }
                var kept = new List<KeyValuePair<double, ResolvedTarget>>();
                foreach (int key in groupOrder) {
                    var group = groups[key];
                    group.Sort(byRank);
                    int n = Mathf.Min(take, group.Count);
                    for (int i = 0; i < n; i++) kept.Add(group[i]);
                }
                hits = kept;
            }
            else {
                hits.Sort(byRank);
                hits = hits.GetRange(0, Mathf.Min(take, hits.Count));
            }
        }

        foreach (var hit in hits) into.Add(hit.Value);
        return true;
    }

    private static bool PickScopeLines(OfLine of, DataSource data, List<int> rows, List<int> cols,
        bool columnsScoped, Dictionary<string, object> result, out bool columns, out List<int> picked) {

        columns = false;
        picked = null;

        string axis = of.axis?.Trim().ToLowerInvariant();
        if (axis == "columns" || axis == "column") columns = true;
        else if (axis != "rows" && axis != "row") {
            result["error"] = "'ofLine' needs an 'axis' of 'rows' or 'columns'.";
            return false;
        }

        // Judging one line against another only means something within a single
        // metric; across metrics the units differ and the comparison is empty.
        if (data != null && data.IsGrouped(true)) {
            if (columns) {
                result["error"] = "The metrics are in different units, so 'ofLine' cannot judge one against another " +
                    "on the column axis. Name the metric in 'columns' and judge the rows instead.";
                return false;
            }
            if (!columnsScoped) {
                result["error"] = "Each row spans several metrics in different units, so there is no single number " +
                    "to judge it by. Add 'columns' naming the metric, such as columns: ['Revenue'].";
                return false;
            }
        }

        if (!TryParseMeasure(of.measure, "ofLine", result, out string measure)) return false;
        bool highest = !string.Equals(of.pick, "lowest", System.StringComparison.OrdinalIgnoreCase);
        int count = of.count ?? 1;

        List<int> lines = columns ? cols : rows;
        List<int> cross = columns ? rows : cols;

        var scored = new List<KeyValuePair<double, int>>(lines.Count);
        foreach (int line in lines) {
            if (!TryLineMeasure(data, columns, line, cross, measure, out double score)) continue;
            scored.Add(new KeyValuePair<double, int>(score, line));
        }

        if (scored.Count == 0) {
            result["error"] = $"None of the {(columns ? "columns" : "rows")} had numbers to judge.";
            return false;
        }

        scored.Sort((a, b) => highest ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key));
        int take = Mathf.Min(count, scored.Count);
        picked = new List<int>(take);
        var titles = new List<object>(take);
        for (int i = 0; i < take; i++) {
            picked.Add(scored[i].Value);
            titles.Add(LineLabel(data, columns, scored[i].Value));
        }
        result["scopedTo"] = titles;
        result["scopedBy"] = measure;
        return true;
    }

    private static string LineLabel(DataSource data, bool columns, int visLine) {
        string title = data != null ? data.TitleAt(columns, visLine) : null;
        return string.IsNullOrEmpty(title) ? (visLine + 1).ToString() : title;
    }
}
