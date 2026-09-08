using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class GetStatistics : AgenticTool<GetStatistics.Args> {

    public class Args {
        [Doc("A row: its name, or its 1-based position. Returns stats for that whole row."), Optional]
        public string row;
        [Doc("A metric: its name, or its 1-based position. Returns stats for it, one set per year when the sheet pairs its columns."), Optional]
        public string column;
        [Doc("'rows' or 'columns': returns stats for every line on that axis at once."), Optional]
        public string axis;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "GetStatistics",
        Description = "Read a row's or column's statistics: count, minimum, maximum, average and sum, the same " +
                      "numbers the user's Profile tooltip shows them, plus 'minAt' and 'maxAt', naming the line " +
                      "on the other axis where each extreme sits. " +
                      "Give 'row' or 'column' for one line, or " +
                      "'axis' ('rows' or 'columns') for every line on that axis in one call. " +
                      "When the sheet pairs its columns each year is summarised separately, and a row comes back " +
                      "with one summary per metric, because metrics are in different units and do not combine. " +
                      "Names and 1-based " +
                      "positions run across the whole dataset in display order. Use this for totals, averages and " +
                      "highest/lowest questions instead of reading raw cells and computing; GetNumbers stays the " +
                      "source for individual cell values.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var data = Scene.Data;
        var mgr = Scene.Sheets;
        if (data == null || mgr == null || !mgr.IsBuilt) { result["error"] = "No sheet in scene."; return; }

        bool hasRow = !string.IsNullOrWhiteSpace(args.row);
        bool hasColumn = !string.IsNullOrWhiteSpace(args.column);
        bool hasAxis = !string.IsNullOrWhiteSpace(args.axis);

        int given = (hasRow ? 1 : 0) + (hasColumn ? 1 : 0) + (hasAxis ? 1 : 0);
        if (given != 1) {
            result["error"] = "Give exactly one of 'row', 'column', or 'axis'.";
            return;
        }

        int rowMax = mgr.RowCount - 1;
        int colMax = mgr.ColCount - 1;

        if (hasAxis) {
            string axis = args.axis.Trim().ToLowerInvariant();
            bool columns = axis == "columns" || axis == "column";
            if (!columns && axis != "rows" && axis != "row") {
                result["error"] = "'axis' must be 'rows' or 'columns'.";
                return;
            }

            int count = columns ? colMax + 1 : rowMax + 1;
            var lines = new List<object>();
            for (int v = 0; v < count; v++)
                lines.Add(columns ? LineEntry(data, true, v, rowMax, colMax) : RowEntry(data, v, colMax));
            result["axis"] = columns ? "columns" : "rows";
            result["lines"] = lines;
            NoteRefreshed(columns);
            return;
        }

        bool isColumn = hasColumn;
        int max = isColumn ? colMax : rowMax;
        if (!TryResolveLine(isColumn ? args.column : args.row, isColumn, 0, max, result, out int block)) return;

        result["axis"] = isColumn ? "column" : "row";

        if (!isColumn) {
            foreach (var kv in RowEntry(data, block, colMax)) result[kv.Key] = kv.Value;
            return;
        }

        BlockSpan(true, block, 0, colMax, out int lo, out int hi);
        if (lo == hi) {
            foreach (var kv in LineEntry(data, true, lo, rowMax, colMax)) result[kv.Key] = kv.Value;
            return;
        }

        // A paired metric is summarised a year at a time; the two are the same
        // measure and comparing them is the point.
        result["metric"] = DataSource.GroupLabelAt(data, true, block);
        var years = new List<object>();
        for (int v = lo; v <= hi; v++) years.Add(LineEntry(data, true, v, rowMax, colMax));
        result["years"] = years;
    }

    private static Dictionary<string, object> RowEntry(DataSource data, int visRow, int colMax) {
        var entry = new Dictionary<string, object> {
            { "title", data.TitleAt(false, visRow) },
            { "position", visRow + 1 }
        };

        if (!data.IsGrouped(true)) {
            Fill(entry, SheetStats.Over(data, visRow, visRow, 0, colMax), data, false, visRow);
            return entry;
        }

        // One row spans every metric, and they are in different units, so a single
        // summary would average dollars with share counts. Report one per metric.
        var metrics = new List<object>();
        int blocks = data.GroupCount(true);
        for (int b = 0; b < blocks; b++) {
            data.GroupSpan(true, b, out int lo, out int hi);
            if (hi > colMax) hi = colMax;
            var one = new Dictionary<string, object> { { "metric", DataSource.GroupLabelAt(data, true, b) } };
            Fill(one, SheetStats.Over(data, visRow, visRow, lo, hi), data, false, visRow);
            metrics.Add(one);
        }
        entry["metrics"] = metrics;
        entry["note"] = "Each metric stands on its own; they are in different units and do not combine.";
        return entry;
    }

    private static Dictionary<string, object> LineEntry(DataSource data, bool column, int visLine,
        int rowMax, int colMax) {
        SheetStats.Summary s = column
            ? SheetStats.Over(data, 0, rowMax, visLine, visLine)
            : SheetStats.Over(data, visLine, visLine, 0, colMax);

        var entry = new Dictionary<string, object> {
            { "title", data.TitleAt(column, visLine) },
            { "position", data.GroupOf(column, visLine) + 1 }
        };

        if (data.IsGrouped(column)) {
            entry["metric"] = DataSource.GroupLabelAt(data, column, data.GroupOf(column, visLine));
            entry["year"] = data.SeriesTitleAt(column, visLine);
        }

        Fill(entry, s, data, column, visLine);
        return entry;
    }

    private static void Fill(Dictionary<string, object> entry, SheetStats.Summary s,
        DataSource data, bool column, int visLine) {
        if (!s.valid) {
            entry["note"] = "No values here.";
            return;
        }

        entry["count"] = s.count;
        entry["minimum"] = Round(s.min);
        entry["maximum"] = Round(s.max);
        entry["average"] = Round(s.mean);
        entry["sum"] = Round(s.sum);

        entry["minAt"] = data.TitleAt(!column, column ? s.minVisRow : s.minVisCol);
        entry["maxAt"] = data.TitleAt(!column, column ? s.maxVisRow : s.maxVisCol);
    }
}
