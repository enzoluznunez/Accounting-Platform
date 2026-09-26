using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

public class ButtonList
{
    public enum Axis { Horizontal, Vertical }
    public enum Sizing { Measured, Equal }

    public class Options
    {
        public Axis axis = Axis.Horizontal;
        public Sizing sizing = Sizing.Measured;
        public RectOffset padding;
        public TextAnchor alignment = TextAnchor.UpperLeft;
        public bool expandCrossAxis = true;
        public float itemHeight;
        public float itemPadding = Style.ButtonTextPad;
        public bool newestFirst;
        public bool backed;
        public bool scrollable;

        // A checkable list is a column of rows rather than a row of buttons: the
        // label sits at the left and a square at the right carries the state, so
        // the row keeps its resting colours however it is set.
        public bool checkable;
        public float checkSide = Style.Body;
    }

    private const float EngageBleed = Style.SmallPadding;

    private readonly RectTransform _root;
    private readonly RectTransform _content;
    private readonly Options _options;
    private readonly List<UIButton.Handle> _items = new List<UIButton.Handle>();

    public float MaxItemExtent { get; private set; }

    public ButtonList(RectTransform root, Options options)
    {
        _root = root;
        _options = options ?? new Options();
        _content = _options.scrollable ? BuildScroll() : _root;

        BuildAxisLayout();
    }

    private RectTransform BuildScroll()
    {
        bool vertical = _options.axis == Axis.Vertical;

        RectMask2D mask = _root.gameObject.AddComponent<RectMask2D>();
        mask.padding = vertical
            ? new Vector4(-EngageBleed, 0f, -EngageBleed, 0f)
            : new Vector4(0f, -EngageBleed, 0f, -EngageBleed);

        Image catcher = _root.gameObject.AddComponent<Image>();
        catcher.color = Color.clear;
        catcher.raycastTarget = true;

        GameObject contentObj = new GameObject("Content", typeof(RectTransform));
        RectTransform content = contentObj.GetComponent<RectTransform>();
        content.SetParent(_root, false);

        content.anchorMin = vertical ? new Vector2(0f, 1f) : new Vector2(0f, 0f);
        content.anchorMax = vertical ? new Vector2(1f, 1f) : new Vector2(0f, 1f);
        content.pivot = vertical ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        ContentSizeFitter fitter = contentObj.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = vertical
            ? ContentSizeFitter.FitMode.Unconstrained
            : ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = vertical
            ? ContentSizeFitter.FitMode.PreferredSize
            : ContentSizeFitter.FitMode.Unconstrained;

        ScrollRect scroll = _root.gameObject.AddComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = _root;
        scroll.horizontal = !vertical;
        scroll.vertical = vertical;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = Style.ScrollSensitivity;
        scroll.horizontalScrollbar = null;
        scroll.verticalScrollbar = null;

        return content;
    }

    private void BuildAxisLayout()
    {
        HorizontalOrVerticalLayoutGroup lg = _options.axis == Axis.Vertical
            ? (HorizontalOrVerticalLayoutGroup)_content.gameObject.AddComponent<VerticalLayoutGroup>()
            : _content.gameObject.AddComponent<HorizontalLayoutGroup>();

        lg.spacing = Style.SmallPadding;
        if (_options.padding != null) lg.padding = _options.padding;
        lg.childAlignment = _options.alignment;
        lg.childControlWidth = true;
        lg.childControlHeight = true;

        bool expandAlong = _options.sizing == Sizing.Equal;
        if (_options.axis == Axis.Horizontal)
        {
            lg.childForceExpandWidth = expandAlong;
            lg.childForceExpandHeight = _options.expandCrossAxis;
        }
        else
        {
            lg.childForceExpandHeight = expandAlong;
            lg.childForceExpandWidth = _options.expandCrossAxis;
        }
    }

    public UIButton.Handle Add(string name, string label, UnityAction onClick)
    {
        // The square needs room of its own at the right end, and the label needs
        // to stop before it rather than run underneath.
        float padRight = _options.checkable
            ? _options.checkSide + _options.itemPadding * 2f
            : _options.itemPadding;

        UIButton.Handle h = UIButton.Create(_content, name, label,
            flexibleWidth: _options.sizing == Sizing.Equal || _options.checkable,
            height: _options.itemHeight,
            alignment: _options.checkable ? TextAlignmentOptions.Left : TextAlignmentOptions.Center,
            padLeft: _options.itemPadding, padRight: padRight);

        if (_options.backed) UIButton.AddBack(h);
        if (_options.checkable)
        {
            UIButton.AddCheck(h, _options.checkSide);
            UIButton.SetUntinted(h);
            Stretch(h);
        }
        else if (_options.sizing == Sizing.Measured) Measure(h);
        if (onClick != null) h.Button.onClick.AddListener(onClick);
        if (_options.newestFirst) h.Root.transform.SetAsFirstSibling();

        _items.Add(h);
        return h;
    }

    public UIButton.Handle At(int index) =>
        index >= 0 && index < _items.Count ? _items[index] : null;

    private void Measure(UIButton.Handle h)
    {
        LayoutElement le = h.Root.GetComponent<LayoutElement>();
        if (le == null) return;

        UIMeasure.TryTextWidth(h.Text, out float pref);
        float extent = (pref > 0f ? pref : 0f) + _options.itemPadding * 2f;

        le.preferredWidth = extent;
        le.flexibleWidth = 0f;
        if (extent > MaxItemExtent) MaxItemExtent = extent;
    }

    public void Clear()
    {
        UILayout.Clear(_content);
        _items.Clear();
        MaxItemExtent = 0f;
    }

    // A check row spans the list whatever its label measures to, so the squares
    // line up down the right edge instead of stepping in with the text.
    private static void Stretch(UIButton.Handle h)
    {
        LayoutElement le = h.Root.GetComponent<LayoutElement>();
        if (le == null) return;
        le.preferredWidth = -1f;
        le.minWidth = -1f;
        le.flexibleWidth = 1f;
    }

    public void SetSelected(int activeIndex)
    {
        for (int i = 0; i < _items.Count; i++)
            UIButton.SetSelected(_items[i], i == activeIndex);
    }
}
