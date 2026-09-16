using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;
using UnityEngine.Profiling;

public abstract class AgenticTool : Function {

    private static int refreshedAxes;

    private readonly string sample;

    protected AgenticTool() {
        sample = "GeminiTool." + GetType().Name;
    }

    protected abstract void Run(Dictionary<string, object> args, Dictionary<string, object> result);

    private static readonly string[] RefusalKeys =
        { "error", "preconditionUnmet", "needsSheet", "needsChoice" };

    protected static bool IsRefusal(Dictionary<string, object> result) {
        for (int i = 0; i < RefusalKeys.Length; i++)
            if (result.ContainsKey(RefusalKeys[i])) return true;
        return false;
    }

    protected virtual bool EditsAreOutcome => false;

    protected static void NoteRefreshed(bool columns) => refreshedAxes |= columns ? 2 : 1;

    protected override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var result = new Dictionary<string, object>();
        var total = System.Diagnostics.Stopwatch.StartNew();
        long runMs = 0;
        int editsBefore = 0, editsAfter = 0;

        await MainThread.Run(() => {
            var run = System.Diagnostics.Stopwatch.StartNew();
            Profiler.BeginSample(sample);
            StateChannel.InAgentCall = true;
            try {
                AgentTurn.NoteToolCall();
                Scene.Sort?.CompleteOrderSequence();
                editsBefore = ManageDatasets.ActiveEdits != null ? ManageDatasets.ActiveEdits.Count : 0;
                refreshedAxes = 0;
                Run(args ?? new Dictionary<string, object>(), result);
            }
            catch (Exception e) {
                result.Clear();
                result["error"] = e.Message;
            }
            finally {
                if (!IsRefusal(result)) {
                    if ((refreshedAxes & 1) != 0) StalePositions.Clear(false);
                    if ((refreshedAxes & 2) != 0) StalePositions.Clear(true);
                }
                editsAfter = ManageDatasets.ActiveEdits != null ? ManageDatasets.ActiveEdits.Count : 0;
                StateChannel.InAgentCall = false;
                string did = StateChannel.TakeAgentBatch();
                if (!string.IsNullOrEmpty(did)) result["did"] = did;
                Profiler.EndSample();
                run.Stop();
                runMs = run.ElapsedMilliseconds;
            }
        });
        total.Stop();

        bool changed = editsAfter != editsBefore;
        if (EditsAreOutcome) result["changed"] = changed;

        if (!IsRefusal(result) && !result.ContainsKey("ok")) {
            result["ok"] = !EditsAreOutcome || changed;
            if (EditsAreOutcome && !changed && !result.ContainsKey("note"))
                result["note"] = "Nothing changed; the sheet is already the way this call would leave it.";
        }

        UnityEngine.Debug.Log($"[Gemini][tool] {Name} args={Brief(args)} -> {Brief(result)} " +
            $"({total.ElapsedMilliseconds} ms, run {runMs} ms, edits {editsBefore}->{editsAfter})");

