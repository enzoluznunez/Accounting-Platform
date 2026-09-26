using System.Threading;
using UnityEngine;

public static partial class Gemini {

    public const long ContextWindowTokens = 131072;

    public const long SafetyNetTrigger = 48000;
    public const long SafetyNetTarget = 24000;
    public const long ExhaustTokens = ContextWindowTokens / 2;
    public const long ExhaustWarnTokens = ExhaustTokens - 6000;

    private static int usageConnection;
    private static long contextTokens;
    private static long sessionPromptTokens;
    private static long sessionUncachedTokens;
    private static long lastExactContext;
    private static long lastPromptTokens = -1;

    private static volatile bool exhausted;
    private static volatile bool exhaustWarned;

    public static bool Exhausted => exhausted;

    public static void ResetWindow() {
        exhausted = false;
        exhaustWarned = false;
        ResetContextEstimate();
        StateChannel.ClearPending();
    }

    public static void ResetContextEstimate() {
        usageConnection = 0;
        contextTokens = 0;
        sessionPromptTokens = 0;
        sessionUncachedTokens = 0;
        lastExactContext = 0;
        lastPromptTokens = -1;
        Interlocked.Exchange(ref toolRoundsThisTurn, 0);
    }

    private static bool TrackContext(int conn, long promptTokens, long uncachedTokens, int rounds, out long context, out bool exact) {
        if (conn != usageConnection) {
            usageConnection = conn;
            if (!resumedConnection) contextTokens = 0;
        }

        if (promptTokens > 0) sessionPromptTokens += promptTokens;
        if (uncachedTokens > 0) sessionUncachedTokens += uncachedTokens;

        exact = rounds <= 0;
        context = contextTokens;
        if (promptTokens <= 0) return false;

        long reading = promptTokens / (rounds + 1);
        if (exact || reading > contextTokens) contextTokens = reading;

        context = contextTokens;
        return true;
    }

    public static void ObserveTokens(long context) => ObserveCeiling(context);

    private static void ObserveCeiling(long context) {
        if (context >= ExhaustTokens) { MarkExhausted(context); return; }
        if (context < ExhaustWarnTokens || exhaustWarned) return;

        exhaustWarned = true;
        Debug.LogWarning($"[Gemini][window] approaching context ceiling: {context} of {ExhaustTokens}");
        Interlocked.Exchange(ref pendingClosingNotice, 1);
    }

    public static void ClearExhaustion() {
        exhausted = false;
        exhaustWarned = false;
    }

    private static void MarkExhausted(long context) {
        if (exhausted) return;
        exhausted = true;
        Debug.LogWarning($"[Gemini][window] context ceiling reached: {context} >= {ExhaustTokens}");
        resumeHandle = null;
        RetireConnection();
        var s = liveSession;
        if (s != null) { try { _ = s.CloseAsync(); } catch { } }
    }
}
