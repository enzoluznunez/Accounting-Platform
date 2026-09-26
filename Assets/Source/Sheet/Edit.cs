using System.Collections.Generic;
using UnityEngine;

public enum EditKind { Move, Rotate, Scale, Sort, Filter, Profile }

public struct MoveRecord
{
    public int sheetId;
    public Vector3 prePos;
    public Quaternion preRot;
    public Vector3 preScale;
    public Vector3 postPos;
    public Quaternion postRot;
    public Vector3 postScale;
    public float distance;
}

// Always a strip: one whole row or column raised above the sheet.
public struct ProjectionRecord
{
    public bool isColumn;
    public int dataRow;
    public int dataCol;
    public float lift;
}

public class Edit
{
    public EditKind kind;
    public int sheetId = -1;
    public MoveRecord move;
    public ProjectionRecord projection;

    public bool reorderIsColumn;
    public List<int> reorderPreOrder;
    public DataSource.SortMode reorderPreMode;
    public int reorderFrom;
    public int reorderTarget;
    public int reorderLines;

    public int group;

    // Which column groups were hidden before this filter, and after it. Undo puts
    // the first back; a replay onto a freshly loaded dataset applies the second.
    public List<int> filterPreHidden;
    public List<int> filterPostHidden;

    // Which axis the hidden set belongs to. One edit only ever covers one axis,
    // so a press on the company list and a press on the metric list undo apart.
    public bool filterIsRow;

    public static string KindName(EditKind kind)
    {
        switch (kind)
        {
            case EditKind.Move: return "move";
            case EditKind.Rotate: return "rotate";
            case EditKind.Scale: return "scale";
            case EditKind.Sort: return "sort";
            case EditKind.Filter: return "filter";
            case EditKind.Profile: return "profile";
            default: return "edit";
        }
    }
}

public class EditList : List<Edit>
{
    private int _groupSeq;
    private int _group;

    public int OpenGroup() => _group = ++_groupSeq;
    public void CloseGroup() => _group = 0;

    public Edit Peek() => Count > 0 ? this[Count - 1] : null;

    public int TopGroupSize()
    {
        Edit top = Peek();
        if (top == null) return 0;
        if (top.group == 0) return 1;

        int n = 0;
        for (int i = Count - 1; i >= 0 && this[i].group == top.group; i--) n++;
        return n;
    }

    public int UndoStepCount()
    {
        int steps = 0;
        int i = Count - 1;
        while (i >= 0)
        {
            int g = this[i].group;
            steps++;
            if (g == 0) { i--; continue; }
            while (i >= 0 && this[i].group == g) i--;
        }
        return steps;
    }

    public static event System.Action OnChanged;

    private static void RaiseChanged() => OnChanged?.Invoke();

    private void Push(Edit e)
    {
        e.group = _group;
        Add(e);
        RaiseChanged();
    }

    public void DropAt(int index)
    {
        if (index < 0 || index >= Count) return;
        RemoveAt(index);
        RaiseChanged();
    }

    public int StampGroup(int fromIndex)
    {
        if (fromIndex < 0) fromIndex = 0;
        if (fromIndex >= Count) return 0;

        int id = ++_groupSeq;
        for (int i = fromIndex; i < Count; i++) this[i].group = id;
        return Count - fromIndex;
    }

    public Edit Pop()
    {
        if (Count == 0) return null;
        Edit e = this[Count - 1];
        RemoveAt(Count - 1);
        RaiseChanged();
        return e;
    }

    public void PushMove(MoveRecord move, EditKind kind) =>
        Push(new Edit { kind = kind, sheetId = move.sheetId, move = move });

    public void PushSort(bool isColumn, IReadOnlyList<int> preOrder, DataSource.SortMode preMode, int from, int target) =>
        Push(new Edit
        {
            kind = EditKind.Sort,
            reorderIsColumn = isColumn,
            reorderPreOrder = preOrder != null ? new List<int>(preOrder) : new List<int>(),
            reorderPreMode = preMode,
            reorderFrom = from,
            reorderTarget = target,
            reorderLines = 1
        });

    public void PushReorder(bool isColumn, IReadOnlyList<int> preOrder, DataSource.SortMode preMode, int linesMoved) =>
        Push(new Edit
        {
            kind = EditKind.Sort,
            reorderIsColumn = isColumn,
            reorderPreOrder = preOrder != null ? new List<int>(preOrder) : new List<int>(),
            reorderPreMode = preMode,
            reorderFrom = -1,
            reorderTarget = -1,
            reorderLines = linesMoved
        });

    public void PushProjection(ProjectionRecord projection, EditKind kind) =>
        Push(new Edit { kind = kind, projection = projection });

    public void PushFilter(IReadOnlyList<int> preHidden, IReadOnlyList<int> postHidden, bool isRow) =>
        Push(new Edit
        {
            kind = EditKind.Filter,
            filterPreHidden = preHidden != null ? new List<int>(preHidden) : new List<int>(),
            filterPostHidden = postHidden != null ? new List<int>(postHidden) : new List<int>(),
            filterIsRow = isRow
        });

    public void DropKind(EditKind kind) => RemoveAll(e => e.kind == kind);

}
