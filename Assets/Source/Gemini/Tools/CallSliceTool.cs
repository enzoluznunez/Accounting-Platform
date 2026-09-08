using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class CallSliceTool : AgenticTool<CallSliceTool.Args> {

    public class Args {
        [Doc("Cut just after this metric/row of the target piece: its name, or its 1-based position."), Optional]
        public string after;
        [Doc("Make several cuts in one go: each a name or 1-based position to cut after. Use this instead of calling repeatedly, because every cut renumbers the pieces."), Optional]
        public string[] cuts;
[Doc("Whether this works on columns or rows. Leave it out when the name you gave already says which."), Values("columns", "rows"), Optional]
        public string axis;
        [Doc("Target piece."), Optional]
        public int? sheet;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallSliceTool",
        Description = "Cut a sheet piece, as if the user touched the cut line. Pass 'axis' when the name you give does " +
                      "not say which. On columns, after=N cuts between column N and N+1; on rows, between row N and " +
                      "N+1. Names work as well as numbers. " +
                      "When the sheet pairs its columns a cut falls between whole metrics, never between a metric's " +
                      "two years, and positions count metrics. " +
                      "Use 'cuts' to make several cuts at once and never call this " +
                      "repeatedly for one request: each cut renumbers the pieces, so a second call would be aiming at a " +
                      "layout that no longer exists. " +
                      "The result lists the resulting pieces in order along the axis.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Slice, result)) return;

        var slice = Scene.Slice;
        if (slice == null) { result["error"] = "Slice tool not found in scene."; return; }

        bool many = args.cuts != null && args.cuts.Length > 0;
        if (many && !string.IsNullOrEmpty(args.after)) {
            result["error"] = "Give either 'after' for one cut or 'cuts' for several, not both.";
            return;
        }
        if (!many && string.IsNullOrEmpty(args.after)) {
            result["error"] = "Give 'after' to cut once, or 'cuts' to make several cuts at once.";
            return;
        }

        string hint = many ? args.cuts[0] : args.after;
        if (!ResolveAxis("slice", args.axis, hint, result, out bool columns)) return;
        string axis = columns ? "columns" : "rows";

        if (!TryResolvePiece(args.sheet, result, "slice", out var mgr, out var sheet, out int pieceId)) return;

        int lineMin = columns ? sheet.colMin : sheet.rowMin;
        int lineMax = columns ? sheet.colMax : sheet.rowMax;
        var data = Scene.Data;
        string what = DataSource.GroupNoun(data, columns);

        // Cuts are chosen in whole blocks, so a paired axis is only ever cut
        // between metrics.
        int blockMin = data != null ? data.GroupOf(columns, lineMin) : lineMin;
        int span = BlockCountIn(columns, lineMin, lineMax);
        if (span < 2) {
            result["error"] = $"That piece has only one {what}; it cannot be sliced that way.";
            return;
        }

        var blocks = new List<int>();
        if (many) {
            if (!TryResolveLines(args.cuts, columns, lineMin, lineMax, result, out List<int> resolved)) return;
            blocks.AddRange(resolved);
        }
        else {
            if (!TryResolveLine(args.after, columns, lineMin, lineMax, result, out int one)) return;
            blocks.Add(one);
        }

        foreach (int block in blocks)
            if (block - blockMin + 1 > span - 1) {
                result["error"] = $"Cannot cut after the last {what} of that piece.";
                return;
            }

        blocks.Sort();

        // A cut lands after the block's final line.
        var lines = new List<int>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) {
            BlockSpan(columns, blocks[i], lineMin, lineMax, out _, out int hi);
            lines.Add(hi);
        }

        var madeAt = new List<object>();
        var pieces = new List<object>();
        CreateSheet target = sheet;

        bool ok = RunGrouped(lines.Count, i => {
            if (target == null) return true;
            if (!slice.CutAt(columns, target, lines[i], out SliceRecord record)) {
                result["error"] = madeAt.Count == 0
                    ? "The cut could not be made there."
                    : $"Made {madeAt.Count} cut(s), then one could not be made; the sheet is part-way through.";
                if (madeAt.Count > 0) result["cutsMade"] = madeAt;
                return false;
            }
            madeAt.Add(blocks[i] - blockMin + 1);
            pieces.Add(record.aId);
            target = mgr.SheetById(record.bId);
            return true;
        });
        if (!ok) return;

        if (target != null) pieces.Add(target.sheetId);

        result["sliced"] = axis;
        result["sheet"] = pieceId;
        result["cutsMade"] = madeAt;
        result["sheets"] = pieces;
        result["pieceCount"] = mgr.Sheets.Count;
        result["note"] = $"Pieces run in {axis} order: {string.Join(", ", pieces)}.";
    }
}
