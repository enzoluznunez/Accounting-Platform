public static class StatsTooltip
{
    public static void Hide()
    {
        Tooltip tooltip = Scene.Tooltip;
        if (tooltip != null) tooltip.HideStats();
    }

    public static bool TryResolve(ManageSheets sheetManager, ReadSheets.Reading reading,
        out Tooltip tooltip, out DataSource data, out SheetPiece piece)
    {
        tooltip = Scene.Tooltip;
        data = Scene.Data;
        piece = null;
        if (tooltip == null || data == null || sheetManager == null) return false;

        piece = reading.Sheet != null
            ? reading.Sheet
            : sheetManager.SheetAt(reading.VisRow, reading.VisCol);
        return piece != null;
    }
}
