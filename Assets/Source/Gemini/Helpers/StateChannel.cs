using System.Collections.Generic;

public static class StateChannel
{
    private const int MaxUserQueue = 40;

    private static int _agentDepth;

    public static bool InAgentCall
    {
        get => _agentDepth > 0;
        set
        {
            if (value) _agentDepth++;
            else if (_agentDepth > 0) _agentDepth--;
        }
    }

    public static bool Suppressed { get; set; }

    public static bool UserDriven => !InAgentCall && !Suppressed;

    private static readonly List<string> _agentBatch = new List<string>();
    private static readonly List<string> _userQueue = new List<string>();
    private static readonly Dictionary<string, string> _state = new Dictionary<string, string>();
    private static readonly List<string> _stateOrder = new List<string>();
    private static bool _snapshotWanted;

    public static void Record(string source, string what)
    {
        if (string.IsNullOrEmpty(what)) return;

        if (InAgentCall)
        {
            _agentBatch.Add(what);
            return;
        }

        if (Suppressed) return;

        _userQueue.Add(what);
        while (_userQueue.Count > MaxUserQueue) _userQueue.RemoveAt(0);
        Gemini.RequestActionPush();
    }

    public static string TakeAgentBatch()
    {
        if (_agentBatch.Count == 0) return null;
        string text = string.Join("; ", _agentBatch);
        _agentBatch.Clear();
        return text;
    }

    public static void SetState(string key, string what)
    {
        if (string.IsNullOrEmpty(what)) return;
        if (!_state.ContainsKey(key)) _stateOrder.Add(key);
        _state[key] = what;
    }

    public static void RecordState(string key, string what)
    {
        SetState(key, what);
        Record(key, what);
    }

    public static void ClearPending()
    {
        _userQueue.Clear();
        _agentBatch.Clear();
        _agentDepth = 0;
        _snapshotWanted = false;
        StalePositions.ClearAll();
        PanelGuard.ClearToolPanel();
        DescribeDataset.Forget();
    }

    public static void RequestSnapshot()
    {
        _snapshotWanted = true;
        Gemini.RequestActionPush();
    }

    public static bool HasPending => _snapshotWanted || _userQueue.Count > 0;

    public static bool TryTakeBatch(out string text)
    {
        text = null;
        if (!HasPending) return false;

        var lines = new List<string>(2);

        if (_snapshotWanted)
        {
            _snapshotWanted = false;
            if (_stateOrder.Count > 0)
            {
                var facts = new List<string>(_stateOrder.Count);
                for (int i = 0; i < _stateOrder.Count; i++) facts.Add(_state[_stateOrder[i]]);
                lines.Add("[state] " + string.Join("; ", facts));
            }
        }

        if (_userQueue.Count > 0)
        {
            lines.Add("[tool] User: " + string.Join("; ", _userQueue));
            _userQueue.Clear();
        }

        if (lines.Count == 0) return false;
        text = string.Join("\n", lines);
        return true;
    }
}