        return result;
    }

    private static string Brief(Dictionary<string, object> map) {
        if (map == null || map.Count == 0) return "{}";
        try {
            string head = BriefJson(map, 300, out long totalBytes);
            return totalBytes > 300 ? head + "..." : head;
        }
        catch (Exception e) { return "<unserializable: " + e.Message + ">"; }
    }

    protected static Schema ParametersFor(System.Type args) => ToolArguments.Schema(args);

    protected static bool TryResolveTargetBounds(int? sheet, Dictionary<string, object> result,
        out int rowMin, out int rowMax, out int colMin, out int colMax, out int pieceId) {
        rowMin = rowMax = colMin = colMax = 0;
        pieceId = 0;

        var mgr = Scene.Sheets;
        if (mgr == null || !mgr.IsBuilt) { result["error"] = "No sheet in scene."; return false; }
        if (!mgr.IsPresented) {
            Refuse(result, "visible sheet",
                "The sheet is hidden because the dataset is collapsed. Reopen it with SetDataset, then call this again.");
            return false;
        }

        var list = mgr.Sheets;
        CreateSheet piece;

        if (sheet.HasValue) {
            pieceId = sheet.Value;
            piece = mgr.SheetById(pieceId);
            if (piece == null) { result["error"] = $"There is no sheet piece #{pieceId}."; return false; }
        }
        else if (list.Count == 1) { piece = list[0]; pieceId = piece.sheetId; }
        else {
            result["needsSheet"] = true;
            result["message"] = "The sheet is sliced into several pieces; ask the user which one.";
            var pieces = new List<object>();
            for (int i = 0; i < list.Count; i++) {
                var s = list[i];
                pieces.Add(new Dictionary<string, object> {
                    { "id", s.sheetId },
                    { "cellCount", s.RowCount * s.ColCount }
                });
            }
            result["sheets"] = pieces;
            return false;
        }

        rowMin = piece.rowMin; rowMax = piece.rowMax; colMin = piece.colMin; colMax = piece.colMax;
        return true;
    }

    // On a grouped axis the agent addresses metrics, not raw columns: names match
    // the metric, numbers count metrics, and the value returned is a block index.
    // Rows and ungrouped columns have one line per block, so this is unchanged for
    // them and every caller below reads the same either way.
    protected static bool TryResolveLine(object arg, bool columns, int min, int max,
        Dictionary<string, object> result, out int block) {
        block = -1;
        string token = AsString(arg)?.Trim();
        var data = Scene.Data;
        string what = DataSource.GroupNoun(data, columns);
        if (string.IsNullOrEmpty(token)) { result["error"] = $"Provide a {what} number or name."; return false; }

        int size = data != null ? data.GroupSize(columns) : 1;
        int blockMin = min / size;
        int blockMax = max / size;
        int span = blockMax - blockMin + 1;

        if (int.TryParse(token, out int number)) {
            if (StalePositions.IsDirty(columns)) {
                StalePositions.Clear(columns);
                var live = Scene.Data;
                var current = new List<string>();
                if (live != null) {
                    int liveBlocks = live.GroupCount(columns);
                    for (int b = blockMin; b <= blockMax && b < liveBlocks; b++)
                        current.Add(DataSource.GroupLabelAt(live, columns, b));
                }
                result["error"] = $"The {what} positions have shifted: the user reordered the {what}s since you " +
                    $"last read them. They now run: {string.Join(", ", current)}. Name the {what} you mean, or " +
                    "give its position in this order.";
                return false;
            }
            if (number < 1 || number > span) {
                result["error"] = $"There is no {what} {number} there; it has {span}.";
                return false;
            }
            block = blockMin + number - 1;
            return true;
        }

        if (data == null) { result["error"] = "No dataset is open."; return false; }

        int found = -1, hits = 0;
        var names = new List<object>();
        for (int b = blockMin; b <= blockMax; b++) {
            string title = data.GroupTitleAt(columns, b) ?? "";
            names.Add(title);
            if (!string.Equals(title, token, StringComparison.OrdinalIgnoreCase)) continue;
            found = b;
            hits++;
        }

        if (hits == 0 && token.Length >= 3) {
            for (int b = blockMin; b <= blockMax; b++) {
                string title = data.GroupTitleAt(columns, b) ?? "";
                if (!title.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
                found = b;
                hits++;
            }
        }

        if (hits == 1) { block = found; return true; }
        if (hits > 1) {
            result["error"] = $"More than one {what} is called '{token}'; ask the user which, or give its number.";
            return false;
        }
        string other = DataSource.GroupNoun(data, !columns);
        result["error"] = TitleExistsOnAxis(token, !columns)
            ? $"No {what} called '{token}', but there is a {other} called '{token}'; pass it as '{other}' instead."
            : $"No {what} called '{token}'.";
        result[columns ? "columns" : "rows"] = names;
        return false;
    }

    // The line span one block covers, clipped to the piece.
    protected static void BlockSpan(bool columns, int block, int min, int max, out int lo, out int hi) {
        var data = Scene.Data;
        int size = data != null ? data.GroupSize(columns) : 1;
        lo = Math.Max(block * size, min);
        hi = Math.Min(block * size + size - 1, max);
    }

    // Every line the given blocks cover, clipped to the piece.
    protected static List<int> ExpandBlocks(bool columns, IReadOnlyList<int> blocks, int min, int max) {
        var lines = new List<int>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) {
            BlockSpan(columns, blocks[i], min, max, out int lo, out int hi);
            for (int v = lo; v <= hi; v++) lines.Add(v);
        }
        return lines;
    }

    protected static int BlockCountIn(bool columns, int min, int max) {
        var data = Scene.Data;
        int size = data != null ? data.GroupSize(columns) : 1;
        return (max / size) - (min / size) + 1;
    }

    // Which cell of a block a spoken series name picks out, such as a year on a
    // paired axis. Returns false when the dataset has no series to choose from.
    protected static bool TryResolveSeries(string token, bool columns, Dictionary<string, object> result,
        out int series) {
        series = -1;
        var data = Scene.Data;
        if (data == null || !data.IsGrouped(columns)) {
            result["error"] = "This dataset has one cell per line, so there is no year to choose.";
            return false;
        }

        IReadOnlyList<string> names = data.SeriesTitles;
        token = token?.Trim();
        if (string.IsNullOrEmpty(token)) { series = -1; return true; }

        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], token, StringComparison.OrdinalIgnoreCase)) { series = i; return true; }

        for (int i = 0; i < names.Count; i++)
            if (names[i] != null && names[i].EndsWith(token, StringComparison.OrdinalIgnoreCase)) { series = i; return true; }

        result["error"] = $"'{token}' is not one of this sheet's columns; it has {string.Join(" and ", names)}.";
        result["series"] = new List<object>(names);
        return false;
    }

    protected static bool TryResolveScope(string[] names, bool columns, int min, int max,
        Dictionary<string, object> result, out List<int> lines) {

        if (names == null || names.Length == 0) {
            var data = Scene.Data;
            int size = data != null ? data.GroupSize(columns) : 1;
            lines = new List<int>(BlockCountIn(columns, min, max));
            for (int b = min / size; b <= max / size; b++) lines.Add(b);
            return true;
        }
        return TryResolveLines(names, columns, min, max, result, out lines);
    }

    protected static bool TryResolveLines(IReadOnlyList<string> tokens, bool columns, int min, int max,
        Dictionary<string, object> result, out List<int> visIndexes) {
        visIndexes = null;
        string what = DataSource.GroupNoun(Scene.Data, columns);

        if (tokens == null || tokens.Count == 0) {
            result["error"] = $"Provide at least one {what} name or number.";
            return false;
        }

        int span = BlockCountIn(columns, min, max);
        if (tokens.Count > span) {
            result["error"] = $"That is {tokens.Count} entries but there are only {span} {what}s.";
            return false;
        }

        var resolved = new List<int>(tokens.Count);
        var seen = new HashSet<int>();
        for (int i = 0; i < tokens.Count; i++) {
            if (!TryResolveLine(tokens[i], columns, min, max, result, out int vis)) return false;
            if (!seen.Add(vis)) {
                result["error"] = $"'{tokens[i]}' is listed more than once; each {what} may appear only once.";
                return false;
            }
            resolved.Add(vis);
        }

        visIndexes = resolved;
        return true;
    }

    protected static bool TryInferAxis(string token, out bool columns) {
        columns = false;
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || int.TryParse(token, out _)) return false;

        bool onColumns = TitleExistsOnAxis(token, true);
        bool onRows = TitleExistsOnAxis(token, false);
        if (onColumns == onRows) return false;

        columns = onColumns;
        return true;
    }

    protected static string MissingPiece(int id) =>
        $"There is no sheet piece #{id} on the open dataset; call ListDatasets for current ids.";

    protected static bool TryResolvePieces(ManageSheets mgr, int[] ids,
        Dictionary<string, object> result, out List<CreateSheet> pieces) {
        pieces = null;
        var seen = new HashSet<int>();
        var found = new List<CreateSheet>(ids.Length);
        foreach (int id in ids) {
            if (!seen.Add(id)) { result["error"] = $"Piece #{id} is listed more than once."; return false; }
            CreateSheet piece = mgr.SheetById(id);
            if (piece == null) { result["error"] = MissingPiece(id); return false; }
            found.Add(piece);
        }
        pieces = found;
        return true;
    }

    protected static bool RunGrouped(int count, Func<int, bool> act) {
        var edits = ManageDatasets.ActiveEdits;
        if (count > 1) edits.OpenGroup();
        try {
            for (int i = 0; i < count; i++)
                if (!act(i)) return false;
        }
        finally { edits.CloseGroup(); }
        return true;
    }

    protected static bool ForEachPiece(int[] ids, Dictionary<string, object> result, string verb,
        Func<CreateSheet, Dictionary<string, object>, bool> act) {
        var mgr = Scene.Sheets;
        if (mgr == null || !mgr.IsBuilt) { result["error"] = $"The sheet is not ready to {verb}."; return false; }

        if (!TryResolvePieces(mgr, ids, result, out List<CreateSheet> pieces)) return false;

        var done = new List<object>();
        bool ok = RunGrouped(pieces.Count, i => {
            CreateSheet piece = pieces[i];
            var step = new Dictionary<string, object>();
            if (!act(piece, step)) {
                bool prompted = step.Count > 0 && !step.ContainsKey("error");
                if (prompted) {
                    foreach (var entry in step) result[entry.Key] = entry.Value;
                    if (done.Count > 0)
                        result["note"] = $"Handled {done.Count} piece(s) before this was needed.";
                }
                else {
                    result["error"] = done.Count == 0
                        ? (step.TryGetValue("error", out var e) ? e.ToString() : $"Could not {verb} piece #{piece.sheetId}.")
                        : $"Handled {done.Count} piece(s), then #{piece.sheetId} failed.";
                }
                if (done.Count > 0) result["sheets"] = done;
                return false;
            }
            done.Add(piece.sheetId);
            return true;
        });
        if (!ok) return false;

        result["sheets"] = done;
        return true;
    }

    protected static void NeedChoice(Dictionary<string, object> result, string what,
        IReadOnlyList<string> options, string message) {
        result["needsChoice"] = what;
        if (options != null && options.Count > 0) result["options"] = new List<object>(options);
        result["message"] = message;
    }

    protected static bool TitleExistsOnAxis(string token, bool columns) {
        var data = Scene.Data;
        if (data == null) return false;

        if (data.IsGrouped(columns)) {
            int blocks = data.GroupCount(columns);
            for (int b = 0; b < blocks; b++)
                if (string.Equals(data.GroupTitleAt(columns, b), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        IReadOnlyList<string> titles = columns ? data.ColumnTitles : data.RowTitles;
        for (int i = 0; i < titles.Count; i++)
            if (string.Equals(titles[i], token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    protected static bool TryWorldDirection(string direction, out UnityEngine.Vector3 world) {
        world = UnityEngine.Vector3.zero;
        if (direction == "up") { world = UnityEngine.Vector3.up; return true; }
        if (direction == "down") { world = UnityEngine.Vector3.down; return true; }

        UnityEngine.Transform cam = CameraRig.MainTransform;
        if (cam == null) return false;

        UnityEngine.Vector3 forward = CameraRig.Flatten(cam.forward, UnityEngine.Vector3.forward);
        UnityEngine.Vector3 right = UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, forward).normalized;

        switch (direction) {
            case "forward": world = forward; return true;
            case "back": world = -forward; return true;
            case "left": world = -right; return true;
            case "right": world = right; return true;
            case "forward-left": world = (forward - right).normalized; return true;
            case "forward-right": world = (forward + right).normalized; return true;
            case "back-left": world = (-forward - right).normalized; return true;
            case "back-right": world = (-forward + right).normalized; return true;
        }
        return false;
    }

    protected static bool TryGet(Dictionary<string, object> args, string key, out object value) {
        value = null;
        return args != null && args.TryGetValue(key, out value) && value != null;
    }

    protected static string AsString(object v) {
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return v?.ToString();
    }

    protected static object Round(double v) {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        if (v > (double)decimal.MaxValue || v < (double)decimal.MinValue) return null;
        return (decimal)Math.Round(v, 4);
    }

    protected static bool TryParseTool(string s, out ToolType tool) {
        tool = ToolType.None;
        if (string.IsNullOrEmpty(s)) return false;
        switch (s.Trim().ToLowerInvariant()) {
            case "none": tool = ToolType.None; return true;
            case "filter": tool = ToolType.Filter; return true;
            case "slice": tool = ToolType.Slice; return true;
            case "color": case "colour": tool = ToolType.Color; return true;
            case "move": case "grab": tool = ToolType.Move; return true;
            case "rotate": tool = ToolType.Rotate; return true;
            case "scale": tool = ToolType.Scale; return true;
            case "sort": tool = ToolType.Sort; return true;
            case "profile": tool = ToolType.Profile; return true;
        }
        return false;
    }

    protected struct ResolvedTarget {
        public int visRow;
        public int visCol;
        public bool hasRow;
        public bool hasCol;
    }

    protected readonly struct PiecePose {
        public readonly UnityEngine.Vector3 pos;
        public readonly UnityEngine.Quaternion rot;
        public readonly UnityEngine.Vector3 scale;
        public PiecePose(UnityEngine.Transform t) { pos = t.localPosition; rot = t.localRotation; scale = t.localScale; }
    }

    protected static bool TryResolvePiece(int? id, Dictionary<string, object> result,
        string verb, out ManageSheets mgr, out CreateSheet sheet, out int pieceId) {
        mgr = Scene.Sheets;
        sheet = null;
        pieceId = 0;
        if (mgr == null || !mgr.IsBuilt) { result["error"] = $"The sheet is not ready to {verb}; is a sheet shown?"; return false; }
        if (!TryResolveTargetBounds(id, result, out _, out _, out _, out _, out pieceId)) return false;
        sheet = mgr.SheetById(pieceId);
        if (sheet == null) { result["error"] = "Could not resolve that piece."; return false; }
        return true;
    }

    protected static PiecePose ApplyPieceTransform(ManageSheets mgr, CreateSheet sheet, Action<UnityEngine.Transform> mutate) {
        mgr.CompletePieceMotion(sheet);
        UnityEngine.Transform t = sheet.transform;
        var pre = new PiecePose(t);
        mutate(t);
        mgr.NotifyMoveCommitted(sheet, pre.pos, pre.rot, pre.scale);
        mgr.AnimatePieceFrom(sheet, pre.pos, pre.rot, pre.scale);
        return pre;
    }

    protected static bool ResolveAxis(string toolName, string axisArg, string inferFrom,
        Dictionary<string, object> result, out bool columns) {
        columns = false;

        if (!string.IsNullOrEmpty(axisArg)) {
            string a = axisArg.Trim().ToLowerInvariant();
            if (a != "columns" && a != "rows") {
                result["error"] = $"'axis' must be 'columns' or 'rows', not '{axisArg}'.";
                return false;
            }
            columns = a == "columns";
            result["axisFrom"] = "given";
            return true;
        }

        if (TryInferAxis(inferFrom, out columns)) {
            result["axisFrom"] = "inferred";
            return true;
        }

        NeedChoice(result, "axis", new List<string> { "columns", "rows" },
            $"The {toolName} tool works on columns or rows and the name you gave does not say which. Ask the user which, then call this again with 'axis'.");
        return false;
    }

    protected static void Refuse(Dictionary<string, object> result, string unmet, string message) {
        result["preconditionUnmet"] = unmet;
        result["message"] = message;
    }

    protected static bool EnsureToolPanelOpen(Dictionary<string, object> result) {
        var panel = Scene.ToolPanel;
        if (panel == null) { result["error"] = "The tool panel was not found in the scene."; return false; }
        if (!panel.IsVisible) {
            if (AgentTurn.UserTookOver) {
                Refuse(result, "tool panel closed by the user",
                    "The user closed the tool panel while you were working, so it is not yours to reopen. " +
                    "Tell them what you had done and ask before carrying on.");
                return false;
            }
            panel.ShowPanel();
        }
        return true;
    }

    protected static bool EnsureToolSelected(ToolType tool, Dictionary<string, object> result) {
        if (!EnsureToolPanelOpen(result)) return false;

        var tools = Scene.Tools;
        if (tools == null) { result["error"] = "The tool manager was not found in the scene."; return false; }
        if (tools.SelectedTool != tool) {
            if (AgentTurn.UserTookOver) {
                Refuse(result, "tool changed by the user",
                    $"The user changed the tool while you were working, so the {tool} tool is no longer selected. " +
                    "Tell them what you had done and ask before carrying on.");
                return false;
            }
            tools.SelectTool(tool);
        }

        if (tools.SelectedTool != tool) {
            result["error"] = $"The {tool} tool could not be selected.";
            return false;
        }
        return true;
    }

    protected static bool TryCellValue(DataSource data, int visRow, int visCol, out double value) {
        value = 0d;
        if (data == null) return false;

        IReadOnlyList<int> rowOrder = data.RowOrder;
        IReadOnlyList<int> colOrder = data.ColumnOrder;
        int dataRow = visRow >= 0 && visRow < rowOrder.Count ? rowOrder[visRow] : -1;
        int dataCol = visCol >= 0 && visCol < colOrder.Count ? colOrder[visCol] : -1;

        if (dataRow < 0 || dataCol < 0 || !data.HasValue(dataRow, dataCol)) return false;

        value = data.GetValue(dataRow, dataCol);
        return true;
    }

    protected static bool TryParseMeasure(string raw, string owner,
        Dictionary<string, object> result, out string measure) {
        measure = raw?.Trim().ToLowerInvariant();
        if (measure == "sum" || measure == "average" || measure == "max" || measure == "min") return true;
        result["error"] = $"'{owner}' needs a 'measure': sum, average, max or min.";
        return false;
    }

    private struct MeasureAcc {
        private double sum, best, worst;
        private int n;

        public void Add(double v) {
            if (n == 0) { best = v; worst = v; }
            else { if (v > best) best = v; if (v < worst) worst = v; }
            sum += v;
            n++;
        }

        public bool TryScore(string measure, out double score) {
            score = 0d;
            if (n == 0) return false;
            switch (measure) {
                case "sum": score = sum; return true;
                case "average": score = sum / n; return true;
                case "max": score = best; return true;
                case "min": score = worst; return true;
                default: return false;
            }
        }
    }

    protected static bool TryLineMeasure(DataSource data, bool isColumn, int line,
        int crossMin, int crossMax, string measure, out double score) {

        var acc = new MeasureAcc();
        for (int j = crossMin; j <= crossMax; j++)
            if (TryCellValue(data, isColumn ? j : line, isColumn ? line : j, out double v)) acc.Add(v);
        return acc.TryScore(measure, out score);
    }

    protected static bool TryLineMeasure(DataSource data, bool isColumn, int line,
        IReadOnlyList<int> cross, string measure, out double score) {

        var acc = new MeasureAcc();
        for (int k = 0; k < cross.Count; k++) {
            int j = cross[k];
            if (TryCellValue(data, isColumn ? j : line, isColumn ? line : j, out double v)) acc.Add(v);
        }
        return acc.TryScore(measure, out score);
    }

    protected static string ActiveDatasetLabel() => Scene.DatasetLabel;
}

public abstract class AgenticTool<TArgs> : AgenticTool where TArgs : class, new() {

    protected abstract void Run(TArgs args, Dictionary<string, object> result);

    protected sealed override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        var bound = ToolArguments.Bind(typeof(TArgs), args, out string error) as TArgs;
        if (bound == null) { result["error"] = error; return; }
        Run(bound, result);
    }
}
