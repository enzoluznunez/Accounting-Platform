using UnityEngine;

public static class SheetRaycast
{
    public struct Hit
    {
        public bool Valid;
        public SheetCube Cube;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
    }

    private const int MaxBuffer = 4096;

    private static Collider[] _overlaps = new Collider[64];

    public static bool NearestCube(Vector3 point, float radius, out Hit hit)
    {
        hit = default;
        if (radius <= 0f) return false;

        int count = Overlap(point, radius);
        float best = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider candidate = _overlaps[i];
            if (candidate == null || !candidate.TryGetComponent(out SheetCube cube)) continue;

            Vector3 closest = candidate.ClosestPoint(point);
            float distance = Vector3.Distance(closest, point);
            if (distance >= best) continue;

            best = distance;
            hit = new Hit
            {
                Valid = true,
                Cube = cube,
                Point = closest,
                Normal = distance > 1e-5f ? (point - closest).normalized : Vector3.up,
                Distance = distance
            };
        }

        return hit.Valid;
    }

    public static bool Contains(SheetCube cube, Vector3 point)
    {
        Collider box = cube != null ? cube.Collider : null;
        if (box == null || !box.enabled) return false;
        return (box.ClosestPoint(point) - point).sqrMagnitude <= 1e-8f;
    }

    private static int Overlap(Vector3 point, float radius)
    {
        while (true)
        {
            int count = Physics.OverlapSphereNonAlloc(point, radius, _overlaps,
                ~0, QueryTriggerInteraction.Collide);

            if (count < _overlaps.Length || _overlaps.Length >= MaxBuffer) return count;
            _overlaps = new Collider[Mathf.Min(_overlaps.Length * 2, MaxBuffer)];
        }
    }
}
