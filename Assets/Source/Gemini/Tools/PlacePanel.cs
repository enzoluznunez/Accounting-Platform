using System;
using System.Collections.Generic;
using Google.GenAI.Types;
using UnityEngine;

public sealed class PlacePanel : AgenticTool<PlacePanel.Args> {

    private const float DefaultDistance = 0.15f;
    private const float MinDistance = 0.02f;
    private const float MaxDistance = 1f;

    private const float DefaultDegrees = 30f;
    private const float MinDegrees = 1f;
    private const float MaxDegrees = 360f;

    public class Args {
        [Doc("'move' slides it, 'rotate' turns it, 'face' turns it square to the user without moving it."),
         Values("move", "rotate", "face")]
        public string action;
        [Doc("For 'move': left, right, forward, back, up or down, from the user's point of view. " +
             "For 'rotate': left or right."), Optional]
        public string direction;
        [Doc("For 'move': how far in meters (default 0.15, clamped 0.02-1)."), Optional]
        public float? distance;
        [Doc("For 'rotate': how far to turn in degrees (default 30, clamped 1-360)."), Optional]
        public float? degrees;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "PlacePanel",
        Description = "Place the tool panel in the room for the user, as if they grabbed its edge and moved it. " +
                      "'move' slides it, 'rotate' turns it about the upright axis, and 'face' squares it to the " +
                      "user without moving it. Only do this when the user asks for it. " +
                      "It stays upright and within arm's reach, and must be open before it can be placed. " +
                      "Check the latest '[tool]' or '[state]' message for the panel's state before calling: " +
                      "if the user closed the panel, do not place it blind; reopen it with SetPanel first, or ask. " +
                      "Placement is not an edit on the sheet's undo timeline, so Undo will not reverse it.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        PanelUI panel = Scene.ToolPanel;
        if (panel == null) { result["error"] = "The tool panel was not found in the scene."; return; }

        if (PanelGuard.ToolPanelClosedByUser && !panel.IsVisible) {
            Refuse(result, "panel closed by the user",
                "The user closed the tool panel by hand, so it is not yours to move. " +
                "Ask them, or reopen it with SetPanel first if they want it placed.");
            return;
        }

        if (!panel.IsVisible) {
            Refuse(result, "open tool panel",
                "The tool panel is closed, so there is nothing to place. Open it with SetPanel, then call this again.");
            return;
        }

        Transform pt = panel.transform;
        var mgr = Scene.Sheets;
        if (mgr != null) mgr.CompleteTransformMotion(pt);
        Vector3 prePos = pt.position;
        Quaternion preRot = pt.rotation;

        switch (args.action.Trim().ToLowerInvariant()) {
            case "move": Move(panel, args, result); break;
            case "rotate": Rotate(panel, args, result); break;
            case "face": Face(panel, result); break;
            default: result["error"] = $"Unknown action '{args.action}'."; return;
        }

        if (mgr != null) mgr.GlideTransformFrom(pt, prePos, preRot);
    }

    private static void Move(PanelUI panel, Args args, Dictionary<string, object> result) {
        string direction = args.direction?.Trim().ToLowerInvariant();
        if (!TryWorldDirection(direction, out Vector3 worldDir)) {
            result["error"] = $"Unknown direction '{args.direction}'. Use left, right, forward, back, up, or down.";
            return;
        }

        float distance = Mathf.Clamp(args.distance ?? DefaultDistance, MinDistance, MaxDistance);

        Transform t = panel.transform;
        Vector3 before = t.position;
        Vector3 target = PanelPlacement.Reachable(before + worldDir * distance, out bool limited);
        t.position = target;

        float actual = (t.position - before).magnitude;
        result["moved"] = direction;
        result["requestedMeters"] = Math.Round(distance, 3);
        result["actualMeters"] = Math.Round(actual, 3);
        if (limited)
            result["note"] = "The panel was kept within the user's reach and moved less than asked.";

        StateChannel.Record("Panel", $"moved the tool panel {actual:0.00}m {direction}");
    }

    private static void Rotate(PanelUI panel, Args args, Dictionary<string, object> result) {
        string direction = args.direction?.Trim().ToLowerInvariant();
        float sign;
        if (direction == "right") sign = 1f;
        else if (direction == "left") sign = -1f;
        else { result["error"] = $"Unknown direction '{args.direction}'. Use left or right."; return; }

        float degrees = Mathf.Clamp(args.degrees ?? DefaultDegrees, MinDegrees, MaxDegrees);

        Transform t = panel.transform;
        t.rotation = Quaternion.AngleAxis(sign * degrees, Vector3.up) * t.rotation;

        result["rotated"] = direction;
        result["degrees"] = Math.Round(degrees, 1);
        StateChannel.Record("Panel", $"turned the tool panel {degrees:0}° {direction}");
    }

    private static void Face(PanelUI panel, Dictionary<string, object> result) {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) { result["error"] = "The user's viewpoint could not be found."; return; }

        Transform t = panel.transform;
        Vector3 face = t.position - cam.position;
        face.y = 0f;
        if (face.sqrMagnitude < 1e-6f) {
            result["error"] = "The panel is too close to the user to square it up; move it first.";
            return;
        }

        t.rotation = Quaternion.LookRotation(face.normalized);
        result["faced"] = true;
        StateChannel.Record("Panel", "turned the tool panel to face the user");
    }

}
