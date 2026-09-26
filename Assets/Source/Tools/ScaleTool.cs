using UnityEngine;
using Oculus.Interaction;

public class ScaleTool : Tool
{
    private const float MinScale = 0.01f;
    private const float MaxScale = 2f;

    private static ITransformer ScaleTransformerFor(CreateSheet sheet)
    {
        GrabFreeTransformer scale = sheet.GetComponent<GrabFreeTransformer>();
        if (scale == null)
        {
            scale = sheet.gameObject.AddComponent<GrabFreeTransformer>();
            scale.InjectOptionalPositionConstraints(PinnedPosition());
            scale.InjectOptionalScaleConstraints(ScaleLimits());
        }

        scale.InjectOptionalRotationConstraints(PinnedRotation(sheet.transform.localEulerAngles));
        return scale;
    }

    private static TransformerUtils.ConstrainedAxis Pin(float value) =>
        new TransformerUtils.ConstrainedAxis
        {
            ConstrainAxis = true,
            AxisRange = new TransformerUtils.FloatRange { Min = value, Max = value }
        };

    private static TransformerUtils.ConstrainedAxis Range(float min, float max) =>
        new TransformerUtils.ConstrainedAxis
        {
            ConstrainAxis = true,
            AxisRange = new TransformerUtils.FloatRange { Min = min, Max = max }
        };

    private static TransformerUtils.PositionConstraints PinnedPosition() =>
        new TransformerUtils.PositionConstraints
        {
            ConstraintsAreRelative = true,
            XAxis = Pin(0f),
            YAxis = Pin(0f),
            ZAxis = Pin(0f)
        };

    private static TransformerUtils.RotationConstraints PinnedRotation(Vector3 euler) =>
        new TransformerUtils.RotationConstraints
        {
            XAxis = Pin(euler.x),
            YAxis = Pin(euler.y),
            ZAxis = Pin(euler.z)
        };

    private static TransformerUtils.ScaleConstraints ScaleLimits() =>
        new TransformerUtils.ScaleConstraints
        {
            ConstraintsAreRelative = false,
            XAxis = Range(MinScale, MaxScale),
            YAxis = Range(MinScale, MaxScale),
            ZAxis = Range(MinScale, MaxScale)
        };

    private bool _armPending;

    protected override ToolType Kind => ToolType.Scale;

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

        Report("resized the sheet");

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
        }, EditKind.Scale);
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
        sheetManager.SetTwoGrab(ScaleTransformerFor);
    }
}
