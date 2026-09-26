using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetPanel : AgenticTool<SetPanel.Args> {

    public class Args {
        [Doc("What to do with it. Prefer 'open' or 'close' over 'toggle' when you know the state you want.")]
        [Values("open", "close", "toggle")]
        public string state;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetPanel",
        Description = "Open, close, or toggle the tool panel, the panel holding the tool buttons and your own " +
                      "speed setting.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        string state = args.state.Trim().ToLowerInvariant();

        var tool = Scene.ToolPanel;
        if (tool == null) { result["error"] = "Tool panel not found in scene."; return; }

        Apply(state, tool.IsVisible, tool.ShowPanel, tool.HidePanel, tool.TogglePanel);
        result["visible"] = tool.IsVisible;
        result["did"] = tool.IsVisible ? "the tool panel is open" : "the tool panel is closed";
    }

    private static void Apply(string state, bool visible, System.Action show, System.Action hide, System.Action toggle) {
        switch (state) {
            case "open": if (!visible) show(); break;
            case "close": if (visible) hide(); break;
            default: toggle(); break;
        }
    }
}
