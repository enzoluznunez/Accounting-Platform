using UnityEngine;

public static class Scene {

    private static ManageTools toolManager;
    private static ToolPanelUI toolPanelUI;
    private static DataSource dataSource;
    private static Watch watch;
    private static FilterTool filterTool;
    private static ProfileTool profileTool;
    private static SortTool sortTool;
    private static ManageSheets sheetManager;
    private static ReadSheets sheetReader;
    private static ManageDatasets manageDatasets;
    private static Tooltip tooltip;

    public static ManageTools Tools => Resolve(ref toolManager);
    public static ToolPanelUI ToolPanel => Resolve(ref toolPanelUI);
    public static Watch Assistant => Resolve(ref watch);
    public static FilterTool Filter => Resolve(ref filterTool);
    public static ProfileTool Profile => Resolve(ref profileTool);
    public static SortTool Sort => Resolve(ref sortTool);
    public static ManageSheets Sheets => Resolve(ref sheetManager);
    public static ReadSheets Reader => Resolve(ref sheetReader);
    public static ManageDatasets Datasets => ManageDatasets.Instance != null ? ManageDatasets.Instance : Resolve(ref manageDatasets);
    public static Tooltip Tooltip => Resolve(ref tooltip);

    public static string DatasetLabel {
        get {
            ManageDatasets.Dataset active = Datasets != null ? Datasets.ActiveDataset : null;
            return active != null ? active.DisplayLabel : "dataset";
        }
    }

    public static DataSource Data {
        get {
            var active = ManageDatasets.ActiveSource;
            if (active != null) return active;
            if (dataSource == null) {
                var sheets = Sheets;
                dataSource = sheets != null && sheets.dataSource != null
                    ? sheets.dataSource
                    : Object.FindAnyObjectByType<DataSource>();
            }
            return dataSource;
        }
    }

    private static T Resolve<T>(ref T cached) where T : Object {
        if (cached == null) cached = Object.FindAnyObjectByType<T>();
        return cached;
    }
}
