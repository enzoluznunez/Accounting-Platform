using UnityEngine;

// Shared behaviour for tools that let the user grab the whole sheet: arming the
// grab after activation and recording each committed grab as an undoable move.
public abstract class GrabTool : Tool
{
    private bool _armPending;

    protected abstract EditKind EditKind { get; }

    protected abstract string Describe(float distance);

    protected abstract void ArmGrab();

    protected override void OnToolStart()
    {
        if (sheetManager == null) return;
        sheetManager.OnSheetsChanged += ApplyGrabEnabled;
        sheetManager.OnSheetMoveCommitted += OnSheetMoveCommitted;
    }

    protected override void OnToolDestroy()
    {
        if (sheetManager == null) return;
        sheetManager.OnSheetsChanged -= ApplyGrabEnabled;
        sheetManager.OnSheetMoveCommitted -= OnSheetMoveCommitted;
    }

    protected override void OnResetTool()
    {
        if (sheetManager != null) sheetManager.ResetGrabs();
    }

    protected override void OnActiveChanged(bool active)
    {
        if (sheetManager == null) return;
        if (active) { _armPending = true; return; }
        _armPending = false;
        sheetManager.SetGrabbable(false);
    }

    private void OnSheetMoveCommitted(CreateSheet sheet, Vector3 prePos, Quaternion preRot, Vector3 preScale)
    {
        if (!Active || sheet == null || toolManager == null || sheetManager == null) return;

        float distance = sheetManager.transform
            .TransformVector(sheet.transform.localPosition - prePos).magnitude;

        Report(Describe(distance));

        ManageDatasets.ActiveEdits.PushMove(new MoveRecord
        {
            sheetId = sheet.sheetId,
            prePos = prePos,
            preRot = preRot,
            preScale = preScale,
            postPos = sheet.transform.localPosition,
            postRot = sheet.transform.localRotation,
            postScale = sheet.transform.localScale,
            distance = distance
        }, EditKind);
    }

    private void ApplyGrabEnabled()
    {
        if (Active) _armPending = true;
    }

    private void LateUpdate()
    {
        if (!_armPending) return;
        _armPending = false;
        if (!Active || sheetManager == null) return;

        ArmGrab();
    }
}
