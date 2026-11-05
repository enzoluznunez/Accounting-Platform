using System;
using UnityEngine;

public enum ToolType { None, Filter, Move, Sort, Rotate, Scale, Profile }

public class ManageTools : MonoBehaviour
{
    public event Action<ToolType> OnToolChanged;

    public event Action<ToolType> OnToolReset;

    public ManageSheets sheetManager;


    public ToolType SelectedTool { get; private set; } = ToolType.None;

    private void Start()
    {
        if (sheetManager == null) sheetManager = Scene.Sheets;
    }

    private static bool UserDriven => StateChannel.UserDriven;

    public void SelectTool(ToolType tool)
    {
        if (UserDriven) AgentTurn.UserTookControl();
        ToolType next = SelectedTool == tool ? ToolType.None : tool;
        if (next == SelectedTool) return;

        SelectedTool = next;
        OnToolChanged?.Invoke(SelectedTool);
        ReportSelection();
    }

    public void DeselectTool()
    {
        if (UserDriven) AgentTurn.UserTookControl();
        if (SelectedTool == ToolType.None) return;

        SelectedTool = ToolType.None;
        OnToolChanged?.Invoke(SelectedTool);
        ReportSelection();
    }

    private void ReportSelection() => StateChannel.RecordState("tool",
        SelectedTool == ToolType.None ? "no tool is selected" : $"the {SelectedTool} tool is selected");

    private ToolType _suspended = ToolType.None;

    public void SuspendTool()
    {
        _suspended = SelectedTool;
        DeselectTool();
    }

    public void ResumeTool()
    {
        if (_suspended == ToolType.None) return;
        ToolType resume = _suspended;
        _suspended = ToolType.None;
        SelectTool(resume);
    }

    public void ForgetSuspendedTool() => _suspended = ToolType.None;

    public void ResetTool(ToolType tool)
    {
        OnToolReset?.Invoke(tool);
        if (TryEditKindOf(tool, out EditKind kind)) ManageDatasets.ActiveEdits.DropKind(kind);
        if (sheetManager != null) sheetManager.SyncProjectionToStack();
    }

    private static bool TryEditKindOf(ToolType tool, out EditKind kind)
    {
        switch (tool)
        {
            case ToolType.Filter: kind = EditKind.Filter; return true;
            case ToolType.Move: kind = EditKind.Move; return true;
            case ToolType.Sort: kind = EditKind.Sort; return true;
            case ToolType.Rotate: kind = EditKind.Rotate; return true;
            case ToolType.Scale: kind = EditKind.Scale; return true;
            case ToolType.Profile: kind = EditKind.Profile; return true;
            default: kind = default; return false;
        }
    }

    private ManageSheets.UndoResult UndoTop(out string kindName)
    {
        Edit rec = ManageDatasets.ActiveEdits.Peek();
        kindName = rec != null ? Edit.KindName(rec.kind) : null;

        if (rec == null) return ManageSheets.UndoResult.Stale;
        if (sheetManager == null) return ManageSheets.UndoResult.Unreachable;

        ManageSheets.UndoResult outcome = sheetManager.Undo(rec);
        if (outcome != ManageSheets.UndoResult.Unreachable)
        {
            ManageDatasets.ActiveEdits.Pop();
            sheetManager.SyncProjectionToStack();
        }
        return outcome;
    }

    // Stale also covers an empty timeline: there was nothing to revert.
    public ManageSheets.UndoResult Undo()
    {
        Edit top = ManageDatasets.ActiveEdits.Peek();
        if (top == null) return ManageSheets.UndoResult.Stale;

        int inGroup = ManageDatasets.ActiveEdits.TopGroupSize();

        // A sort reorders lines and a filter takes some away or puts them back;
        // undoing either shifts the positions the assistant last read.
        bool sortRows = false, sortColumns = false, filterRows = false, filterColumns = false;
        for (int i = ManageDatasets.ActiveEdits.Count - inGroup; i < ManageDatasets.ActiveEdits.Count; i++)
        {
            if (i < 0) continue;
            Edit rec = ManageDatasets.ActiveEdits[i];
            if (rec.kind == EditKind.Sort)
            {
                if (rec.reorderIsColumn) sortColumns = true;
                else sortRows = true;
            }
            else if (rec.kind == EditKind.Filter)
            {
                if (rec.filterIsRow) filterRows = true;
                else filterColumns = true;
            }
        }

        // An animated sort anywhere in the group must stop before its record is
        // undone, or its last step would put the order straight back.
        if (sortRows || sortColumns)
        {
            SortTool sort = Scene.Sort;
            if (sort != null) sort.HaltOrderSequence();
        }

        ManageSheets.UndoResult outcome = UndoTop(out string kindName);
        switch (outcome)
        {
            case ManageSheets.UndoResult.Applied:
                for (int i = 1; i < inGroup; i++)
                    if (UndoTop(out _) == ManageSheets.UndoResult.Unreachable) break;
                if (sortRows || filterRows) StalePositions.MarkDirty(false);
                if (sortColumns || filterColumns) StalePositions.MarkDirty(true);
                StateChannel.Record(inGroup > 1
                    ? $"undid the {kindName} edit ({inGroup} steps, one action)"
                    : $"undid the {kindName} edit");
                break;

            case ManageSheets.UndoResult.Unreachable:
                Debug.LogWarning($"[ManageTools] {kindName} undo could not run; the record was kept.");
                break;

            default:
                Debug.LogWarning($"[ManageTools] {kindName} undo no longer matches the scene; the record was dropped.");
                break;
        }
        return outcome;
    }

    public void UndoAll()
    {
        int had = ManageDatasets.ActiveEdits.Count;

        bool shifted = false;
        for (int i = 0; i < had; i++)
        {
            EditKind kind = ManageDatasets.ActiveEdits[i].kind;
            if (kind == EditKind.Sort || kind == EditKind.Filter) { shifted = true; break; }
        }

        for (int i = 0; i < had && ManageDatasets.ActiveEdits.Peek() != null; i++)
            if (UndoTop(out _) == ManageSheets.UndoResult.Unreachable) break;

        foreach (ToolType tool in System.Enum.GetValues(typeof(ToolType)))
            if (tool != ToolType.None) ResetTool(tool);

        ManageDatasets.ActiveEdits.Clear();
        if (sheetManager != null) sheetManager.SyncProjectionToStack();
        if (shifted)
        {
            StalePositions.MarkDirty(false);
            StalePositions.MarkDirty(true);
        }
        if (had > 0) StateChannel.Record($"undid all {had} edits");
    }
}
