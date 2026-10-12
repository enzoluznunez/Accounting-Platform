using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public static class UILayout
{
    public const int SettlePasses = 4;

    public static void Clear(Transform root)
    {
        if (root == null) return;

        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
            child.SetParent(null, false);
            UnityEngine.Object.Destroy(child.gameObject);
        }
    }

    public static T Ensure<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    public static RectTransform EnsureChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing as RectTransform ?? existing.gameObject.AddComponent<RectTransform>();
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child.AddComponent<RectTransform>();
    }

    public static LayoutElement FixedHeight(GameObject go, float height)
    {
        LayoutElement le = Ensure<LayoutElement>(go);
        le.minHeight = height;
        le.preferredHeight = height;
        le.flexibleHeight = 0f;
        return le;
    }

    public static LayoutElement FixedSize(GameObject go, Vector2 size)
    {
        LayoutElement le = FixedHeight(go, size.y);
        le.minWidth = size.x;
        le.preferredWidth = size.x;
        le.flexibleWidth = 0f;
        return le;
    }

    public static IEnumerator Converge(Func<bool> alive, Action apply, Func<float> measure)
    {
        float previous = measure();

        for (int pass = 0; pass < SettlePasses; pass++)
        {
            yield return null;
            if (!alive()) break;

            apply();

            float current = measure();
            if (Mathf.Approximately(current, previous)) break;
            previous = current;
        }
    }
}
