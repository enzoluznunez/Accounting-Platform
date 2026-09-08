using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SortTool : Tool
{
    public float reflowSmoothing = 18f;

    private readonly List<SortLineProxy> _proxies = new List<SortLineProxy>();

    private SortLineProxy _held;
    private CreateSheet _dragSheet;
    private bool _dragColumns;
    private int _dragBlock = -1;
    private int _dragTarget;
    private int _blockMin;
    private int _blockMax;

    protected override ToolType Kind => ToolType.Sort;

    protected override void OnToolStart()
    {
        if (sheetManager != null) sheetManager.OnSheetsChanged += RebuildProxies;
    }

    protected override void OnToolDestroy()
    {
        if (sheetManager != null) sheetManager.OnSheetsChanged -= RebuildProxies;
        CompleteOrderSequence();
        CancelDrag();
        ClearProxies();
    }

    private void OnDisable() => CompleteOrderSequence();

    protected override void OnResetTool()
    {
        CompleteOrderSequence();
        CancelDrag();
        if (sheetManager != null) sheetManager.SuppressNextReflow();
        Scene.Data?.ResetOrder();
        RebuildProxies();
    }

    protected override void OnActiveChanged(bool active)
    {
        CancelDrag();
        RebuildProxies();
    }

    protected override void ClearToolState()
    {
        base.ClearToolState();
        CancelDrag();
        RebuildProxies();
    }

    private void RebuildProxies()
    {
        if (_sequence != null) return;
        if (_held != null) CancelDrag();
        ClearProxies();

        if (!Active || sheetManager == null || !sheetManager.IsBuilt) return;

        IReadOnlyList<CreateSheet> sheets = sheetManager.Sheets;

        for (int i = 0; i < sheets.Count; i++)
        {
            CreateSheet sheet = sheets[i];
            if (sheet == null || !sheet.IsBuilt) continue;

            for (int axis = 0; axis < 2; axis++)
            {
                bool columns = axis == 1;
                int min = sheet.BlockMin(columns);
                int max = sheet.BlockMax(columns);
                for (int block = min; block <= max; block++)
                {
                    SortLineProxy proxy = SortLineProxy.Create(sheet, columns, block, sheetManager.Height);
                    if (proxy != null) _proxies.Add(proxy);
                }
            }
        }
    }

    private void ClearProxies()
    {
        for (int i = 0; i < _proxies.Count; i++)
            if (_proxies[i] != null) Destroy(_proxies[i].gameObject);
        _proxies.Clear();
    }

    private SortLineProxy FirstGrabbedProxy()
    {
        for (int i = 0; i < _proxies.Count; i++)
            if (_proxies[i] != null && _proxies[i].IsGrabbed) return _proxies[i];
        return null;
    }

    private void Update()
    {
        if (!Active || sheetManager == null) return;

        if (_held != null && !_held.IsGrabbed) { CommitDrag(); return; }

        if (_held == null)
        {
            SortLineProxy grabbed = FirstGrabbedProxy();
            if (grabbed == null) return;
            BeginDrag(grabbed);
            if (_held == null) return;
        }

        bool columns = _dragColumns;
        float dragged = _held.Coord;

        _dragTarget = Mathf.Clamp(
            Mathf.RoundToInt(_dragSheet.BlockFraction(columns, dragged)), _blockMin, _blockMax);

        Reflow(dragged);
    }

    private void BeginDrag(SortLineProxy proxy)
    {
        CreateSheet sheet = proxy.sheet;
        if (sheet == null) return;

        _dragColumns = proxy.columns;
        bool columns = _dragColumns;

        _dragSheet = sheet;
        _blockMin = sheet.BlockMin(columns);
        _blockMax = sheet.BlockMax(columns);
        _dragBlock = proxy.block;
        _dragTarget = _dragBlock;
        _held = proxy;
    }

    private void Reflow(float dragged)
    {
        float t = 1f - Mathf.Exp(-reflowSmoothing * Time.deltaTime);
        bool columns = _dragColumns;

        for (int block = _blockMin; block <= _blockMax; block++)
        {
            if (block == _dragBlock)
            {
                _dragSheet.LayoutBlock(columns, block, dragged);
                continue;
            }

            float goal = _dragSheet.BlockCoord(columns, DisplaySlot(block, _dragBlock, _dragTarget));
            float now = _dragSheet.BlockOffset(columns, block);
            _dragSheet.LayoutBlock(columns, block, Mathf.Lerp(now, goal, t));
        }
    }

    private void CommitDrag()
    {
        int from = _dragBlock;
        int target = _dragTarget;
        CreateSheet sheet = _dragSheet;

        _held = null;
        ClearDrag();

        if (!MoveLine(_dragColumns, from, target, sheet)) RestLayout(sheet);
        RebuildProxies();
    }

    [Tooltip("Beyond this many steps a set-order request stops animating one line at a time and moves them together.")]
    public int maxSequencedSteps = 24;

    public int LastReorderedLines { get; private set; }

    public bool SequenceRunning => _sequence != null;

    private Coroutine _sequence;
    private System.Action _sequenceFinish;

    public void CompleteOrderSequence()
    {
        if (_sequence != null) { StopCoroutine(_sequence); _sequence = null; }
        var finish = _sequenceFinish;
        _sequenceFinish = null;
        finish?.Invoke();
    }

    public bool HaltOrderSequence()
    {
        if (_sequence == null) return false;

        StopCoroutine(_sequence);
        _sequence = null;
        _sequenceFinish = null;
        if (sheetManager != null) sheetManager.ForceAgentMotion = false;
        RebuildProxies();
        return true;
    }

    public bool SetOrder(bool columns, IReadOnlyList<int> targetOrder)
    {
        if (!Active) return false;

        DataSource src = Scene.Data;
        if (src == null || targetOrder == null || targetOrder.Count == 0) return false;

        CompleteOrderSequence();

        IReadOnlyList<int> live = columns ? src.ColumnOrder : src.RowOrder;
        var preOrder = new List<int>(live);
        DataSource.SortMode preMode = columns ? src.ColumnSortMode : src.RowSortMode;

        // Plan and walk in block space so a grouped axis steps a whole metric at a
        // time; the recorded order stays line-level, so undo is unchanged.
        int size = src.GroupSize(columns);
        List<int> preBlocks = Blocks(preOrder, size);
        List<int> targetBlocks = Blocks(targetOrder, size);

        var steps = PlanSteps(preBlocks, targetBlocks);
        if (steps.Count == 0) return false;

        int changed = 0;
        for (int i = 0; i < targetBlocks.Count && i < preBlocks.Count; i++)
            if (preBlocks[i] != targetBlocks[i]) changed++;
        LastReorderedLines = changed;

        ManageDatasets.ActiveEdits.PushReorder(columns, preOrder, preMode, changed);
        Report($"set the order of {changed} {DataSource.GroupNoun(src, columns)}s");

        var final = new List<int>(targetOrder);
        bool stepwise = sheetManager != null && sheetManager.AgentMotionAnimates
                        && steps.Count <= maxSequencedSteps;

        if (!stepwise)
        {
            Apply(src, columns, final);
            return true;
        }

        sheetManager.ForceAgentMotion = true;
        _sequenceFinish = () => {
            if (sheetManager != null) sheetManager.ForceAgentMotion = false;
            Apply(src, columns, final);
        };
        _sequence = StartCoroutine(WalkOrder(src, columns, steps, final));
        return true;
    }

    private static List<int> Blocks(IReadOnlyList<int> order, int size)
    {
        var blocks = new List<int>((order.Count + size - 1) / size);
        for (int i = 0; i < order.Count; i += size) blocks.Add(order[i]);
        return blocks;
    }

    private static void Apply(DataSource src, bool columns, IReadOnlyList<int> order)
    {
        if (columns) src.SetColumnOrder(order, DataSource.SortMode.Manual);
        else src.SetRowOrder(order, DataSource.SortMode.Manual);
    }

    private struct OrderStep
    {
        public int key;
        public int pos;
    }

    private static List<OrderStep> PlanSteps(IReadOnlyList<int> preOrder, IReadOnlyList<int> targetOrder)
    {
        var working = new List<int>(preOrder);
        var steps = new List<OrderStep>();
        for (int i = 0; i < targetOrder.Count && i < working.Count; i++)
        {
            int want = targetOrder[i];
            int at = working.IndexOf(want);
            if (at < 0 || at == i) continue;
            working.RemoveAt(at);
            working.Insert(i, want);
            steps.Add(new OrderStep { key = want, pos = i });
        }
        return steps;
    }

    private IEnumerator WalkOrder(DataSource src, bool columns, List<OrderStep> steps, List<int> final)
    {
        int size = src.GroupSize(columns);
        for (int i = 0; i < steps.Count; i++)
        {
            OrderStep step = steps[i];
            IReadOnlyList<int> live = columns ? src.ColumnOrder : src.RowOrder;
            int at = -1;
            for (int b = 0; b * size < live.Count; b++) if (live[b * size] == step.key) { at = b; break; }
            if (at < 0 || at == step.pos) continue;

            if (columns) src.MoveColumn(at * size, step.pos * size);
            else src.MoveRow(at, step.pos);

            if (sheetManager != null) yield return sheetManager.WaitForReflow();
            else yield return null;
        }

        if (sheetManager != null) sheetManager.ForceAgentMotion = false;
        _sequence = null;
        _sequenceFinish = null;
        Apply(src, columns, final);
    }

    // 'from' and 'to' are block positions: whole metrics on a grouped axis, single
    // lines otherwise.
    public bool MoveLine(bool columns, int from, int to, CreateSheet piece)
    {
        if (!Active) return false;

        DataSource src = Scene.Data;
        if (src == null || from == to) return false;

        int size = src.GroupSize(columns);
        IReadOnlyList<int> order = columns ? src.ColumnOrder : src.RowOrder;
        int blocks = order.Count / size;
        if (from < 0 || from >= blocks || to < 0 || to >= blocks) return false;

        var preOrder = new List<int>(order);
        DataSource.SortMode preMode = columns ? src.ColumnSortMode : src.RowSortMode;

        int blockMin = piece == null ? 0 : (columns ? piece.colMin : piece.rowMin) / size;
        string where = piece != null ? $" in piece {piece.sheetId}" : "";

        string what = DataSource.GroupLabelAt(src, columns, from);
        string noun = DataSource.GroupNoun(src, columns) + "s";

        var postOrder = new List<int>(preOrder);
        List<int> moved = postOrder.GetRange(from * size, size);
        postOrder.RemoveRange(from * size, size);
        postOrder.InsertRange(to * size, moved);

        string arrangement = "";
        if (blocks <= 40)
        {
            List<string> names = DataSource.BlockTitlesFor(src, columns, postOrder);
            arrangement = $"; the {noun} now run: {string.Join(", ", names)}";
        }

        Report($"moved {what} from position {from - blockMin + 1} to {to - blockMin + 1}{where}{arrangement}");

        if (StateChannel.UserDriven) StalePositions.MarkDirty(columns);

        ManageDatasets.ActiveEdits.PushSort(columns, preOrder, preMode, from, to);

        if (columns) src.MoveColumn(from * size, to * size);
        else src.MoveRow(from, to);
        return true;
    }

    private void CancelDrag()
    {
        CreateSheet sheet = _dragSheet;
        _held = null;
        ClearDrag();
        RestLayout(sheet);
    }

    private void ClearDrag()
    {
        _dragSheet = null;
        _dragBlock = -1;
    }

    private void RestLayout(CreateSheet sheet)
    {
        if (sheet != null) sheet.RestLines();
    }

    private static int DisplaySlot(int slot, int from, int target)
    {
        if (slot == from) return target;
        int p = slot > from ? slot - 1 : slot;
        if (p >= target) p += 1;
        return p;
    }
}
