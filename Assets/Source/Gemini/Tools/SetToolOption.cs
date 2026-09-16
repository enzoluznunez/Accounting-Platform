using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetToolOption : AgenticTool {

    public override FunctionDeclaration Declaration {
        get {
            var color = Scene.Color;
            var colorNames = color != null && color.Options != null
                ? new List<string>(color.Options)
                : new List<string>(ColorTool.DefaultPaletteNames);

            var options = new List<string>(colorNames) { "none" };
            options.AddRange(ToolPanelUI.AssistantSpeedLabels);

            return new FunctionDeclaration {
                Name = "SetToolOption",
                Description = "Arm a tool's option before using it. For the Color tool the option is a color: " +
                              string.Join(", ", colorNames) + ". " +
                              "The Slice, Sort, Profile, and Filter tools have no option; selecting them is enough. " +
                              "'none' clears the color and leaves the Color tool armed with nothing chosen. " +
                              "'assistant' sets how fast your own actions play out on screen: '" +
                              string.Join("', '", ToolPanelUI.AssistantSpeedLabels) +
                              "'. Its buttons are on screen only while the tool panel is open and no " +
                              "tool is selected.",
                Parameters = new Schema {
                    Type = Type.Object,
                    Properties = new Dictionary<string, Schema> {
                        { "tool", new Schema { Type = Type.String,
                            Enum = new List<string> { "color", "assistant" },
                            Description = "Which tool to arm an option on." } },
                        { "option", new Schema { Type = Type.String,
                            Enum = options,
                            Description = "The option to arm: a color for the Color tool, or a speed for the assistant." } }
                    },
                    Required = new List<string> { "tool", "option" }
                }
            };
        }
    }

    protected override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        TryGet(args, "tool", out var toolArg);
        string tool = AsString(toolArg)?.Trim().ToLowerInvariant();

        string option = TryGet(args, "option", out var optArg) ? AsString(optArg) : null;
        if (tool == "assistant") {
            ArmAssistantSpeed(option, result);
            return;
        }

        if (!TryParseTool(tool, out ToolType type)) {
            result["error"] = $"Unknown tool '{tool}'. Use color or assistant.";
            return;
        }

        ToolOptions target = ToolOptionsFor(type);
        if (target == null) {
            result["error"] = $"The {type} tool has no option to arm; selecting it is enough.";
            return;
        }

        if (string.IsNullOrWhiteSpace(option)) {
            result["error"] = $"Provide 'option' for the {type} tool. Available: {string.Join(", ", target.Options)}.";
            return;
        }

        if (!EnsureToolSelected(type, result)) return;

        if (!target.SetOption(option)) {
            result["error"] = $"Unknown {target.OptionNoun} '{option}'. Available: {string.Join(", ", target.Options)}.";
            return;
        }

        result[target.OptionNoun] = target.CurrentOptionName;
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

    private static ToolOptions ToolOptionsFor(ToolType type) {
        switch (type) {
            case ToolType.Color: return Scene.Color;
            default: return null;
        }
    }
}
