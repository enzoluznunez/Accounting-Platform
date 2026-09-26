using UnityEngine;
using Oculus.Interaction;

public class RotateTool : Tool
{
    private static ITransformer YawTransformerFor(CreateSheet sheet)
    {
        TwoGrabRotateTransformer yaw = sheet.GetComponent<TwoGrabRotateTransformer>();
        if (yaw != null) return yaw;

        yaw = sheet.gameObject.AddComponent<TwoGrabRotateTransformer>();
        yaw.InjectOptionalConstraints(new TwoGrabRotateTransformer.TwoGrabRotateConstraints
        {
            MinAngle = new FloatConstraint(),
            MaxAngle = new FloatConstraint()
        });
        return yaw;
    }

    private bool _armPending;

    protected override ToolType Kind => ToolType.Rotate;

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

        Report("rotated the sheet");

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
        }, EditKind.Rotate);
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

        sheetManager.SetGrabbable(true);
        sheetManager.SetTwoGrab(YawTransformerFor);
    }
}
