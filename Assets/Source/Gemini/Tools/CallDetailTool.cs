using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class CallDetailTool : AgenticTool<CallDetailTool.Args> {

    public class Args {
        [Doc("The row's name, or its 1-based position within the piece."), Optional]
        public string row;
        [Doc("The column's name, or its 1-based position within the piece."), Optional]
        public string column;
        [Doc("Raise several cells in one go, each with its own row and column. Use this instead of calling repeatedly."), Optional]
        public Cell[] cells;
        [Doc("Target piece (when the sheet is sliced)."), Optional]
        public int? sheet;
        [Doc("Pick the cell by its value instead of by name: the tool reads the numbers itself. " +
             "Use this for requests like 'show me the biggest cell' rather than reading the values first."), Optional]
        public Of of;
    }

    public class Of {
        [Doc("'highest' takes the largest value on the sheet, 'lowest' the smallest."),
         Values("highest", "lowest")]
        public string pick;
        [Doc("How many cells to raise, best first. Defaults to 1."), Limits(1, 100), Optional]
        public int? count;
        [Doc("Limit the search to these rows, by name or 1-based position within the piece. " +
             "Leave out to search every row."), Optional]
        public string[] rows;
        [Doc("Limit the search to these columns, by name or 1-based position within the piece. " +
             "Leave out to search every column."), Optional]
        public string[] columns;
    }

    public class Cell {
        [Doc("The row's name, or its 1-based position.")]
        public string row;
        [Doc("The column's name, or its 1-based position.")]
        public string column;
    }


    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallDetailTool",
        Description = "Raise a single cell as a projection: a copy of that cell floating directly above it, at the " +
                      "height the tallest bar could reach. It stays up after the tool is put away, is an edit on the " +
                      "undo timeline, and follows its value through a Sort reorder. Give 'row' and 'column' as names or " +
                      "1-based positions within the piece; pass 'sheet' when the sheet is sliced. " +
                      "Pass 'of' to pick cells by value instead: 'pick' highest or lowest, 'count' for the top " +
                      "few, and 'rows' or 'columns' to search only those lines; the tool reads the numbers " +
                      "itself, so raising the three highest cells, or the best month of one item, is one call " +
                      "with no separate read.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Detail, result)) return;

        var detail = Scene.Detail;
        if (detail == null) { result["error"] = "Detail tool not found in scene."; return; }

        if (!TryResolveTargetBounds(args.sheet, result, out int rowMin, out int rowMax, out int colMin, out int colMax, out int pieceId))
            return;

        var wanted = new List<ResolvedTarget>();
        if (args.of != null) {
            if (!PickCells(args.of, rowMin, rowMax, colMin, colMax, result, wanted)) return;
        }
        else {
            var cells = new List<Cell>();
            if (args.cells != null && args.cells.Length > 0) cells.AddRange(args.cells);
            else cells.Add(new Cell { row = args.row, column = args.column });

            foreach (Cell cell in cells) {
                if (!TryResolveLine(cell.row, false, rowMin, rowMax, result, out int vr)) return;
                if (!TryResolveLine(cell.column, true, colMin, colMax, result, out int block)) return;

                // A named metric raises each of its years.
                BlockSpan(true, block, colMin, colMax, out int lo, out int hi);
                for (int vc = lo; vc <= hi; vc++)
                    wanted.Add(new ResolvedTarget { visRow = vr, visCol = vc, hasRow = true, hasCol = true });
            }
        }

        var raised = new List<object>();
        bool ok = RunGrouped(wanted.Count, i => {
            ResolvedTarget cell = wanted[i];
            if (!detail.Show(cell.visRow, cell.visCol)) {
                result["error"] = raised.Count == 0
                    ? "That cell could not be projected; it has no value, or it is already projected."
                    : $"Raised {raised.Count}, then one could not be projected.";
                if (raised.Count > 0) result["projected"] = raised;
                return false;
            }
            var where = new Dictionary<string, object> {
                { "row", cell.visRow - rowMin + 1 },
                { "column", Scene.Data != null
                    ? Scene.Data.GroupOf(true, cell.visCol) - Scene.Data.GroupOf(true, colMin) + 1
                    : cell.visCol - colMin + 1 } };
            string year = Scene.Data != null ? Scene.Data.SeriesTitleAt(true, cell.visCol) : null;
            if (year != null) where["year"] = year;
            raised.Add(where);
            return true;
        });
        if (!ok) return;

        result["projected"] = raised;
        if (pieceId > 0) result["sheet"] = pieceId;
    }

    private static bool PickCells(Of of, int rowMin, int rowMax, int colMin, int colMax,
        Dictionary<string, object> result, List<ResolvedTarget> into) {

        var data = Scene.Data;
        if (data == null) { result["error"] = "No data source found in scene."; return false; }

        string pick = of.pick?.Trim().ToLowerInvariant();
        if (pick != "highest" && pick != "lowest") {
            result["error"] = "'of' needs a 'pick' of 'highest' or 'lowest'.";
            return false;
        }
        bool highest = pick == "highest";
        int count = of.count ?? 1;

        if (!TryResolveScope(of.rows, false, rowMin, rowMax, result, out List<int> rows)) return false;
        if (!TryResolveScope(of.columns, true, colMin, colMax, result, out List<int> colBlocks)) return false;
        List<int> cols = ExpandBlocks(true, colBlocks, colMin, colMax);

        var hits = new List<KeyValuePair<double, ResolvedTarget>>();
        foreach (int r in rows)
            foreach (int c in cols) {
                if (!TryCellValue(data, r, c, out double v)) continue;
                hits.Add(new KeyValuePair<double, ResolvedTarget>(v, new ResolvedTarget {
                    visRow = r, visCol = c, hasRow = true, hasCol = true }));
            }

        if (hits.Count == 0) { result["error"] = "No cell in that scope had a value to compare."; return false; }

        hits.Sort((a, b) => highest ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key));
        int take = System.Math.Min(count, hits.Count);

        var picked = new List<object>(take);
        for (int i = 0; i < take; i++) {
            into.Add(hits[i].Value);
            picked.Add(new Dictionary<string, object> {
                { "row", data.TitleAt(false, hits[i].Value.visRow) },
                { "column", data.TitleAt(true, hits[i].Value.visCol) },
                { "value", System.Math.Round(hits[i].Key, 2) }
            });
        }

        result["pick"] = highest ? "highest" : "lowest";
        result["picked"] = picked;
        return true;
    }

}
