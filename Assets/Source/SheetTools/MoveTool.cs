public class MoveTool : GrabTool
{
    protected override ToolType Kind => ToolType.Move;

    protected override EditKind EditKind => EditKind.Move;

    protected override string Describe(float distance) => $"moved the sheet {distance:0.00}m";

    protected override void ArmGrab()
    {
        sheetManager.SetOneGrab();
        sheetManager.SetGrabbable(true);
        sheetManager.LogGrabState("MoveTool armed");
    }
}
