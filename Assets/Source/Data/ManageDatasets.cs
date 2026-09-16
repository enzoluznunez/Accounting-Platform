using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class ManageDatasets : MonoBehaviour
{
    public static ManageDatasets Instance { get; private set; }
    public static DataSource ActiveSource => Instance != null ? Instance.Active : null;

    public ManageSheets sheetManager;
    public PanelUI dataPanelUI;
    public ManageTools toolManager;

    private IDataPanel Panel => dataPanelUI as IDataPanel;

    public event Action OnDatasetsChanged;
    public event Action<int> OnActiveDatasetChanged;
    public event Action<string> OnDatasetLoadFailed;

    public class Dataset
    {
        public DataSource source;
        public string label;
        public string payload;
        public int sheetId = ManageSheets.FirstSheetId;
        public bool loaded;

        // A parse is in flight exactly while a reader exists that has not
        // answered yet; the result handler and Unload both clear the reader.
        public bool loading => source != null && !loaded;

        // Listed from the industry index rather than scanned in: it stands in the
        // rail from startup and is parsed the first time someone opens it, so a
        // session begins with every industry named and none of them read.
        public bool catalogued;
        public int companies;

        public readonly EditList Edits = new EditList();
    }

    private readonly List<Dataset> _datasets = new List<Dataset>();
    private int _active = -1;
    private int _datasetsCreated;
    private static readonly EditList Unowned = new EditList();

    public IReadOnlyList<Dataset> Datasets => _datasets;
    public int ActiveIndex => _active;
    public int DatasetCount => _datasets.Count;
    public DataSource Active => (_active >= 0 && _active < _datasets.Count) ? _datasets[_active].source : null;
    public Dataset ActiveDataset => (_active >= 0 && _active < _datasets.Count) ? _datasets[_active] : null;

    public static EditList ActiveEdits
    {
        get
        {
            Dataset d = Instance != null ? Instance.ActiveDataset : null;
            return d != null ? d.Edits : Unowned;
        }
    }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        ResolveRefs();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void ResolveRefs()
    {
        if (sheetManager == null) sheetManager = FindAnyObjectByType<ManageSheets>();
        if (dataPanelUI == null)
        {
            var panels = FindObjectsByType<PanelUI>(FindObjectsSortMode.None);
            for (int i = 0; i < panels.Length; i++)
                if (panels[i] is IDataPanel) { dataPanelUI = panels[i]; break; }
        }
        if (toolManager == null) toolManager = FindAnyObjectByType<ManageTools>();
    }

    public void AddDataset(string payload, string label = null)
    {
        if (string.IsNullOrEmpty(payload)) return;

        for (int i = 0; i < _datasets.Count; i++)
            if (_datasets[i].payload == payload)
            {
                bool alreadyActive = i == _active;
                string openLabel = _datasets[i].label;
                SwitchDataset(i);
                Notices.Show(this, "Already Open",
                    alreadyActive ? $"{openLabel} is already open." : $"Switched to {openLabel}.");
                return;
            }

        Dataset dataset = new Dataset
        {
            payload = payload,
            label = label ?? Stylize(DeriveLabel(payload, _datasetsCreated))
        };
        _datasets.Add(dataset);
        BeginLoad(dataset);
    }

    // A dataset the app knows of but has not read. It is listed straight away and
    // costs nothing until it is opened.
    public void AddCatalogEntry(string file, string label, int companies)
    {
        if (AddCatalogEntryQuietly(file, label, companies)) OnDatasetsChanged?.Invoke();
    }

    // The rail is torn down and rebuilt on every change, so a whole index is
    // listed in one go and announced once rather than once per industry.
    public void AddCatalogEntries(IEnumerable<(string file, string label, int companies)> entries)
    {
        if (entries == null) return;

        bool added = false;
        foreach ((string file, string label, int companies) in entries)
            added |= AddCatalogEntryQuietly(file, label, companies);

        if (added) OnDatasetsChanged?.Invoke();
    }

    private bool AddCatalogEntryQuietly(string file, string label, int companies)
    {
        if (string.IsNullOrEmpty(file)) return false;

        for (int i = 0; i < _datasets.Count; i++)
            if (_datasets[i].payload == file) return false;

        _datasets.Add(new Dataset
        {
            payload = file,
            label = string.IsNullOrEmpty(label) ? Stylize(DeriveLabel(file, _datasets.Count)) : label,
            catalogued = true,
            companies = companies
        });
        return true;
    }

    private readonly Dictionary<Dataset, TaskCompletionSource<bool>> _awaitingLoad =
        new Dictionary<Dataset, TaskCompletionSource<bool>>();

    // Reads a listed dataset if it has not been read, and answers when it is
    // ready either way. The assistant awaits this so that it never reports
    // switching to an industry the app is still reading.
    public Task<bool> EnsureLoaded(int index)
    {
        if (index < 0 || index >= _datasets.Count) return Task.FromResult(false);

        Dataset dataset = _datasets[index];
        if (dataset.loaded) return Task.FromResult(true);

        if (!_awaitingLoad.TryGetValue(dataset, out TaskCompletionSource<bool> waiting))
        {
            waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingLoad[dataset] = waiting;
        }

        BeginLoad(dataset);
        return waiting.Task;
    }

    private void SettleLoad(Dataset dataset, bool ok)
    {
        if (!_awaitingLoad.TryGetValue(dataset, out TaskCompletionSource<bool> waiting)) return;
        _awaitingLoad.Remove(dataset);
        waiting.SetResult(ok);
    }

    private void BeginLoad(Dataset dataset)
    {
        if (dataset == null || dataset.loading || dataset.loaded) return;

        GameObject host = new GameObject($"Dataset_{_datasetsCreated++}");
        host.transform.SetParent(transform, false);

        Parser reader = host.AddComponent<Parser>();
        dataset.source = reader;

        reader.onLoadResult = (ok, reason) => OnDatasetLoadResult(reader, ok, reason);
        reader.Load(dataset.payload);
    }

    private int IndexOfSource(DataSource source)
    {
        for (int i = 0; i < _datasets.Count; i++)
            if (_datasets[i].source == source) return i;
        return -1;
    }

    private void OnDatasetLoadResult(Parser reader, bool ok, string reason)
    {
        int index = IndexOfSource(reader);
        if (index < 0) return;

        Dataset dataset = _datasets[index];
        SettleLoad(dataset, ok);

        if (!ok)
        {
            // An industry stays in the rail when its file will not read: it is
            // still one of the industries, and trying again is a tap away.
            if (dataset.catalogued)
            {
                Unload(dataset);
                Notices.Show(this, "Industry Unavailable",
                    reason ?? $"{dataset.label} could not be read.");
                OnDatasetsChanged?.Invoke();
                return;
            }

            string payload = dataset.payload;
            RemoveDataset(index);
            OnDatasetLoadFailed?.Invoke(payload);
            Notices.Show(this, "Scan Failed",
                reason ?? "Couldn't read a dataset from that QR code.");
            return;
        }

        dataset.loaded = true;

        StateChannel.Record("Dataset", $"loaded a new dataset, {dataset.label}");
        OnDatasetsChanged?.Invoke();
        SwitchDataset(index);
    }

    public void RemoveDataset(int index)
    {
        if (index < 0 || index >= _datasets.Count) return;

        Dataset dataset = _datasets[index];
        bool wasActive = index == _active;

        if (wasActive && sheetManager != null) sheetManager.CommitPendingGrabs();

        _datasets.RemoveAt(index);
        if (_active > index) _active--;
        else if (wasActive) _active = -1;

        if (dataset.source != null) Destroy(dataset.source.gameObject);

        if (wasActive)
        {
            if (_datasets.Count > 0) SwitchDataset(_datasets.Count - 1);
            else
            {
                if (Panel != null) Panel.Rebind(null);
                if (sheetManager != null) sheetManager.SetDataSource(null);
            }
        }

        if (dataset.loaded)
            StateChannel.Record("Dataset", $"closed the dataset {dataset.label}");
        OnDatasetsChanged?.Invoke();
        if (wasActive && _datasets.Count == 0) OnActiveDatasetChanged?.Invoke(-1);
    }


    public void SwitchDataset(int index)
    {
        if (index < 0 || index >= _datasets.Count || index == _active) return;

        // A listed industry is read here, the first time it is asked for. The
        // parse finishes on a later frame and lands back in OnDatasetLoadResult,
        // which switches to it then.
        if (!_datasets[index].loaded)
        {
            BeginLoad(_datasets[index]);
            return;
        }

        if (sheetManager != null) sheetManager.CommitPendingGrabs();
        if (toolManager != null) toolManager.DeselectTool();

        if (_active >= 0 && _active < _datasets.Count && Panel != null)
            _datasets[_active].sheetId = Panel.ActiveSheetId;

        _active = index;
        Dataset next = _datasets[index];

        Rebind(next);
        if (sheetManager != null) sheetManager.PlaySwitchGrow();
        OnActiveDatasetChanged?.Invoke(index);

        if (Panel != null) Panel.ShowSheet(next.sheetId);

        StateChannel.RecordState("dataset",
            $"the {(string.IsNullOrEmpty(next.label) ? "dataset" : next.label)} dataset is open" +
            "; sheet ids, row and column numbers and edits all belong to it");

        ReportAxes(next.source);
    }

    private static void ReportAxes(DataSource source)
    {
        if (source == null) return;

        string columns = AxisSketch(source, true);
        string rows = AxisSketch(source, false);
        if (columns == null || rows == null) return;

        StateChannel.SetState("axes", $"the columns are {columns}; the rows are {rows}");
    }

    private static string AxisSketch(DataSource source, bool columns)
    {
        IReadOnlyList<int> order = columns ? source.ColumnOrder : source.RowOrder;
        if (order == null || order.Count == 0) return null;

        var shown = new List<string>(2);
        for (int i = 0; i < order.Count && shown.Count < 2; i++)
        {
            string title = source.TitleAt(columns, i);
            if (!string.IsNullOrEmpty(title)) shown.Add(title);
        }
        if (shown.Count == 0) return null;

        int rest = order.Count - shown.Count;
        string list = string.Join(", ", shown);
        return rest > 0 ? $"{list} and {rest} more" : list;
    }

    // Gives back everything the parse held, and leaves the entry listed.
    private void Unload(Dataset dataset)
    {
        dataset.loaded = false;
        if (dataset.source != null) Destroy(dataset.source.gameObject);
        dataset.source = null;
    }

    private void Rebind(Dataset dataset)
    {
        TryStep("dataPanel", () => { if (Panel != null) Panel.Rebind(dataset.source); });
        TryStep("sheetManager", () => { if (sheetManager != null) sheetManager.SetDataSource(dataset.source); });
        TryStep("replay", () => { if (sheetManager != null) sheetManager.ReplayEdits(dataset.Edits); });
    }

    private void TryStep(string label, Action step)
    {
        try { step(); }
        catch (Exception e) { Debug.LogError($"[ManageDatasets] Rebind step '{label}' failed: {e}"); }
    }

    private static string Stylize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        string[] words = raw.Split(new[] { '_', '-', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return raw;

        for (int i = 0; i < words.Length; i++)
        {
            if (HasInnerUppercase(words[i])) continue;
            string w = words[i].ToLowerInvariant();
            bool edge = i == 0 || i == words.Length - 1;
            words[i] = (!edge && SmallWords.Contains(w))
                ? w
                : char.ToUpperInvariant(w[0]) + w.Substring(1);
        }
        return string.Join(" ", words);
    }

    private static readonly HashSet<string> SmallWords = new HashSet<string>
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "if", "in", "nor", "of",
        "on", "or", "per", "so", "the", "to", "up", "via", "vs", "yet"
    };

    private static bool HasInnerUppercase(string word)
    {
        for (int i = 1; i < word.Length; i++)
            if (char.IsUpper(word[i])) return true;
        return false;
    }

    private static string DeriveLabel(string payload, int ordinal)
    {
        string fallback = $"Dataset {ordinal + 1}";
        if (string.IsNullOrEmpty(payload)) return fallback;

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DeriveDataUriName(payload) ?? fallback;

        if (payload.IndexOf('\n') >= 0)
            return fallback;

        string trimmed = payload.Trim();
        int query = trimmed.IndexOf('?');
        if (query >= 0) trimmed = trimmed.Substring(0, query);
        trimmed = trimmed.TrimEnd('/');

        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name.Substring(0, dot);

        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    private static string DeriveDataUriName(string payload)
    {
        int comma = payload.IndexOf(',');
        string header = comma >= 0 ? payload.Substring(0, comma) : payload;

        int nameIdx = header.IndexOf("name=", StringComparison.OrdinalIgnoreCase);
        if (nameIdx < 0) return null;

        string name = header.Substring(nameIdx + 5);
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name.Substring(0, semi);
        name = name.Trim();

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name.Substring(0, dot);

        return string.IsNullOrEmpty(name) ? null : name;
    }
}
