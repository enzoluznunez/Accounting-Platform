using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.Input;

[RequireComponent(typeof(ReadSheets))]
public class SheetTouch : MonoBehaviour
{
    [Tooltip("How far past the fingertip a bar starts responding, in metres.")]
    public float hoverEnter = 0.02f;

    [Tooltip("How far the fingertip must withdraw before the bar stops responding. Keep above hoverEnter.")]
    public float hoverExit = 0.05f;

    [Tooltip("Seconds between sweeps for poke interactors that appear after start.")]
    public float rescanSeconds = 2f;

    [Tooltip("Furthest a fingertip can credibly be from the head, in metres. Guards against parked interactors.")]
    public float maxReachFromHead = 1.2f;

    private struct Source
    {
        public PokeInteractor poke;
        public IHand hand;
    }

    private ReadSheets _hub;
    private readonly List<Source> _pokes = new List<Source>();
    private float _nextScan;

    private PokeInteractor _driver;
    private CreateCube _cube;
    private bool _selected;

    private void Awake() => _hub = GetComponent<ReadSheets>();

    private void OnDisable() => Drop();

    private void Update()
    {
        if (_hub == null) return;

        Rescan();

        if (!_hub.Listening) { Drop(); return; }

        if (_driver != null && !Track(_driver)) Drop();
        if (_driver != null) return;

        for (int i = 0; i < _pokes.Count; i++)
        {
            Source source = _pokes[i];
            if (!Live(source)) continue;
            if (!Acquire(source)) continue;
            _driver = source.poke;
            return;
        }
    }

    private void Rescan()
    {
        if (Time.unscaledTime < _nextScan && _pokes.Count > 0) return;
        _nextScan = Time.unscaledTime + Mathf.Max(rescanSeconds, 0.25f);

        _pokes.Clear();

        PokeInteractor[] found = FindObjectsByType<PokeInteractor>(FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            IHand hand = found[i].GetComponentInParent<IHand>();
            if (hand == null) continue;
            _pokes.Add(new Source { poke = found[i], hand = hand });
        }
    }

    private bool Live(Source source)
    {
        PokeInteractor poke = source.poke;
        if (poke == null || !poke.isActiveAndEnabled) return false;
        if (source.hand == null || !source.hand.IsTrackedDataValid) return false;

        Transform head = CameraRig.MainTransform;
        if (head == null) return false;

        return (poke.Origin - head.position).sqrMagnitude <=
            maxReachFromHead * maxReachFromHead;
    }

    private Source Current()
    {
        for (int i = 0; i < _pokes.Count; i++)
            if (_pokes[i].poke == _driver) return _pokes[i];
        return default;
    }

    private static Vector3 WristOf(Source source, Vector3 tip) =>
        source.hand != null && source.hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist)
            ? wrist.position
            : tip;

    private bool Acquire(Source source)
    {
        PokeInteractor poke = source.poke;
        Vector3 tip = poke.Origin;
        float reach = Mathf.Max(poke.Radius, 0f) + Mathf.Max(hoverEnter, 0f);
        if (!SheetRaycast.NearestCube(tip, reach, out SheetRaycast.Hit hit)) return false;

        _cube = hit.cube;
        _selected = false;
        _hub.Hover(ReadSheets.Describe(hit, tip, WristOf(source, tip)));
        return true;
    }

    private bool Track(PokeInteractor poke)
    {
        Source source = Current();
        if (!Live(source)) return false;

        Vector3 tip = poke.Origin;
        float radius = Mathf.Max(poke.Radius, 0f);
        float reach = radius + Mathf.Max(hoverExit, hoverEnter);

        if (!SheetRaycast.NearestCube(tip, reach, out SheetRaycast.Hit hit)) return false;

        ReadSheets.Reading reading = ReadSheets.Describe(hit, tip, WristOf(source, tip));
        _cube = hit.cube;

        if (_selected)
        {
            if (hit.distance <= radius + Mathf.Max(hoverExit, 0f))
            {
                _hub.Hover(reading);
                return true;
            }

            _selected = false;
            _hub.Release(reading);
            return true;
        }

        if (hit.distance <= radius || SheetRaycast.Contains(hit.cube, tip))
        {
            _selected = true;
            _hub.Select(reading);
            return true;
        }

        _hub.Hover(reading);
        return true;
    }

    private void Drop()
    {
        if (_driver == null && _cube == null && !_selected) return;

        bool wasSelected = _selected;
        CreateCube cube = _cube;

        _driver = null;
        _cube = null;
        _selected = false;

        if (_hub == null) return;

        if (wasSelected && cube != null)
            _hub.Release(ReadSheets.Describe(cube, cube.transform.position));

        _hub.Cleared();
    }
}
