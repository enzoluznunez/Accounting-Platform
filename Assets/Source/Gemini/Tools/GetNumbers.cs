using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class GetNumbers : AgenticTool<GetNumbers.Args> {

    private const int MaxCells = 100;

    public class Args {
        [Doc("The row to read: its name, or its 1-based position. Give it with 'column' for one cell, or alone to read across the row."), Optional]
        public string row;
        [Doc("The metric to read: its name, or its 1-based position. Give it with 'row' for one company's figures, or alone to read down the metric."), Optional]
        public string column;
        [Doc("Which year of a paired metric to read, such as '2019'. Leave it out to get both years."), Optional]
        public string year;
        [Doc("A sheet id from ListDatasets, to read only that piece. Omit to read the whole dataset."), Optional]
        public int? sheet;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "GetNumbers",
        Description = "Read the numbers behind the bars: the cell values of the open dataset. " +
                      "Give 'row' and 'column' together for a single cell, 'row' alone to read across that row, " +
                      "'column' alone to read down that column, or neither to read the whole block; a block over " +
                      "100 cells is refused, so read a large sheet a row or column at a time. " +
                      "When the sheet pairs its columns, 'column' names the metric and covers both its years; add " +
                      "'year' to read just one of them. " +
                      "Rows and columns take a name or a 1-based position, and readings come back in display order, " +
                      "so a Sort reorder is reflected in them. A cell with no value reads as null, never as zero. " +
                      "This is the source for anything numeric: read the values you need and work out totals, averages, " +
                      "differences, percentages and comparisons from them. " +
                      "Values are exact; the spreadsheet the user sees abbreviates them (1.2M), so round when you read " +
                      "one aloud. Pass 'sheet' to read one piece of a sliced sheet; omit it for the whole dataset.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var data = Scene.Data;
        var mgr = Scene.Sheets;
        if (data == null || mgr == null || !mgr.IsBuilt) { result["error"] = "No sheet in scene."; return; }

        int id = args.sheet ?? ManageSheets.WholeSheetId;
        bool whole = id == ManageSheets.WholeSheetId;
        CreateSheet piece = whole ? null : mgr.SheetById(id);

        if (!whole && piece == null) {
            result["error"] = $"There is no sheet #{id} on the open dataset; call ListDatasets for current ids.";
            return;
        }

        int rowMin = piece != null ? piece.rowMin : 0;
        int rowMax = piece != null ? piece.rowMax : mgr.RowCount - 1;
        int colMin = piece != null ? piece.colMin : 0;
        int colMax = piece != null ? piece.colMax : mgr.ColCount - 1;

        bool hasRow = !string.IsNullOrWhiteSpace(args.row);
        bool hasColumn = !string.IsNullOrWhiteSpace(args.column);

        int visRow = -1;
        int colLo = colMin, colHi = colMax;
        if (hasRow && !TryResolveLine(args.row, false, rowMin, rowMax, result, out visRow)) return;
        if (hasColumn) {
            if (!TryResolveLine(args.column, true, colMin, colMax, result, out int block)) return;
            BlockSpan(true, block, colMin, colMax, out colLo, out colHi);

            // A named year narrows a paired metric to the one cell it asks for.
            if (!string.IsNullOrWhiteSpace(args.year)) {
                if (!TryResolveSeries(args.year, true, result, out int series)) return;
                if (series >= 0) colLo = colHi = System.Math.Min(colLo + series, colHi);
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.year)) {
            result["error"] = "'year' picks one column of a metric, so give 'column' as well.";
            return;
        }

        if (piece != null) result["sheet"] = id;

        bool wholeRows = rowMin == 0 && rowMax == mgr.RowCount - 1;
        bool wholeColumns = colMin == 0 && colMax == mgr.ColCount - 1;

        if (hasRow && hasColumn) {
            if (colLo == colHi) Cell(data, visRow, colLo, result);
            else Block(data, visRow, visRow, colLo, colHi, result);
        }
        else if (hasRow) {
            Line(data, false, visRow, colMin, colMax, result);
            if (wholeColumns) NoteRefreshed(true);
        }
        else if (hasColumn) {
            if (colLo == colHi) Line(data, true, colLo, rowMin, rowMax, result);
            else Block(data, rowMin, rowMax, colLo, colHi, result);
            if (wholeRows) NoteRefreshed(false);
        }
        else {
            int cells = (rowMax - rowMin + 1) * (colMax - colMin + 1);
            if (cells > MaxCells) {
                result["error"] = $"That block has {cells} cells, more than the {MaxCells} this tool returns " +
                                  "at once; read it a row or column at a time, or use GetStatistics for totals " +
                                  "and averages.";
                return;
            }
            Block(data, rowMin, rowMax, colMin, colMax, result);
            if (wholeRows) NoteRefreshed(false);
            if (wholeColumns) NoteRefreshed(true);
        }
    }

    private static void Cell(DataSource data, int visRow, int visCol, Dictionary<string, object> result) {
        result["row"] = data.TitleAt(false, visRow);
        result["column"] = data.TitleAt(true, visCol);

        if (TryValue(data, visRow, visCol, out double value)) result["value"] = Round(value);
        else {
            result["value"] = null;
            result["note"] = "That cell has no value.";
        }
    }

    private static void Line(DataSource data, bool downColumn, int line, int min, int max,
        Dictionary<string, object> result) {
        result[downColumn ? "column" : "row"] = data.TitleAt(downColumn, line);

        var titles = new List<object>();
        var values = new List<object>();

        for (int v = min; v <= max; v++) {
            int visRow = downColumn ? v : line;
            int visCol = downColumn ? line : v;
            titles.Add(data.TitleAt(!downColumn, v));
            values.Add(TryValue(data, visRow, visCol, out double value) ? Round(value) : null);
        }

        result[downColumn ? "rows" : "columns"] = titles;
        result["values"] = values;
    }

    private static void Block(DataSource data, int rowMin, int rowMax, int colMin, int colMax,
        Dictionary<string, object> result) {
        var rowTitles = new List<object>();
        var grid = new List<object>();

        for (int vr = rowMin; vr <= rowMax; vr++) {
            rowTitles.Add(data.TitleAt(false, vr));

            var line = new List<object>();
            for (int vc = colMin; vc <= colMax; vc++)
                line.Add(TryValue(data, vr, vc, out double value) ? Round(value) : null);
            grid.Add(line);
        }

        var columnTitles = new List<object>();
        for (int vc = colMin; vc <= colMax; vc++) columnTitles.Add(data.TitleAt(true, vc));

        result["rows"] = rowTitles;
        result["columns"] = columnTitles;
        result["values"] = grid;
        result["note"] = "'values' is one list per row, in the same order as 'rows', each aligned to 'columns'.";
    }

    private static bool TryValue(DataSource data, int visRow, int visCol, out double value) {
        value = 0d;

        IReadOnlyList<int> rowOrder = data.RowOrder;
        IReadOnlyList<int> colOrder = data.ColumnOrder;
        int dataRow = visRow >= 0 && visRow < rowOrder.Count ? rowOrder[visRow] : -1;
        int dataCol = visCol >= 0 && visCol < colOrder.Count ? colOrder[visCol] : -1;

        if (dataRow < 0 || dataCol < 0 || !data.HasValue(dataRow, dataCol)) return false;

        value = data.GetValue(dataRow, dataCol);
        return true;
    }
}
