using System;
using UnityEngine;

public class ReadSheets : MonoBehaviour
{
    public struct Reading
    {
        public bool Valid;
        public SheetPiece Sheet;
        public SheetCube Cube;
        public int VisRow;
        public int VisCol;
        public int DataRow;
        public int DataCol;
        public Vector3 Point;
        public Vector3 Tip;
        public Vector3 Wrist;
        public Vector3 Normal;
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

    private SheetPiece _pressed;

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

        _pressed = reading.Sheet;
        OnSelect?.Invoke(reading);
    }

    public void Release(Reading reading)
    {
        if (!Listening) return;

        bool commits = reading.Valid && _pressed != null && _pressed == reading.Sheet;
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

    public static Reading Describe(SheetCube cube, Vector3 point) => new Reading
    {
        Valid = true,
        Sheet = cube.Sheet,
        Cube = cube,
        VisRow = cube.visRow,
        VisCol = cube.visCol,
        DataRow = cube.dataRow,
        DataCol = cube.dataCol,
        Point = point
    };

    public static Reading Describe(SheetRaycast.Hit hit, Vector3 tip, Vector3 wrist)
    {
        Reading reading = Describe(hit.Cube, hit.Point);
        reading.Tip = tip;
        reading.Wrist = wrist;
        reading.Normal = hit.Normal;
        return reading;
    }
}
