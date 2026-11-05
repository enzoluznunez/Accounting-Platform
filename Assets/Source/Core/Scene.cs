using UnityEngine;

public static class Scene {

    private static ManageTools _toolManager;
    private static ToolPanelUI _toolPanelUI;
    private static DataSource _dataSource;
    private static HandButton _handButton;
    private static FilterTool _filterTool;
    private static ProfileTool _profileTool;
    private static SortTool _sortTool;
    private static ManageSheets _sheetManager;
    private static ReadSheets _sheetReader;
    private static ManageDatasets _manageDatasets;
    private static Tooltip _tooltip;

    public static ManageTools Tools => Resolve(ref _toolManager);
    public static ToolPanelUI ToolPanel => Resolve(ref _toolPanelUI);
    public static HandButton Assistant => Resolve(ref _handButton);
    public static FilterTool Filter => Resolve(ref _filterTool);
    public static ProfileTool Profile => Resolve(ref _profileTool);
    public static SortTool Sort => Resolve(ref _sortTool);
    public static ManageSheets Sheets => Resolve(ref _sheetManager);
    public static ReadSheets Reader => Resolve(ref _sheetReader);
    public static ManageDatasets Datasets => ManageDatasets.Instance != null ? ManageDatasets.Instance : Resolve(ref _manageDatasets);
    public static Tooltip Tooltip => Resolve(ref _tooltip);

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
            if (_dataSource == null) {
                var sheets = Sheets;
                _dataSource = sheets != null && sheets.dataSource != null
                    ? sheets.dataSource
                    : Object.FindAnyObjectByType<DataSource>();
            }
            return _dataSource;
        }
    }

    private static T Resolve<T>(ref T cached) where T : Object {
        if (cached == null) cached = Object.FindAnyObjectByType<T>();
        return cached;
    }
}
