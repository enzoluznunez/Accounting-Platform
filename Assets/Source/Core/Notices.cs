using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class Notices
{
    private const float NoticeSeconds = 3f;

    private static int _lastToastFrame = -1;
    private static int _generation;
    private static Tooltip _tooltip;

    public static void EditsDropped(MonoBehaviour host, string message) =>
        Show(host, "Edits Cleared", message);

    public static void Show(MonoBehaviour host, string title, string message)
    {
        StateChannel.Record(message);

        if (Time.frameCount == _lastToastFrame) return;
        _lastToastFrame = Time.frameCount;

        if (_tooltip == null) _tooltip = Scene.Tooltip;
        if (_tooltip == null || host == null || !host.isActiveAndEnabled) return;

        _generation++;
        _tooltip.ShowNotice(title, message);
        host.StartCoroutine(HideAfterDelay(_generation));
    }

    public static void HideNotice()
    {
        _generation++;
        if (_tooltip != null) _tooltip.DismissNotice();
    }

    private static IEnumerator HideAfterDelay(int generation)
    {
        yield return new WaitForSecondsRealtime(NoticeSeconds);
        if (generation != _generation || _tooltip == null) yield break;

        _generation++;
        _tooltip.DismissNotice();
    }
}
