using System.Collections.Generic;

public static class SheetStats
{
    public struct Summary
    {
        public bool Valid;
        public int Count;
        public double Min;
        public double Max;
        public int MinVisRow;
        public int MinVisCol;
        public int MaxVisRow;
        public int MaxVisCol;
        public double Mean;
        public double Sum;
    }

    public static Summary Over(DataSource data, int rowMin, int rowMax, int colMin, int colMax)
    {
        Summary summary = new Summary();
        if (data == null) return summary;

        IReadOnlyList<int> rowOrder = data.RowOrder;
        IReadOnlyList<int> colOrder = data.ColumnOrder;
        if (rowOrder == null || colOrder == null) return summary;

        double sum = 0.0;

        for (int visRow = rowMin; visRow <= rowMax; visRow++)
        {
            if (visRow < 0 || visRow >= rowOrder.Count) continue;
            int dataRow = rowOrder[visRow];

            for (int visCol = colMin; visCol <= colMax; visCol++)
            {
                if (visCol < 0 || visCol >= colOrder.Count) continue;
                int dataCol = colOrder[visCol];
                if (!data.HasValue(dataRow, dataCol)) continue;

                double value = data.GetValue(dataRow, dataCol);
                if (summary.Count == 0 || value < summary.Min)
                {
                    summary.Min = value;
                    summary.MinVisRow = visRow;
                    summary.MinVisCol = visCol;
                }
                if (summary.Count == 0 || value > summary.Max)
                {
                    summary.Max = value;
                    summary.MaxVisRow = visRow;
                    summary.MaxVisCol = visCol;
                }

                sum += value;
                summary.Count++;
            }
        }

        if (summary.Count == 0) return summary;

        summary.Sum = sum;
        summary.Mean = sum / summary.Count;
        summary.Valid = true;
        return summary;
    }
}
