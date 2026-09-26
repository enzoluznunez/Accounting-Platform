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
        if (sheetManager == null) sheetManager = FindAnyObjectByType<ManageSheets>();
    }

    private static bool UserDriven => StateChannel.UserDriven;

    public void SelectTool(ToolType tool)
    {
        if (UserDriven) AgentTurn.UserTookControl("tool changed");
        ToolType next = SelectedTool == tool ? ToolType.None : tool;
        if (next == SelectedTool) return;

        SelectedTool = next;
        OnToolChanged?.Invoke(SelectedTool);
        ReportSelection();
    }

    public void DeselectTool()
    {
        if (UserDriven) AgentTurn.UserTookControl("tool deselected");
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

    private static bool TryEditKindOf(ToolType tool, out EditKind kind) =>
        Enum.TryParse(tool.ToString(), out kind);

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

    public bool Undo()
    {
        Edit top = ManageDatasets.ActiveEdits.Peek();
        if (top == null) return false;

        if (top.kind == EditKind.Sort)
        {
            SortTool sort = Scene.Sort;
            if (sort != null) sort.HaltOrderSequence();
        }

        int inGroup = ManageDatasets.ActiveEdits.TopGroupSize();

        bool sortRows = false, sortColumns = false;
        for (int i = ManageDatasets.ActiveEdits.Count - inGroup; i < ManageDatasets.ActiveEdits.Count; i++)
        {
            if (i < 0) continue;
            Edit rec = ManageDatasets.ActiveEdits[i];
            if (rec.kind != EditKind.Sort) continue;
            if (rec.reorderIsColumn) sortColumns = true;
            else sortRows = true;
        }

        switch (UndoTop(out string kindName))
        {
            case ManageSheets.UndoResult.Applied:
                for (int i = 1; i < inGroup; i++)
                    if (UndoTop(out _) == ManageSheets.UndoResult.Unreachable) break;
                if (UserDriven)
                {
                    if (sortRows) StalePositions.MarkDirty(false);
                    if (sortColumns) StalePositions.MarkDirty(true);
                }
                StateChannel.Record("Undo", inGroup > 1
                    ? $"undid the {kindName} edit ({inGroup} steps, one action)"
                    : $"undid the {kindName} edit");
                return true;

            case ManageSheets.UndoResult.Unreachable:
                Debug.LogWarning($"[ManageTools] {kindName} undo could not run; the record was kept.");
                return false;

            default:
                Debug.LogWarning($"[ManageTools] {kindName} undo no longer matches the scene; the record was dropped.");
                return false;
        }
    }

    public void UndoAll()
    {
        int had = ManageDatasets.ActiveEdits.Count;

        bool hadSort = false;
        for (int i = 0; i < had; i++)
            if (ManageDatasets.ActiveEdits[i].kind == EditKind.Sort) { hadSort = true; break; }

        for (int i = 0; i < had && ManageDatasets.ActiveEdits.Peek() != null; i++)
            if (UndoTop(out _) == ManageSheets.UndoResult.Unreachable) break;

        foreach (ToolType tool in System.Enum.GetValues(typeof(ToolType)))
            if (tool != ToolType.None) ResetTool(tool);

        ManageDatasets.ActiveEdits.Clear();
        if (sheetManager != null) sheetManager.SyncProjectionToStack();
        if (hadSort && UserDriven)
        {
            StalePositions.MarkDirty(false);
            StalePositions.MarkDirty(true);
        }
        if (had > 0) StateChannel.Record("Undo", $"undid all {had} edits");
    }
}
