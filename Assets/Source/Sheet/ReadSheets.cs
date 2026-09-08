using System;
using UnityEngine;

public class ReadSheets : MonoBehaviour
{
    public struct Reading
    {
        public bool valid;
        public CreateSheet sheet;
        public CreateCube cube;
        public int visRow;
        public int visCol;
        public int dataRow;
        public int dataCol;
        public Vector3 point;
        public Vector3 tip;
        public Vector3 wrist;
        public Vector3 normal;
    }

    public event Action<Reading> OnHover;
    public event Action<Reading> OnSelect;
    public event Action<Reading> OnRelease;
    public event Action<Reading> OnCommit;
    public event Action OnCleared;

    public bool Listening => OnHover != null || OnSelect != null || OnRelease != null ||
        OnCommit != null || OnCleared != null;

    [Tooltip("Let a fingertip drive the sheet. Turn off only to take the sheet out of play.")]
    public bool touchInput = true;

    private CreateSheet _pressed;

    private void Awake()
    {
        if (touchInput && GetComponent<SheetTouch>() == null) gameObject.AddComponent<SheetTouch>();
    }

    public void Hover(Reading reading)
    {
        if (!Listening) return;
        OnHover?.Invoke(reading);
    }

    public void Select(Reading reading)
    {
        if (!Listening) return;

        _pressed = reading.sheet;
        OnSelect?.Invoke(reading);
    }

    public void Release(Reading reading)
    {
        if (!Listening) return;

        bool commits = reading.valid && _pressed != null && _pressed == reading.sheet;
        _pressed = null;

        OnRelease?.Invoke(reading);
        if (commits) OnCommit?.Invoke(reading);
    }

    public void Cleared()
    {
        if (!Listening) return;

        _pressed = null;
        OnCleared?.Invoke();
    }

    public static Reading Describe(CreateCube cube, Vector3 point) => new Reading
    {
        valid = true,
        sheet = cube.Sheet,
        cube = cube,
        visRow = cube.visRow,
        visCol = cube.visCol,
        dataRow = cube.dataRow,
        dataCol = cube.dataCol,
        point = point
    };

    public static Reading Describe(SheetRaycast.Hit hit, Vector3 tip, Vector3 wrist)
    {
        Reading reading = Describe(hit.cube, hit.point);
        reading.tip = tip;
        reading.wrist = wrist;
        reading.normal = hit.normal;
        return reading;
    }
}
