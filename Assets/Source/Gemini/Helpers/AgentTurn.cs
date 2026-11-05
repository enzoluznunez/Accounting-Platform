using System.Collections.Generic;
using UnityEngine;

public static class AgentTurn
{
    public static bool Marked { get; private set; }
    public static bool UserTookOver { get; private set; }

    private static readonly Dictionary<EditList, int> _baselines = new Dictionary<EditList, int>();

    public static void NoteToolCall()
    {
        Marked = true;
        EditList edits = ManageDatasets.ActiveEdits;
        if (!_baselines.ContainsKey(edits)) _baselines[edits] = edits.Count;
    }

    private static int BaselineOf(EditList edits) =>
        _baselines.TryGetValue(edits, out int start) ? start : edits.Count;

    public static int AppliedSoFar()
    {
        EditList edits = ManageDatasets.ActiveEdits;
        if (!Marked) return 0;
        return Mathf.Max(0, edits.Count - BaselineOf(edits));
    }

    public static void UserTookControl()
    {
        ManageSheets sheets = Scene.Sheets;
        if (sheets != null) sheets.Interrupt();

        SortTool sort = Scene.Sort;
        if (sort != null && sort.HaltOrderSequence() && sheets != null) sheets.AmendReorderRecord();

        UserTookOver = true;

        int applied = AppliedSoFar();
        if (applied <= 0) return;

        int stamped = ManageDatasets.ActiveEdits.StampGroup(BaselineOf(ManageDatasets.ActiveEdits));
        if (stamped <= 0) return;

        StateChannel.Record($"the user stopped you after {stamped} of your changes had been applied");

        if (sheets != null)
            Notices.Show(sheets, "Assistant Stopped", stamped == 1
                ? "One change was already made. Undo takes it back."
                : $"{stamped} changes were already made. Undo takes them back in one step.");
    }

    public static void Clear()
    {
        Marked = false;
        UserTookOver = false;
        _baselines.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Clear();
}
