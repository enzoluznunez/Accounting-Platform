using System;
using System.Collections.Generic;

public abstract class ToolOptions : Tool
{
    private int _selected = -1;
    private ButtonList _group;

    protected int Selected => _selected;
    protected bool HasOption => _selected >= 0;

    public abstract IReadOnlyList<string> Options { get; }
    public abstract string OptionNoun { get; }

    protected abstract ButtonList BuildOptions();
    protected virtual void OnOptionChanged() { }

    public string CurrentOptionName =>
        _selected >= 0 && _selected < Options.Count ? Options[_selected] : "none";

    protected override void BuildPanelContent()
    {
        _group = BuildOptions();
        ApplyVisual();
    }

    protected override void ClearToolState() => ClearOption(false);

    public bool SetOption(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        string target = name.Trim();
        if (string.Equals(target, "none", StringComparison.OrdinalIgnoreCase)) { ClearOption(); return true; }

        IReadOnlyList<string> options = Options;
        for (int i = 0; i < options.Count; i++)
        {
            if (!string.Equals(options[i], target, StringComparison.OrdinalIgnoreCase)) continue;
            Select(i);
            return true;
        }
        return false;
    }

    protected void Select(int index)
    {
        if (index < 0 || index >= Options.Count || _selected == index) return;
        _selected = index;
        ApplyVisual();
        OnOptionChanged();
        StateChannel.RecordState("option", $"the {OptionNoun} is {CurrentOptionName}");
    }

    protected void ClearOption(bool announce = true)
    {
        if (_selected < 0) return;
        _selected = -1;
        ApplyVisual();
        OnOptionChanged();
        string what = $"no {OptionNoun} is chosen";
        if (announce) StateChannel.RecordState("option", what);
        else StateChannel.SetState("option", what);
    }

    private void ApplyVisual() => _group?.SetSelected(_selected);
}
