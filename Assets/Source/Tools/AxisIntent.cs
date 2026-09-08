using UnityEngine;

public sealed class AxisIntent
{
    public const float FaceUpCosine = 0.7f;

    public float deadband = 0.25f;

    private bool _columns = true;
    private bool _decided;
    private bool _latched;

    public bool Columns => _columns;
    public bool Decided => _decided;
    public bool Latched => _latched;

    public void Feed(float forColumns, float forRows)
    {
        if (_latched) return;

        bool lead = forColumns >= forRows;
        float top = lead ? forColumns : forRows;
        float other = lead ? forRows : forColumns;

        if (!_decided)
        {
            _columns = lead;
            _decided = true;
            return;
        }

        if (lead == _columns) return;
        if (top - other > deadband * Mathf.Abs(top)) _columns = lead;
    }

    public void Latch() => _latched = _decided;

    public void Release() => _latched = false;

    public void Reset()
    {
        _latched = false;
        _decided = false;
    }

    public static void SweepScores(Vector3 localDelta, out float forColumns, out float forRows)
    {
        forColumns = Mathf.Abs(localDelta.z);
        forRows = Mathf.Abs(localDelta.x);
    }

    public static bool SeamScores(CreateSheet sheet, Vector3 local, out float forColumns, out float forRows)
    {
        forColumns = Seam(sheet, true, local.x);
        forRows = Seam(sheet, false, local.z);
        return forColumns > 0f || forRows > 0f;
    }

    public static bool FaceScores(CreateSheet sheet, Vector3 worldNormal, out float forColumns, out float forRows)
    {
        Vector3 n = sheet.transform.InverseTransformDirection(worldNormal);
        forColumns = Mathf.Abs(n.x);
        forRows = Mathf.Abs(n.z);
        float length = n.magnitude;
        return length > 1e-6f && Mathf.Abs(n.y) < FaceUpCosine * length;
    }

    public static bool ReachScores(CreateSheet sheet, Vector3 wrist, Vector3 tip, out float forColumns, out float forRows)
    {
        Vector3 d = sheet.transform.InverseTransformDirection(tip - wrist);
        d.y = 0f;
        forColumns = Mathf.Abs(d.z);
        forRows = Mathf.Abs(d.x);
        return d.sqrMagnitude > 1e-6f;
    }

    private static float Seam(CreateSheet sheet, bool columns, float coord)
    {
        int min = sheet.BlockMin(columns);
        int max = sheet.BlockMax(columns);
        if (max - min < 1) return -1f;

        float f = sheet.BlockFraction(columns, coord);
        float seam = Mathf.Clamp(Mathf.Round(f - 0.5f), min, max - 1) + 0.5f;
        return 0.5f - Mathf.Abs(f - seam);
    }
}
