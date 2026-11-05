using System.Collections.Generic;
using UnityEngine;

public enum EditKind { Move, Rotate, Scale, Sort, Filter, Profile }

public struct MoveRecord
{
    public int SheetId;
    public Vector3 PrePos;
    public Quaternion PreRot;
    public Vector3 PreScale;
    public Vector3 PostPos;
    public Quaternion PostRot;
    public Vector3 PostScale;
    public float Distance;
}

// Always a strip: one whole row or column raised above the sheet.
public struct ProjectionRecord
{
    public bool IsColumn;
    public int DataRow;
    public int DataCol;
    public float Lift;
}

public class Edit
{
    public EditKind Kind;
    public MoveRecord Move;
    public ProjectionRecord Projection;

    public bool ReorderIsColumn;
    public List<int> ReorderPreOrder;
    public int ReorderFrom;
    public int ReorderTarget;
    public int ReorderLines;

    public int Group;

    // Which column groups were hidden before this filter, and after it. Undo puts
    // the first back; a replay onto a freshly loaded dataset applies the second.
    public List<int> FilterPreHidden;
    public List<int> FilterPostHidden;

    // Which axis the hidden set belongs to. One edit only ever covers one axis,
    // so a press on the company list and a press on the metric list undo apart.
    public bool FilterIsRow;

    public static string KindName(EditKind kind) => kind.ToString().ToLowerInvariant();
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
        if (top.Group == 0) return 1;

        int n = 0;
        for (int i = Count - 1; i >= 0 && this[i].Group == top.Group; i--) n++;
        return n;
    }

    public int UndoStepCount()
    {
        int steps = 0;
        int i = Count - 1;
        while (i >= 0)
        {
            int g = this[i].Group;
            steps++;
            if (g == 0) { i--; continue; }
            while (i >= 0 && this[i].Group == g) i--;
        }
        return steps;
    }

    private void Push(Edit e)
    {
        e.Group = _group;
        Add(e);
    }

    public void DropAt(int index)
    {
        if (index < 0 || index >= Count) return;
        RemoveAt(index);
    }

    public int StampGroup(int fromIndex)
    {
        if (fromIndex < 0) fromIndex = 0;
        if (fromIndex >= Count) return 0;

        int id = ++_groupSeq;
        for (int i = fromIndex; i < Count; i++) this[i].Group = id;
        return Count - fromIndex;
    }

    public Edit Pop()
    {
        if (Count == 0) return null;
        Edit e = this[Count - 1];
        RemoveAt(Count - 1);
        return e;
    }

    public void PushMove(MoveRecord move, EditKind kind) =>
        Push(new Edit { Kind = kind, Move = move });

    public void PushSort(bool isColumn, IReadOnlyList<int> preOrder, int from, int target) =>
        PushOrder(isColumn, preOrder, from, target, 1);

    public void PushReorder(bool isColumn, IReadOnlyList<int> preOrder, int linesMoved) =>
        PushOrder(isColumn, preOrder, -1, -1, linesMoved);

    private void PushOrder(bool isColumn, IReadOnlyList<int> preOrder, int from, int target, int lines) =>
        Push(new Edit
        {
            Kind = EditKind.Sort,
            ReorderIsColumn = isColumn,
            ReorderPreOrder = Copy(preOrder),
            ReorderFrom = from,
            ReorderTarget = target,
            ReorderLines = lines
        });

    public void PushProjection(ProjectionRecord projection, EditKind kind) =>
        Push(new Edit { Kind = kind, Projection = projection });

    public void PushFilter(IReadOnlyList<int> preHidden, IReadOnlyList<int> postHidden, bool isRow) =>
        Push(new Edit
        {
            Kind = EditKind.Filter,
            FilterPreHidden = Copy(preHidden),
            FilterPostHidden = Copy(postHidden),
            FilterIsRow = isRow
        });

    public void DropKind(EditKind kind) => RemoveAll(e => e.Kind == kind);

    private static List<int> Copy(IReadOnlyList<int> list) =>
        list != null ? new List<int>(list) : new List<int>();
}
