using System.Collections.Generic;

public static class SheetStats
{
    public struct Summary
    {
        public bool valid;
        public int count;
        public double min;
        public double max;
        public int minVisRow;
        public int minVisCol;
        public int maxVisRow;
        public int maxVisCol;
        public double mean;
        public double sum;
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
                if (summary.count == 0 || value < summary.min)
                {
                    summary.min = value;
                    summary.minVisRow = visRow;
                    summary.minVisCol = visCol;
                }
                if (summary.count == 0 || value > summary.max)
                {
                    summary.max = value;
                    summary.maxVisRow = visRow;
                    summary.maxVisCol = visCol;
                }

                sum += value;
                summary.count++;
            }
        }

        if (summary.count == 0) return summary;

        summary.sum = sum;
        summary.mean = sum / summary.count;
        summary.valid = true;
        return summary;
    }
}
