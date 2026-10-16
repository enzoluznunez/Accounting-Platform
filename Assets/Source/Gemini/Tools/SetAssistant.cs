using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetAssistant : AgenticTool<SetAssistant.Args> {

    public class Args {
        [Doc("Whether to turn the assistant on, off, or to the opposite of what it is now.")]
        [Values("on", "off", "toggle")]
        public string state;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetAssistant",
        Description = "Turn the Gemini voice assistant on or off, or toggle it (the Assistant button in the tool panel). " +
                      "Turning it off ends voice control until the user re-enables it by hand, so only turn it off when they have asked; " +
                      "the switch-off happens when the current reply finishes, so you can still say goodbye.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var watch = Scene.Assistant;
        if (watch == null) { result["error"] = "Watch not found in scene."; return; }

        bool active;
        switch (args.state) {
            case "on": active = true; break;
            case "off": active = false; break;
            default: active = !watch.IsGeminiActive; break;
        }

        if (active) {
            Gemini.CancelShutdownRequest();
            watch.SetGeminiActive(true, AssistantCause.Agent);
        }
        else {
            if (AgentTurn.UserTookOver) {
                Refuse(result, "user took control",
                    "The user took over while you were working, so do not turn yourself off on their behalf. " +
                    "Tell them where things stand and ask before carrying on.");
                return;
            }
            Gemini.RequestShutdownAfterTurn();
            result["note"] = "The assistant stays on until this reply finishes, then turns off.";
        }
        result["active"] = active;
    }
}
