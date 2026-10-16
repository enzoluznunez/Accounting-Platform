using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class CallProfileTool : AgenticTool<CallProfileTool.Args> {

    public class Args {
        [Doc("Which row or column to pull: its name, or its 1-based position on the sheet."), Optional]
        public string index;
[Doc("Whether this works on columns or rows. Leave it out when the name you gave already says which."), Values("columns", "rows"), Optional]
        public string axis;
        [Doc("Pick the line by its numbers instead of by name: the tool reads them itself. " +
             "Use this for requests like 'show me the best month' rather than reading the values first."), Optional]
        public Of of;
    }

    public class Of {
        [Doc("Which number to judge each line by, worked out across the other axis."),
         Values("sum", "average", "max", "min")]
        public string measure;
        [Doc("'highest' takes the biggest, 'lowest' takes the smallest. Defaults to highest."),
         Values("highest", "lowest"), Optional]
        public string pick;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallProfileTool",
        Description = "Raise a whole row or column as a projection: a copy of that strip floating above it. Pass 'axis' " +
                      "when the name you give does not say which. The strip keeps the sheet's height scale, so its bars " +
                      "are comparable with the sheet it came from. It stays up after the tool is put away, is an edit " +
                      "on the undo timeline, and follows its values through a Sort reorder. Only one strip stands at a " +
                      "time: raising another replaces it, and undoing that brings the earlier one back. " +
                      "Pass 'of' to pick the line by its numbers: 'measure' (sum, average, max, min) and 'pick' " +
                      "(highest or lowest); the tool reads them itself, so profiling the strongest line is one " +
                      "call with no separate read.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Profile, result)) return;

        var profile = Scene.Profile;
        if (profile == null) { result["error"] = "Profile tool not found in scene."; return; }

        if (!ResolveAxis("profile", args.axis, args.index, result, out bool columns)) return;

        var mgr = Scene.Sheets;
        if (mgr == null || !mgr.IsBuilt) { result["error"] = "No sheet in scene."; return; }

        if (!TryResolveTargetBounds(result, out int rowMin, out int rowMax, out int colMin, out int colMax))
            return;

        int lineMin = columns ? colMin : rowMin;
        int lineMax = columns ? colMax : rowMax;
        int block;
        if (args.of != null) {
            int crossMin = columns ? rowMin : colMin;
            int crossMax = columns ? rowMax : colMax;
            if (!PickLine(args.of, columns, lineMin, lineMax, crossMin, crossMax, result, out block)) return;
        }
        else if (!TryResolveLine(args.index, columns, lineMin, lineMax, result, out block)) return;

        // One strip raises a whole paired metric, so any line inside the block
        // stands for it.
        var data = Scene.Data;
        int blockMin = data != null ? data.GroupOf(columns, lineMin) : lineMin;
        BlockSpan(columns, block, lineMin, lineMax, out int line, out _);

        int vr = columns ? (rowMin + rowMax) / 2 : line;
        int vc = columns ? line : (colMin + colMax) / 2;
        if (!profile.ShowProfile(columns, vr, vc)) {
            result["error"] = "That row or column could not be projected; it has no values, or it is already projected.";
            return;
        }

        result["projected"] = columns ? "column" : "row";
        result["index"] = data != null ? data.GroupOf(columns, line) - blockMin + 1 : line - lineMin + 1;
    }

    private static bool PickLine(Of of, bool columns, int lineMin, int lineMax, int crossMin, int crossMax,
        Dictionary<string, object> result, out int picked) {
        picked = -1;

        var data = Scene.Data;
        if (data == null) { result["error"] = "No data source found in scene."; return false; }

        // Ranking needs one measure in one unit. On a paired sheet the columns hold
        // different metrics, so neither axis can be ranked this way: judging metrics
        // against each other is meaningless, and judging a row means adding dollars
        // to share counts.
        if (data.IsGrouped(true)) {
            result["error"] = "The metrics on this sheet are in different units, so there is no single number to " +
                "judge lines by. Name the metric or row you want in 'index' instead.";
            return false;
        }

        if (!TryParseMeasure(of.measure, "of", result, out string measure)) return false;
        bool highest = !string.Equals(of.pick, "lowest", System.StringComparison.OrdinalIgnoreCase);

        if (crossMax < crossMin) { result["error"] = "The sheet has no values to judge by."; return false; }

        var scored = new List<KeyValuePair<double, int>>();
        for (int line = lineMin; line <= lineMax; line++) {
            if (!TryLineMeasure(data, columns, line, crossMin, crossMax, measure, out double score)) continue;
            scored.Add(new KeyValuePair<double, int>(score, line));
        }

        if (scored.Count == 0) { result["error"] = $"None of the {(columns ? "columns" : "rows")} had numbers to judge."; return false; }

        scored.Sort((a, b) => highest ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key));
        int best = scored[0].Value;

        // PickLine reads lines and the caller wants a block; on an ungrouped
        // axis, the only kind ranked here, the two are the same index.
        picked = data.GroupOf(columns, best);

        result["pickedBy"] = measure;
        result["pick"] = highest ? "highest" : "lowest";
        result["picked"] = data.TitleAt(columns, best);
        result["score"] = System.Math.Round(scored[0].Key, 2);
        return true;
    }

}
