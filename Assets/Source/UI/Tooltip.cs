using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class Tooltip : MonoBehaviour
{
    private Coroutine _settle;

    private const float UpOffsetFromAnchor = 0.05f;
    private const float NoticeDistanceFromCamera = 0.5f;

    private const float TitleHeight = Style.Title + Style.SmallPadding;
    private const float ContentTop = Style.SmallPadding + TitleHeight + Style.SmallPadding;
    private const float ContentBottom = Style.SmallPadding;
    private const float Borders = Style.SmallBorder * 2f;
    private const string Dash = "-";

    private static readonly string[] StatNames =
        { "Average", "Count", "Minimum", "Maximum", "Sum" };

    public struct SelectionStats
    {
        public string title;
        public SheetStats.Summary stats;
    }

    private Canvas _canvas;
    private RectTransform _canvasRect;

    private GameObject _hintGroup;
    private TextMeshProUGUI _titleLabel;
    private TextMeshProUGUI _bodyLabel;

    private GameObject _statsGroup;
    private RectTransform _statsRect;
    private TextMeshProUGUI _statsTitle;
    private readonly TextMeshProUGUI[] _statCells = new TextMeshProUGUI[StatNames.Length];

    private SelectionStats _pendingStats;
    private Func<Vector3> _pendingAnchor;

    private void Awake()
    {
        _canvas = GetComponentInChildren<Canvas>(true);
        _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;

        if (_canvas != null) _canvas.sortingOrder = Style.SortNotices;

        if (_canvasRect != null)
        {
            _canvasRect.sizeDelta = new Vector2(Style.TooltipWidth, _canvasRect.sizeDelta.y);
            _canvasRect.pivot = new Vector2(0.5f, 0f);
            _canvasRect.localPosition = Vector3.zero;
        }

        _hintGroup = FindChild("HintGroup");
        _titleLabel = FindComponent<TextMeshProUGUI>("HintTitle");
        _bodyLabel = FindComponent<TextMeshProUGUI>("HintBody");

        PanelUI.ApplyPanelBorders(transform);
        PanelUI.ApplyPanelSprites(transform);

        NormalizeTitleHeight(RectOf(_titleLabel));
        EnableWrap(_titleLabel);
        EnableWrap(_bodyLabel);

        ApplyFonts();

        if (_canvas != null) _canvas.gameObject.SetActive(false);
    }

    private void ApplyFonts()
    {
        Style.ApplyBody(_bodyLabel);
        Style.ApplyTitle(_titleLabel);
    }

    private Prewarm.CanvasWarmState _prewarmState;

    public void PrewarmCanvas(bool on) => Prewarm.WarmCanvas(ref _prewarmState, _canvas, on);

    private static void EnableWrap(TextMeshProUGUI label)
    {
        if (label == null) return;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
    }

    public void ShowNotice(string title, string body)
    {
        StopSettle();

        if (_statsGroup != null) _statsGroup.SetActive(false);
        if (_hintGroup != null) _hintGroup.SetActive(true);

        if (_titleLabel != null) _titleLabel.text = title;
        if (_bodyLabel != null)
        {
            _bodyLabel.text = body;
            RectTransform bodyRT = _bodyLabel.rectTransform;
            bodyRT.anchoredPosition = new Vector2(bodyRT.anchoredPosition.x, -ContentTop);
        }

        Present(NoticeAnchor());
        SetPanelHeight(HeightFor(body));

        if (!isActiveAndEnabled) return;
        _settle = StartCoroutine(SettleHeight(_hintGroup, () => HeightFor(body)));
    }

    // Re-fits the panel over the next frames while the shown group's layout settles.
    private IEnumerator SettleHeight(GameObject group, Func<float> height)
    {
        yield return UILayout.Converge(
            () => group != null && group.activeInHierarchy,
            () => SetPanelHeight(height()),
            () => _canvasRect != null ? _canvasRect.sizeDelta.y : -1f);

        _settle = null;
    }

    private void StopSettle()
    {
        if (_settle != null) StopCoroutine(_settle);
        _settle = null;
    }

    private void OnDisable() => _settle = null;

    private Vector3 NoticeAnchor()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return transform.position;

        return cam.position + CameraRig.FlatForward * NoticeDistanceFromCamera;
    }

    public void ShowStats(Func<Vector3> anchor, SelectionStats selection)
    {
        if (anchor == null) return;

        StopSettle();
        EnsureStatsGroup();
        if (_statsGroup == null) return;

        if (_hintGroup != null) _hintGroup.SetActive(false);
        _statsGroup.SetActive(true);

        if (_statsTitle != null)
            _statsTitle.text = string.IsNullOrEmpty(selection.title) ? Dash : selection.title;

        FillStats(_statCells, selection.stats);

        _pendingStats = selection;
        _pendingAnchor = anchor;

        Present(anchor());
        SetPanelHeight(StatsHeight());

        if (!isActiveAndEnabled) return;
        _settle = StartCoroutine(SettleHeight(_statsGroup, StatsHeight));
    }

    public void HideStats()
    {
        _pendingAnchor = null;
        if (_statsGroup == null || !_statsGroup.activeSelf) return;
        Hide();
    }

    public void DismissNotice()
    {
        if (_pendingAnchor != null) ShowStats(_pendingAnchor, _pendingStats);
        else Hide();
    }

    private static void FillStats(TextMeshProUGUI[] cells, SheetStats.Summary summary)
    {
        if (cells == null) return;

        bool empty = !summary.valid;
        SetCell(cells, 0, empty ? Dash : Formatter.Compact(summary.mean));
        SetCell(cells, 1, empty ? Dash : Formatter.Count(summary.count));
        SetCell(cells, 2, empty ? Dash : Formatter.Compact(summary.min));
        SetCell(cells, 3, empty ? Dash : Formatter.Compact(summary.max));
        SetCell(cells, 4, empty ? Dash : Formatter.Compact(summary.sum));
    }

    private static void SetCell(TextMeshProUGUI[] cells, int index, string text)
    {
        if (index < cells.Length && cells[index] != null) cells[index].text = text;
    }

    private float StatsHeight()
    {
        if (_statsRect == null) return Borders + ContentTop + ContentBottom;

        if (!UIMeasure.TryPreferredHeight(_canvasRect, _statsRect, out float content))
            content = _statsRect.rect.height;

        return Borders + Style.SmallPadding + content + ContentBottom;
    }

    private void EnsureStatsGroup()
    {
        if (_statsGroup != null) return;

        Transform parent = _hintGroup != null ? _hintGroup.transform.parent : _canvasRect;
        if (parent == null) return;

        _statsGroup = new GameObject("StatsGroup", typeof(RectTransform));
        _statsGroup.transform.SetParent(parent, false);

        _statsRect = _statsGroup.GetComponent<RectTransform>();
        _statsRect.anchorMin = new Vector2(0f, 1f);
        _statsRect.anchorMax = new Vector2(1f, 1f);
        _statsRect.pivot = new Vector2(0.5f, 1f);
        _statsRect.sizeDelta = new Vector2(-Style.MediumPadding * 2f, _statsRect.sizeDelta.y);
        _statsRect.anchoredPosition = new Vector2(0f, -Style.SmallPadding);

        VerticalLayoutGroup layout = _statsGroup.AddComponent<VerticalLayoutGroup>();
        layout.spacing = Style.SmallPadding;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = _statsGroup.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _statsTitle = AddLine(_statsGroup.transform, "Title", TextAlignmentOptions.Center, true);

        for (int i = 0; i < StatNames.Length; i++)
            _statCells[i] = AddRow(_statsGroup.transform, StatNames[i], StatNames[i]);

        _statsGroup.SetActive(false);
    }

    private static TextMeshProUGUI AddRow(Transform parent, string name, string labelText)
    {
        GameObject row = new GameObject(name, typeof(RectTransform));
        row.transform.SetParent(parent, false);

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = Style.SmallPadding;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI label = AddLine(row.transform, "Label", TextAlignmentOptions.Left, true);
        label.text = labelText;
        LayoutElement stretch = label.gameObject.AddComponent<LayoutElement>();
        stretch.flexibleWidth = 1f;

        return AddCell(row.transform, "Value", false);
    }

    private static TextMeshProUGUI AddCell(Transform parent, string name, bool bold)
    {
        TextMeshProUGUI label = AddLine(parent, name, TextAlignmentOptions.Right, bold);
        LayoutElement width = label.gameObject.AddComponent<LayoutElement>();
        width.preferredWidth = Style.ValueColumn;
        width.flexibleWidth = 0f;
        return label;
    }

    private static TextMeshProUGUI AddLine(Transform parent, string name, TextAlignmentOptions alignment, bool bold = false) =>
        UILabel.Make(parent, name, Style.Black, alignment, TextOverflowModes.Ellipsis, bold: bold);

    private float HeightFor(string body)
    {
        if (_bodyLabel == null) return Borders + ContentTop + ContentBottom;

        if (_canvasRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_canvasRect);
        _bodyLabel.ForceMeshUpdate();
        float width = _bodyLabel.rectTransform.rect.width;
        return Borders + ContentTop + _bodyLabel.GetPreferredValues(body, width, 0f).y + ContentBottom;
    }

    private void SetPanelHeight(float height)
    {
        if (_canvasRect != null)
            _canvasRect.sizeDelta = new Vector2(_canvasRect.sizeDelta.x, height);
    }

    public bool IsVisible => _canvas != null && _canvas.gameObject.activeInHierarchy;

    public void Hide()
    {
        if (_canvas != null && _canvas.gameObject.activeSelf)
            _canvas.gameObject.SetActive(false);
    }

    private static void NormalizeTitleHeight(RectTransform rt)
    {
        if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, TitleHeight);
    }

    public void UpdatePosition(Vector3 anchorPoint)
    {
        if (CameraRig.MainTransform == null) return;

        transform.position = anchorPoint + Vector3.up * UpOffsetFromAnchor;
        FaceViewer();
    }

    private void FaceViewer()
    {
        if (CameraRig.TryFaceViewer(transform.position, out Quaternion rotation))
            transform.rotation = rotation;
    }

    private void LateUpdate()
    {
        if (IsVisible) FaceViewer();
    }

    private void Present(Vector3 worldHitPoint)
    {
        UpdatePosition(worldHitPoint);
        if (_canvas != null && !_canvas.gameObject.activeSelf)
            _canvas.gameObject.SetActive(true);
    }

    private T FindComponent<T>(string name) where T : Component
    {
        Transform t = FindTransform.FindDeep(transform, name);
        return t != null ? t.GetComponent<T>() : null;
    }

    private GameObject FindChild(string name)
    {
        Transform t = FindTransform.FindDeep(transform, name);
        return t != null ? t.gameObject : null;
    }

    private static RectTransform RectOf(Component c) => c != null ? c.transform as RectTransform : null;
}
