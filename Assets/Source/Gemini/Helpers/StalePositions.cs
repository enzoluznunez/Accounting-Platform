public static class StalePositions
{
    private static bool rowsDirty;
    private static bool colsDirty;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => ClearAll();

    // Only the user's own changes leave the assistant's positions out of date.
    public static void MarkDirty(bool columns)
    {
        if (StateChannel.InAgentCall) return;
        if (columns) colsDirty = true;
        else rowsDirty = true;
    }

    public static bool IsDirty(bool columns) => columns ? colsDirty : rowsDirty;

    public static void Clear(bool columns)
    {
        if (columns) colsDirty = false;
        else rowsDirty = false;
    }

    public static void ClearAll()
    {
        rowsDirty = false;
        colsDirty = false;
    }
}
