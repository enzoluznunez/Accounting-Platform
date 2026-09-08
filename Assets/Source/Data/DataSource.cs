using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public abstract class DataSource : MonoBehaviour
{

    public enum SortMode { Original, Manual }

    public IReadOnlyList<string> ColumnTitles => _columnTitles;
    public IReadOnlyList<string> RowTitles => _rowTitles;
    public int ColumnCount => _columnTitles.Count;
    public int RowCount => _rowTitles.Count;

    public string ColumnAxisTitle => _columnAxisTitle;
    public string RowAxisTitle => _rowAxisTitle;

    public IReadOnlyList<int> ColumnOrder => _columnOrder;
    public IReadOnlyList<int> RowOrder => _rowOrder;

    public string TitleAt(bool columns, int visIndex)
    {
        IReadOnlyList<int> order = columns ? _columnOrder : _rowOrder;
        IReadOnlyList<string> titles = columns ? _columnTitles : _rowTitles;
        if (order == null || titles == null || visIndex < 0 || visIndex >= order.Count) return null;
        int d = order[visIndex];
        return d >= 0 && d < titles.Count ? titles[d] : null;
    }

    public static string LabelAt(DataSource data, bool columns, int visIndex)
    {
        string title = data != null ? data.TitleAt(columns, visIndex) : null;
        return string.IsNullOrEmpty(title) ? $"{(columns ? "column" : "row")} {visIndex + 1}" : title;
    }
    public static List<string> TitlesFor(DataSource data, bool columns, IReadOnlyList<int> dataIndexes)
    {
        IReadOnlyList<string> titles = columns ? data.ColumnTitles : data.RowTitles;
        var list = new List<string>(dataIndexes.Count);
        for (int i = 0; i < dataIndexes.Count; i++)
        {
            int d = dataIndexes[i];
            list.Add(d >= 0 && d < titles.Count ? titles[d] : null);
        }
        return list;
    }

    public int VisIndexOf(bool columns, int dataIndex)
    {
        IReadOnlyList<int> order = columns ? _columnOrder : _rowOrder;
        if (order == null || dataIndex < 0) return -1;
        for (int v = 0; v < order.Count; v++)
            if (order[v] == dataIndex) return v;
        return -1;
    }

    // Consecutive columns may belong to one group: a metric holding one cell per
    // year, drawn as a tight pair. Rows are never grouped, so both axes share one
    // code path and a group size of 1 is exactly the ungrouped behaviour.

    public int ColumnGroupSize => _columnGroupSize;
    public IReadOnlyList<string> SeriesTitles => _seriesTitles;

    public bool IsGrouped(bool columns) => GroupSize(columns) > 1;

    public int GroupSize(bool columns) => columns ? _columnGroupSize : 1;

    public int GroupCount(bool columns)
    {
        int lines = (columns ? _columnOrder : _rowOrder).Count;
        int size = GroupSize(columns);
        return size > 1 ? lines / size : lines;
    }

    public int GroupOf(bool columns, int visIndex)
    {
        int size = GroupSize(columns);
        return size > 1 ? visIndex / size : visIndex;
    }

    public int SeriesOf(bool columns, int visIndex)
    {
        int size = GroupSize(columns);
        return size > 1 ? visIndex % size : 0;
    }

    public void GroupSpan(bool columns, int group, out int lo, out int hi)
    {
        int size = GroupSize(columns);
        lo = group * size;
        hi = lo + size - 1;
    }

    public string GroupTitleAt(bool columns, int group)
    {
        int size = GroupSize(columns);
        if (size <= 1) return TitleAt(columns, group);

        IReadOnlyList<int> order = columns ? _columnOrder : _rowOrder;
        int vis = group * size;
        if (order == null || vis < 0 || vis >= order.Count) return null;

        return GroupTitleOfData(columns, order[vis]) ?? TitleAt(columns, vis);
    }

    public string GroupTitleOfData(bool columns, int dataIndex)
    {
        IReadOnlyList<string> titles = columns ? _columnTitles : _rowTitles;
        int size = GroupSize(columns);
        if (size <= 1)
            return dataIndex >= 0 && dataIndex < titles.Count ? titles[dataIndex] : null;

        int g = dataIndex / size;
        return g >= 0 && g < _groupTitles.Count ? _groupTitles[g] : null;
    }

    // One name per addressable block in an order: per metric on a grouped axis,
    // per line otherwise.
    public static List<string> BlockTitlesFor(DataSource data, bool columns, IReadOnlyList<int> order)
    {
        var list = new List<string>();
        if (data == null || order == null) return list;

        int size = data.GroupSize(columns);
        for (int i = 0; i < order.Count; i += size)
            list.Add(data.GroupTitleOfData(columns, order[i]));
        return list;
    }

    public string SeriesTitleAt(bool columns, int visIndex)
    {
        int size = GroupSize(columns);
        if (size <= 1) return null;
        int s = visIndex % size;
        return s >= 0 && s < _seriesTitles.Count ? _seriesTitles[s] : null;
    }

    // What one addressable line is called: a metric on a grouped axis, otherwise
    // the column or row itself.
    public static string GroupLabelAt(DataSource data, bool columns, int group)
    {
        string title = data != null ? data.GroupTitleAt(columns, group) : null;
        if (!string.IsNullOrEmpty(title)) return title;
        return $"{GroupNoun(data, columns)} {group + 1}";
    }

    public static string GroupNoun(DataSource data, bool columns) =>
        data != null && data.IsGrouped(columns) ? "metric" : columns ? "column" : "row";

    protected void SetColumnGroupSize(int size)
    {
        _columnGroupSize = size > 1 ? size : 1;
        BuildColumnGroups();
    }

    private void BuildColumnGroups()
    {
        _groupTitles.Clear();
        _seriesTitles.Clear();

        int size = _columnGroupSize;
        if (size <= 1) return;

        int groups = _columnTitles.Count / size;
        for (int g = 0; g < groups; g++)
        {
            int first = g * size;
            string shared = _columnTitles[first];
            for (int s = 1; s < size; s++) shared = SharedPrefix(shared, _columnTitles[first + s]);
            shared = shared.Trim();
            _groupTitles.Add(shared.Length > 0 ? shared : _columnTitles[first]);
        }

        string stem = groups > 0 ? _groupTitles[0] : "";
        for (int s = 0; s < size; s++)
        {
            string title = s < _columnTitles.Count ? _columnTitles[s] : "";
            _seriesTitles.Add(stem.Length > 0 && title.StartsWith(stem, StringComparison.Ordinal)
                ? title.Substring(stem.Length).Trim()
                : title);
        }
    }

    // The shared opening of two titles, cut back to a word boundary so
    // "Revenue 2019" and "Revenue 2020" share "Revenue", not "Revenue 20".
    private static string SharedPrefix(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return "";
        if (string.Equals(a, b, StringComparison.Ordinal)) return a;

        int n = Mathf.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        while (i > 0 && !char.IsWhiteSpace(a[i - 1])) i--;
        return a.Substring(0, i);
    }

    public SortMode ColumnSortMode => _columnSortMode;
    public SortMode RowSortMode => _rowSortMode;

    public bool IsLoaded => _isLoaded;

    public string RawText => _rawText;
    protected string _rawText;

    public event Action OnDataLoaded;

    public event Action OnLayoutInvalidated;
    public event Action OnOrderChanged;

    protected List<string> _columnTitles = new List<string>();
    protected List<string> _rowTitles = new List<string>();
    protected string _columnAxisTitle;
    protected string _rowAxisTitle;
    protected float[,] _values = new float[0, 0];

    protected bool[,] _valid = new bool[0, 0];
    protected float _globalMin;
    protected float _globalMax = 1f;

    protected int _columnGroupSize = 1;
    private readonly List<string> _groupTitles = new List<string>();
    private readonly List<string> _seriesTitles = new List<string>();

    // One scale per column group, so metrics of wildly different size stay
    // readable. Ungrouped data keeps a single bucket, which cancels out and
    // leaves the fractions below arithmetically unchanged.
    private float[] _scales = new float[0];

    protected List<int> _columnOrder = new List<int>();
    protected List<int> _rowOrder = new List<int>();
    protected SortMode _columnSortMode = SortMode.Original;
    protected SortMode _rowSortMode = SortMode.Original;

    protected bool _isLoaded;

    protected void NotifyChanged()
    {
        EnsureOrders();
        OnLayoutInvalidated?.Invoke();
    }

    protected int FillGrid(int rowCount, int colCount, Func<int, int, float?> sample)
    {
        _values = new float[rowCount, colCount];
        _valid = new bool[rowCount, colCount];
        int filled = 0;

        for (int r = 0; r < rowCount; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                float? v = sample(r, c);
                if (v.HasValue)
                {
                    _values[r, c] = v.Value;
                    _valid[r, c] = true;
                    filled++;
                }
                else
                {
                    _values[r, c] = float.NaN;
                    _valid[r, c] = false;
                }
            }
        }

        BuildScales(rowCount, colCount, filled);
        return filled;
    }

    private int BucketOf(int colIndex) => _columnGroupSize > 1 ? colIndex / _columnGroupSize : 0;

    // Each group is divided by its own largest magnitude before any fraction is
    // taken, so a metric in trillions and a metric in single dollars both fill the
    // bar height, and zero stays on one shared plane. With a single bucket the
    // divisor cancels from every fraction below, so ungrouped data is unaffected.
    private void BuildScales(int rowCount, int colCount, int filled)
    {
        int buckets = _columnGroupSize > 1 ? BucketOf(colCount - 1) + 1 : 1;
        _scales = new float[Mathf.Max(buckets, 1)];

        for (int c = 0; c < colCount; c++)
        {
            int b = BucketOf(c);
            for (int r = 0; r < rowCount; r++)
            {
                if (!_valid[r, c]) continue;
                float m = Mathf.Abs(_values[r, c]);
                if (m > _scales[b]) _scales[b] = m;
            }
        }

        for (int b = 0; b < _scales.Length; b++)
            if (_scales[b] <= 0f) _scales[b] = 1f;

        _globalMin = float.MaxValue;
        _globalMax = float.MinValue;
        for (int r = 0; r < rowCount; r++)
            for (int c = 0; c < colCount; c++)
            {
                if (!_valid[r, c]) continue;
                float n = Normalized(r, c);
                if (n < _globalMin) _globalMin = n;
                if (n > _globalMax) _globalMax = n;
            }

        if (filled == 0)
        {
            _globalMin = 0f;
            _globalMax = 1f;
        }
        else if (Mathf.Approximately(_globalMin, _globalMax))
        {
            if (_globalMin > 0f) _globalMin = 0f;
            else if (_globalMax < 0f) _globalMax = 0f;
            else _globalMax = _globalMin + 1f;
        }
    }

    private float Normalized(int rowIndex, int colIndex)
    {
        int b = BucketOf(colIndex);
        float k = b >= 0 && b < _scales.Length ? _scales[b] : 1f;
        return k > 0f ? _values[rowIndex, colIndex] / k : _values[rowIndex, colIndex];
    }

    protected virtual void EnsureOrders()
    {
        EnsureOrder(_columnOrder, ColumnCount);
        EnsureOrder(_rowOrder, RowCount);
    }

    private void RaiseOrderChanged()
    {
        NotifyChanged();
        OnOrderChanged?.Invoke();
    }

    public void ResetOrder()
    {
        _columnSortMode = SortMode.Original;
        _rowSortMode = SortMode.Original;
        InitIdentity(_columnOrder, ColumnCount);
        InitIdentity(_rowOrder, RowCount);
        RaiseOrderChanged();
    }

    public bool SetColumnOrder(IReadOnlyList<int> order, SortMode mode)
    {
        if (!ApplyOrder(_columnOrder, order, ColumnCount, _columnGroupSize)) return false;
        _columnSortMode = mode;
        RaiseOrderChanged();
        return true;
    }

    public bool SetRowOrder(IReadOnlyList<int> order, SortMode mode)
    {
        if (!ApplyOrder(_rowOrder, order, RowCount, 1)) return false;
        _rowSortMode = mode;
        RaiseOrderChanged();
        return true;
    }

    // Positions are line indexes on both axes; on a grouped axis the whole group
    // holding 'fromPos' travels to the group slot holding 'toPos'.
    public void MoveColumn(int fromPos, int toPos)
    {
        bool moved = _columnGroupSize > 1
            ? MoveGroupWithin(_columnOrder, fromPos / _columnGroupSize, toPos / _columnGroupSize, _columnGroupSize)
            : MoveWithin(_columnOrder, fromPos, toPos);
        if (moved) SetColumnOrder(_columnOrder, SortMode.Manual);
    }

    public void MoveRow(int fromPos, int toPos)
    {
        if (MoveWithin(_rowOrder, fromPos, toPos)) SetRowOrder(_rowOrder, SortMode.Manual);
    }

    private static bool MoveWithin(List<int> order, int fromPos, int toPos)
    {
        if (fromPos < 0 || fromPos >= order.Count) return false;
        toPos = Mathf.Clamp(toPos, 0, order.Count - 1);
        if (toPos == fromPos) return false;
        int value = order[fromPos];
        order.RemoveAt(fromPos);
        order.Insert(toPos, value);
        return true;
    }

    private static bool MoveGroupWithin(List<int> order, int fromGroup, int toGroup, int groupSize)
    {
        int groups = order.Count / groupSize;
        if (groups <= 0 || fromGroup < 0 || fromGroup >= groups) return false;
        toGroup = Mathf.Clamp(toGroup, 0, groups - 1);
        if (toGroup == fromGroup) return false;

        List<int> block = order.GetRange(fromGroup * groupSize, groupSize);
        order.RemoveRange(fromGroup * groupSize, groupSize);
        order.InsertRange(toGroup * groupSize, block);
        return true;
    }

    private static bool ApplyOrder(List<int> target, IReadOnlyList<int> order, int count, int groupSize)
    {
        if (order == null || order.Count != count || !IsPermutation(order, count)) return false;
        if (!IsGroupAligned(order, groupSize)) return false;

        if (!ReferenceEquals(target, order))
        {
            target.Clear();
            for (int i = 0; i < order.Count; i++) target.Add(order[i]);
        }
        return true;
    }

    // A grouped axis may only be reordered by whole groups, each keeping its
    // members in sequence. Rejecting anything else here makes it impossible for
    // any caller, by hand or by tool, to split a pair.
    private static bool IsGroupAligned(IReadOnlyList<int> order, int groupSize)
    {
        if (groupSize <= 1) return true;
        if (order.Count % groupSize != 0) return false;

        for (int i = 0; i < order.Count; i += groupSize)
        {
            int first = order[i];
            if (first % groupSize != 0) return false;
            for (int s = 1; s < groupSize; s++)
                if (order[i + s] != first + s) return false;
        }
        return true;
    }

    private static bool IsPermutation(IReadOnlyList<int> order, int count)
    {
        if (count == 0) return order.Count == 0;
        bool[] seen = new bool[count];
        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            if (v < 0 || v >= count || seen[v]) return false;
            seen[v] = true;
        }
        return true;
    }

    private static void InitIdentity(List<int> order, int count)
    {
        order.Clear();
        for (int i = 0; i < count; i++) order.Add(i);
    }

    private static void EnsureOrder(List<int> order, int count)
    {
        if (order.Count == count) return;
        InitIdentity(order, count);
    }

    public float GetValue(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || rowIndex >= RowCount || colIndex < 0 || colIndex >= ColumnCount)
            return 0f;
        return _values[rowIndex, colIndex];
    }

    public bool HasValue(int rowIndex, int colIndex)
    {
        return rowIndex >= 0 && rowIndex < _valid.GetLength(0) &&
               colIndex >= 0 && colIndex < _valid.GetLength(1) &&
               _valid[rowIndex, colIndex];
    }

    private static bool TryBaselineRange(float min, float max, out float lo, out float range)
    {
        lo = Mathf.Min(0f, min);
        range = Mathf.Max(0f, max) - lo;
        return range > 0f;
    }

    public float GetHeightFraction(int rowIndex, int colIndex)
    {
        if (!HasValue(rowIndex, colIndex)) return 0f;
        if (!TryBaselineRange(_globalMin, _globalMax, out float lo, out float range)) return 0f;
        return (Normalized(rowIndex, colIndex) - lo) / range;
    }

    public float ZeroFraction =>
        TryBaselineRange(_globalMin, _globalMax, out float lo, out float range) ? -lo / range : 0f;

    public float GetColorFraction(int rowIndex, int colIndex)
    {
        float range = _globalMax - _globalMin;
        if (range <= 0f) return 0f;

        if (!HasValue(rowIndex, colIndex)) return 0f;
        float v = Normalized(rowIndex, colIndex);
        if (float.IsNaN(v)) return 0f;
        return (v - _globalMin) / range;
    }

    protected void RaiseDataLoaded() => RaiseLoadComplete(true);

    protected void RaiseLoadFailed() => RaiseLoadComplete(false);

    private void RaiseLoadComplete(bool loaded)
    {
        _isLoaded = loaded;
        _columnSortMode = SortMode.Original;
        _rowSortMode = SortMode.Original;
        InitIdentity(_columnOrder, ColumnCount);
        InitIdentity(_rowOrder, RowCount);
        OnDataLoaded?.Invoke();
    }
}
