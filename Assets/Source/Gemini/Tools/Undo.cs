using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class Undo : AgenticTool<Undo.Args> {

    public class Args {
        [Doc("How many edits to undo, one at a time. Defaults to 1. Ignored when 'all' is true.")]
        [Limits(1, 1000), DefaultsTo(1), Optional]
        public int? count;
        [Doc("Undo every edit. Confirm with the user before setting this."), Optional]
        public bool? all;
        [Doc("The tool whose edit you expect to be newest. Only checked for a single undo.")]
        [Optional]
        public EditKind? tool;
    }

    public override FunctionDeclaration Declaration {
        get {
            var response = new Schema {
                Type = Type.Object,
                Properties = new Dictionary<string, Schema>()
            };
            response.Properties["undone"] = new Schema { Type = Type.Integer,
                Description = "How many edits were reverted." };
            response.Properties["remaining"] = new Schema { Type = Type.Integer,
                Description = "How many edits are left on the timeline." };
            response.Properties["failed"] = new Schema { Type = Type.Integer,
                Description = "Records that no longer matched the scene and were dropped without reverting anything. Never report these as undone." };
            response.Properties["blocked"] = new Schema { Type = Type.Boolean,
                Description = "True when undo stopped at an edit it could not revert right now; that edit is still on the timeline." };

            return new FunctionDeclaration {
                Name = "Undo",
                Description = "Undo recent edits, newest first (the tool panel's Undo / Undo All buttons). " +
                              "All edits share one timeline: moved/rotated/scaled pieces, Sort reorders, " +
                              "the projections raised by the Profile tool, and what the Filter tool took off the sheet. " +
                              "Set 'all' to undo everything and clear the tool selection; 'count' never turns into Undo All, " +
                              "it stops when the timeline runs out. " +
                              "Optionally pass 'tool' to assert what the newest edit is; if it does not match, this refuses and names " +
                              "what is actually next, so you can ask the user which they meant.",
                Parameters = ParametersFor(typeof(Args)),
                Response = response
            };
        }
    }

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolPanelOpen(result)) return;

        var controller = Scene.Tools;
        if (controller == null) { result["error"] = "Tool controller not found in scene."; return; }

        var top = ManageDatasets.ActiveEdits.Peek();
        if (top == null) {
            result["undone"] = 0;
            result["remaining"] = 0;
            result["note"] = "There are no edits to undo.";
            return;
        }

        bool undoAll = args.all == true;

        int count = 1;
        if (!undoAll && args.count.HasValue) count = args.count.Value < 1 ? 1 : args.count.Value;

        if (args.tool.HasValue && top.kind != args.tool.Value) {
            Refuse(result, "matching newest edit",
                $"The newest edit is from the {Edit.KindName(top.kind)} tool, not the {Edit.KindName(args.tool.Value)} tool. Undo always " +
                "removes the newest edit first. Ask the user which they meant, then call again without 'tool'.");
            result["nextUndo"] = Edit.KindName(top.kind);
            result["remaining"] = ManageDatasets.ActiveEdits.Count;
            return;
        }

        if (undoAll) {
            int had = ManageDatasets.ActiveEdits.Count;
            int steps = ManageDatasets.ActiveEdits.UndoStepCount();
            controller.UndoAll();
            controller.DeselectTool();
            result["undone"] = steps;
            result["records"] = had;
            result["undoneAll"] = true;
            result["remaining"] = 0;
            result["note"] = "Undo All reverts what it can and then clears the timeline; any records that no longer " +
                             "matched the scene were dropped rather than reverted.";
            return;
        }

        int did = 0, failed = 0;
        bool blocked = false;
        for (int i = 0; i < count && !blocked && ManageDatasets.ActiveEdits.Peek() != null; i++) {
            switch (controller.Undo()) {
                case ManageSheets.UndoResult.Applied: did++; break;
                case ManageSheets.UndoResult.Stale: failed++; break;
                // The record was kept, so trying again would only hit it again.
                default: blocked = true; break;
            }
        }

        result["undone"] = did;
        if (did + failed < count && !blocked)
            result["note"] = $"Only {did + failed} of the {count} you asked for were on the timeline; the rest never existed.";
        if (failed > 0) {
            result["failed"] = failed;
            result["note"] = failed + " edit(s) could not be reverted because they no longer matched the scene; " +
                             "they were dropped from the history. Tell the user rather than reporting success.";
        }
        if (blocked) {
            result["blocked"] = true;
            string kept = "The next edit could not be reverted right now because the sheet is not available; it was " +
                          "kept on the timeline. Tell the user rather than reporting success.";
            result["note"] = failed > 0 ? result["note"] + " " + kept : kept;
        }
        result["remaining"] = ManageDatasets.ActiveEdits.Count;
        var next = ManageDatasets.ActiveEdits.Peek();
        if (next != null) result["nextUndo"] = Edit.KindName(next.kind);
    }
}
