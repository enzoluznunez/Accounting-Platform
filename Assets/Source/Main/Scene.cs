using UnityEngine;

public static class Scene {

    private static ManageTools toolManager;
    private static ToolPanelUI toolPanelUI;
    private static IDataPanel dataPanel;
    private static DataSource dataSource;
    private static Watch watch;
    private static FilterTool filterTool;
    private static SliceTool sliceTool;
    private static ColorTool colorTool;
    private static MoveTool moveTool;
    private static RotateTool rotateTool;
    private static ScaleTool scaleTool;
    private static ProfileTool profileTool;
    private static SortTool sortTool;
    private static ManageSheets sheetManager;
    private static ReadSheets sheetReader;
    private static ManageDatasets manageDatasets;
    private static Tooltip tooltip;

    public static ManageTools Tools => Resolve(ref toolManager);
    public static ToolPanelUI ToolPanel => Resolve(ref toolPanelUI);
    public static IDataPanel DataPanel {
        get {
            if (dataPanel == null || (dataPanel is Object o && o == null)) {
                dataPanel = null;
                var panels = Object.FindObjectsByType<PanelUI>(FindObjectsSortMode.None);
                for (int i = 0; i < panels.Length; i++)
                    if (panels[i] is IDataPanel dp) { dataPanel = dp; break; }
            }
            return dataPanel;
        }
    }
    public static Watch Assistant => Resolve(ref watch);
    public static FilterTool Filter => Resolve(ref filterTool);
    public static SliceTool Slice => Resolve(ref sliceTool);
    public static ColorTool Color => Resolve(ref colorTool);
    public static MoveTool Move => Resolve(ref moveTool);
    public static RotateTool Rotate => Resolve(ref rotateTool);
    public static ScaleTool Scale => Resolve(ref scaleTool);
    public static ProfileTool Profile => Resolve(ref profileTool);
    public static SortTool Sort => Resolve(ref sortTool);
    public static ManageSheets Sheets => Resolve(ref sheetManager);
    public static ReadSheets Reader => Resolve(ref sheetReader);
    public static ManageDatasets Datasets => ManageDatasets.Instance != null ? ManageDatasets.Instance : Resolve(ref manageDatasets);
    public static Tooltip Tooltip => Resolve(ref tooltip);

    public static string DatasetLabel {
        get {
            var d = Datasets;
            if (d == null || d.ActiveIndex < 0 || d.ActiveIndex >= d.DatasetCount) return "dataset";
            string label = d.Datasets[d.ActiveIndex].label;
            return string.IsNullOrEmpty(label) ? "dataset" : label;
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
