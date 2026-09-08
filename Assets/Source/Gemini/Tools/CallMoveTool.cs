using System;
using System.Collections.Generic;
using Google.GenAI.Types;
using UnityEngine;

public sealed class CallMoveTool : AgenticTool<CallMoveTool.Args> {

    private const float DefaultDistance = 0.15f;
    private const float MinDistance = 0.001f;
    private const float MaxDistance = 100f;
    private const float GapCells = 0.5f;

    public class Args {
        [Doc("Which way to slide the piece, from the user's point of view; 'forward' is away from them. " +
             "With 'beside', this is which side of the other piece to sit on instead.")]
        [Values("left", "right", "forward", "back", "up", "down",
                "forward-left", "forward-right", "back-left", "back-right")]
        public string direction;
        [Doc("How far to slide, in meters, from a millimetre upward. Ignored when 'beside' is given."), Limits(0.001, 100), DefaultsTo(0.15), Optional]
        public float? distance;
        [Doc("Sit the piece against this other piece instead of sliding a set distance: a sheet id from " +
             "ListDatasets, with 'direction' saying which of its sides to sit on."), Optional]
        public int? beside;
        [Doc("Lay several pieces out in one go: sheet ids in the order you want them, each set down against the " +
             "previous one on 'direction'. Use this instead of calling repeatedly, because every placement moves " +
             "the piece the next one measures from."), Optional]
        public int[] layout;
        [Doc("The piece to move."), Optional]
        public int? sheet;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallMoveTool",
        Description = "Move a sheet piece, as if the user grabbed and slid it. Either slide it a distance in a " +
                      "direction, including diagonals, or pass 'beside' with another piece's id to set it down against " +
                      "that piece's side, which is how to lay two pieces out after slicing. Pieces pass freely through " +
                      "one another.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Move, result)) return;

        bool laying = args.layout != null && args.layout.Length > 0;
        if (laying && args.beside.HasValue) {
            result["error"] = "Give either 'beside' for one piece or 'layout' for several, not both.";
            return;
        }

        var manager = Scene.Sheets;
        if (manager == null || !manager.IsBuilt) { result["error"] = "The sheet is not ready to move."; return; }

        string dir = args.direction?.Trim().ToLowerInvariant();
        if (!TryWorldDirection(dir, out Vector3 dirVec)) {
            result["error"] = $"Unknown direction '{args.direction}'. Use left, right, forward, back, up, down, " +
                              "or a diagonal such as forward-left.";
            return;
        }

        if (laying) { Layout(manager, args.layout, dir, dirVec, result); return; }

        if (!TryResolvePiece(args.sheet, result, "move", out var mgr, out var sheet, out int pieceId)) return;

        if (args.beside.HasValue) {
            Beside(mgr, sheet, pieceId, args.beside.Value, dir, dirVec, result);
            return;
        }

        float asked = args.distance ?? DefaultDistance;
        float distance = Mathf.Clamp(asked, MinDistance, MaxDistance);
        Slide(mgr, sheet, pieceId, dir, dirVec * distance, result);
        result["requestedMeters"] = Round(asked);
        if (distance != asked)
            result["note"] = $"The distance was limited to the {MinDistance} to {MaxDistance} meter range; {distance} m was used.";
    }

    private static void Layout(ManageSheets mgr, int[] ids, string direction, Vector3 worldDir,
        Dictionary<string, object> result) {

        if (!TryResolvePieces(mgr, ids, result, out List<CreateSheet> order)) return;

        var placed = new List<object> { order[0].sheetId };
        bool ok = RunGrouped(order.Count - 1, i => {
            CreateSheet piece = order[i + 1];
            var step = new Dictionary<string, object>();
            Beside(mgr, piece, piece.sheetId, order[i].sheetId, direction, worldDir, step);
            if (step.ContainsKey("error")) {
                result["error"] = $"Laid out {placed.Count} piece(s), then #{piece.sheetId} could not be placed: {step["error"]}";
                result["placed"] = placed;
                return false;
            }
            placed.Add(piece.sheetId);
            return true;
        });
        if (!ok) return;

        result["laidOut"] = placed;
        result["direction"] = direction;
        result["note"] = $"Pieces run {direction} in that order, each against the previous one.";
    }

    private static void Beside(ManageSheets mgr, CreateSheet sheet, int pieceId, int targetId,
        string direction, Vector3 worldDir, Dictionary<string, object> result) {

        if (targetId == pieceId) { result["error"] = "A piece cannot be set down beside itself."; return; }

        CreateSheet target = mgr.SheetById(targetId);
        if (target == null) {
            result["error"] = MissingPiece(targetId);
            return;
        }

        mgr.CompletePieceMotion(target);

        float gap = mgr.CellSize * GapCells * Mathf.Abs(mgr.transform.lossyScale.x);
        float reach = ExtentAlong(mgr, target, worldDir) + ExtentAlong(mgr, sheet, worldDir) + gap;
        Vector3 destination = target.transform.position + worldDir * reach;

        Slide(mgr, sheet, pieceId, direction, destination - sheet.transform.position, result);
        result["beside"] = targetId;
    }

    private static void Slide(ManageSheets mgr, CreateSheet sheet, int pieceId, string direction,
        Vector3 worldDelta, Dictionary<string, object> result) {

        var pre = ApplyPieceTransform(mgr, sheet, t =>
            t.localPosition += mgr.transform.InverseTransformVector(worldDelta));

        mgr.GetCommittedPose(sheet, out Vector3 nowPos, out _, out _);
        float actual = mgr.transform.TransformVector(nowPos - pre.pos).magnitude;
        result["moved"] = direction;
        result["sheet"] = pieceId;
        result["actualMeters"] = Round(actual);
    }

    private static float ExtentAlong(ManageSheets mgr, CreateSheet piece, Vector3 worldDir) {
        Transform t = piece.transform;
        Vector3 scale = t.lossyScale;

        float halfColumns = piece.ColumnExtent * 0.5f * Mathf.Abs(scale.x);
        float halfUp = mgr.Height * 0.5f * Mathf.Abs(scale.y);
        float halfRows = piece.RowExtent * 0.5f * Mathf.Abs(scale.z);

        return Mathf.Abs(Vector3.Dot(worldDir, t.right)) * halfColumns
             + Mathf.Abs(Vector3.Dot(worldDir, t.up)) * halfUp
             + Mathf.Abs(Vector3.Dot(worldDir, t.forward)) * halfRows;
    }
}
