public static class StalePositions
{
    private static bool _rowsDirty;
    private static bool _colsDirty;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => ClearAll();

    // Only the user's own changes leave the assistant's positions out of date.
    public static void MarkDirty(bool columns)
    {
        if (StateChannel.InAgentCall) return;
        if (columns) _colsDirty = true;
        else _rowsDirty = true;
    }

    public static bool IsDirty(bool columns) => columns ? _colsDirty : _rowsDirty;

    public static void Clear(bool columns)
    {
        if (columns) _colsDirty = false;
        else _rowsDirty = false;
    }

    public static void ClearAll()
    {
        _rowsDirty = false;
        _colsDirty = false;
    }
}
