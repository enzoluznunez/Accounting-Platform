using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public static class UIMeasure
{
    private static readonly Vector3[] Corners = new Vector3[4];
    private static readonly List<TMP_Text> Texts = new List<TMP_Text>();

    private static bool TryRebuild(RectTransform layoutRoot, RectTransform target)
    {
        if (layoutRoot == null || target == null) return false;
        if (!target.gameObject.activeInHierarchy) return false;

        LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRoot);
        SettleText(target);
        LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRoot);
        return true;
    }

    private static void SettleText(RectTransform target)
    {
        target.GetComponentsInChildren(false, Texts);
        for (int i = 0; i < Texts.Count; i++)
            if (Texts[i] != null) Texts[i].ForceMeshUpdate();
        Texts.Clear();
    }

    public static bool TryTextWidth(TMP_Text label, out float width)
    {
        width = 0f;
        if (label == null || !label.gameObject.activeInHierarchy) return false;

        string text = label.text;
        if (string.IsNullOrEmpty(text)) return true;

        label.ForceMeshUpdate();
        width = label.GetPreferredValues(text, 0f, 0f).x;
        return true;
    }

    public static bool TryPreferredHeight(RectTransform layoutRoot, RectTransform target, out float height)
    {
        height = 0f;
        if (!TryRebuild(layoutRoot, target)) return false;

        height = LayoutUtility.GetPreferredHeight(target);
        return true;
    }

    public static bool TryLocalBounds(RectTransform layoutRoot, RectTransform[] targets, Transform space,
        out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;
        if (layoutRoot == null || targets == null || space == null) return false;

        LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRoot);

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;

        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i] == null || !targets[i].gameObject.activeInHierarchy) continue;

            targets[i].GetWorldCorners(Corners);
            for (int c = 0; c < 4; c++)
            {
                Vector3 local = space.InverseTransformPoint(Corners[c]);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            any = true;
        }
        if (!any) return false;

        center = (min + max) * 0.5f;
        size = max - min;
        return true;
    }
}
