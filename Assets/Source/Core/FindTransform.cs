using System.Collections.Generic;
using UnityEngine;

public static class FindTransform
{
    public static List<Transform> FindAllDeep(Transform parent, string name, List<Transform> results = null)
    {
        results = results ?? new List<Transform>();
        if (parent == null) return results;

        if (parent.name == name) results.Add(parent);
        for (int i = 0; i < parent.childCount; i++)
            FindAllDeep(parent.GetChild(i), name, results);

        return results;
    }

    public static Transform FindDeep(Transform parent, string name)
    {
        if (parent == null) return null;
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform result = FindDeep(parent.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }
}
