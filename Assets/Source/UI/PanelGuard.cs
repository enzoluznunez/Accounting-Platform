public static class PanelGuard
{
    private static bool _toolClosedByUser;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _toolClosedByUser = false;

    public static bool ToolPanelClosedByUser => _toolClosedByUser;

    public static void MarkToolPanelClosedByUser() => _toolClosedByUser = true;

    public static void ClearToolPanel() => _toolClosedByUser = false;
}
