using Oculus.Interaction;

public class RotateTool : GrabTool
{
    private static ITransformer YawTransformerFor(CreateSheet sheet) => GrabTransformers.EnsureYaw(sheet.gameObject);

    protected override ToolType Kind => ToolType.Rotate;

    protected override EditKind EditKind => EditKind.Rotate;

    protected override string Describe(float distance) => "rotated the sheet";

    protected override void ArmGrab()
    {
        sheetManager.SetGrabbable(true);
        sheetManager.SetTwoGrab(YawTransformerFor);
    }
}
