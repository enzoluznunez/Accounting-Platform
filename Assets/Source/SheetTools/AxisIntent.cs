using UnityEngine;

public sealed class AxisIntent
{
    public float deadband = 0.25f;

    private bool _columns = true;
    private bool _decided;
    private bool _latched;

    public bool Columns => _columns;
    public bool Decided => _decided;

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

    public static bool ReachScores(CreateSheet sheet, Vector3 wrist, Vector3 tip, out float forColumns, out float forRows)
    {
        Vector3 d = sheet.transform.InverseTransformDirection(tip - wrist);
        d.y = 0f;
        forColumns = Mathf.Abs(d.z);
        forRows = Mathf.Abs(d.x);
        return d.sqrMagnitude > 1e-6f;
    }
}
