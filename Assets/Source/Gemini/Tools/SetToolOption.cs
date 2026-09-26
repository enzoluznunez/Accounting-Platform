using System.Collections.Generic;
using Google.GenAI.Types;

// How fast the assistant's own actions play out on screen. This was once the way
// to arm any tool's option, but the only tool that had one was Color, and colour
// is now a property of the data rather than something to choose.
public sealed class SetToolOption : AgenticTool {

    public override FunctionDeclaration Declaration {
        get {
            var options = new List<string>(ToolPanelUI.AssistantSpeedLabels);

            return new FunctionDeclaration {
                Name = "SetToolOption",
                Description = "Set how fast your own actions play out on screen: '" +
                              string.Join("', '", ToolPanelUI.AssistantSpeedLabels) +
                              "'. No tool takes an option of its own; selecting a tool is enough. " +
                              "The speed buttons are on screen only while the tool panel is open and no " +
                              "tool is selected.",
                Parameters = new Schema {
                    Type = Type.Object,
                    Properties = new Dictionary<string, Schema> {
                        { "option", new Schema { Type = Type.String,
                            Enum = options,
                            Description = "How fast your actions play out." } }
                    },
                    Required = new List<string> { "option" }
                }
            };
        }
    }

    protected override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        string option = TryGet(args, "option", out var optArg) ? AsString(optArg) : null;
        ArmAssistantSpeed(option, result);
    }

    private static void ArmAssistantSpeed(string option, Dictionary<string, object> result) {
        var panel = Scene.ToolPanel;
        if (panel == null) { result["error"] = "Tool panel not found in scene."; return; }

        if (string.IsNullOrWhiteSpace(option)) {
            result["error"] = "Provide 'option' for the assistant: " +
                              string.Join(", ", ToolPanelUI.AssistantSpeedLabels) + ".";
            return;
        }

        if (!EnsureToolPanelOpen(result)) return;

        var tools = Scene.Tools;
        if (tools != null && tools.SelectedTool != ToolType.None) {
            Refuse(result, "no tool selected",
                "The assistant's speed buttons only show while no tool is selected. " +
                "Clear the selection with SetTool(tool: 'none'), then call this again.");
            return;
        }

        if (!panel.SetAssistantSpeed(option)) {
            result["error"] = $"Unknown speed '{option}'. Available: " +
                              string.Join(", ", ToolPanelUI.AssistantSpeedLabels) + ".";
            return;
        }
        result["speed"] = panel.AssistantSpeedName;
    }
}
