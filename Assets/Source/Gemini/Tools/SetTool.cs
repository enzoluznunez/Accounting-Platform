using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetTool : AgenticTool<SetTool.Args> {

    public class Args {
        [Doc("The tool to arm, or 'none' to clear the selection.")]
        [Values("filter", "slice", "color", "move", "rotate", "scale", "sort", "profile", "none")]
        public string tool;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetTool",
        Description = "Set which tool is armed in the tool panel, or clear the selection with 'none'. The Call tools " +
                      "arm themselves, so prefer calling the action you want directly over selecting a tool first; use " +
                      "this only when the user asked for the tool itself.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!TryParseTool(args.tool, out var tool)) {
            result["error"] = $"Unknown tool '{args.tool}'.";
            return;
        }

        var controller = Scene.Tools;
        if (controller == null) { result["error"] = "Tool controller not found in scene."; return; }

        if (controller.SelectedTool != tool && AgentTurn.UserTookOver) {
            Refuse(result, "tool changed by the user",
                $"The user changed the tool while you were working, so it is not yours to change again. " +
                "Tell them what you had done and ask before carrying on.");
            result["selected"] = controller.SelectedTool.ToString();
            return;
        }

        if (tool == ToolType.None) controller.DeselectTool();
        else {
            if (!EnsureToolPanelOpen(result)) return;
            if (controller.SelectedTool != tool) controller.SelectTool(tool);
        }

        result["selected"] = controller.SelectedTool.ToString();
    }
}
