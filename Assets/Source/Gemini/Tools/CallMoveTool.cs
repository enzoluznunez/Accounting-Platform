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
        [Doc("Which way to slide the sheet, from the user's point of view; 'forward' is away from them.")]
        [Values("left", "right", "forward", "back", "up", "down",
                "forward-left", "forward-right", "back-left", "back-right")]
        public string direction;
        [Doc("How far to slide, in meters, from a millimetre upward."), Limits(0.001, 100), DefaultsTo(0.15), Optional]
        public float? distance;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallMoveTool",
        Description = "Move the sheet, as if the user grabbed and slid it: a distance in a direction, including " +
                      "diagonals. It passes freely through anything in the room.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Move, result)) return;

        var manager = Scene.Sheets;
        if (manager == null || !manager.IsBuilt) { result["error"] = "The sheet is not ready to move."; return; }

        string dir = args.direction?.Trim().ToLowerInvariant();
        if (!TryWorldDirection(dir, out Vector3 dirVec)) {
            result["error"] = $"Unknown direction '{args.direction}'. Use left, right, forward, back, up, down, " +
                              "or a diagonal such as forward-left.";
            return;
        }

        if (!TryResolveSheet(result, "move", out var mgr, out var sheet)) return;

        float asked = args.distance ?? DefaultDistance;
        float distance = Mathf.Clamp(asked, MinDistance, MaxDistance);
        Slide(mgr, sheet, dir, dirVec * distance, result);
        result["requestedMeters"] = Round(asked);
        if (distance != asked)
            result["note"] = $"The distance was limited to the {MinDistance} to {MaxDistance} meter range; {distance} m was used.";
    }

    private static void Slide(ManageSheets mgr, CreateSheet sheet, string direction,
        Vector3 worldDelta, Dictionary<string, object> result) {

        var pre = ApplyPieceTransform(mgr, sheet, t =>
            t.localPosition += mgr.transform.InverseTransformVector(worldDelta));

        mgr.GetCommittedPose(sheet, out Vector3 nowPos, out _, out _);
        float actual = mgr.transform.TransformVector(nowPos - pre.pos).magnitude;
        result["moved"] = direction;
        result["actualMeters"] = Round(actual);
    }

}
