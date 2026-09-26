using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class UIButton
{
    public class Handle
    {
        public GameObject Root;
        public Button Button;
        public Image Frame;
        public Image Inner;
        public Image Back;
        public Image CheckFrame;
        public Image CheckFill;
        public TextMeshProUGUI Text;
        public PointerHighlight Highlight;
        public float Radius;

        public Color RestFill;
        public Color SelectFill;
        public Color RestText;
        public Color SelectText;
    }

    public static Handle Create(Transform parent, string name, string label,
        float width = 0f, bool flexibleWidth = false, float height = 0f,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center,
        float padLeft = Style.ButtonTextPad, float padRight = Style.ButtonTextPad)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);
        root.AddComponent<RectTransform>();

        LayoutElement le = root.AddComponent<LayoutElement>();
        le.minHeight = height > 0f ? height : Style.Button.y;
        le.preferredHeight = le.minHeight;
        if (width > 0f) le.preferredWidth = width;
        le.flexibleWidth = flexibleWidth ? 1f : 0f;

        Handle h = new Handle { Root = root };
        Build(h, label, alignment, padLeft, padRight, Style.RadiusFor(le.minHeight));
        return h;
    }

    public static Handle Adopt(GameObject root,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center,
        float padLeft = Style.ButtonTextPad, float padRight = Style.ButtonTextPad,
        float radius = Style.OutRadius)
    {
        Handle h = new Handle { Root = root };
        Build(h, null, alignment, padLeft, padRight, radius);
        return h;
    }

    public static Image AddBack(Handle h)
    {
        if (h == null || h.Root == null) return null;
        if (h.Back != null) return h.Back;

        RectTransform rt = EnsureChild(h.Root.transform, "ButtonBack");
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image back = Ensure<Image>(rt.gameObject);
        RoundedSprite.Apply(back, h.Radius);
        back.color = Style.Black;
        back.raycastTarget = false;
        Material backMat = PanelUI.BackMaterial;
        if (backMat != null) back.material = backMat;

        rt.SetAsLastSibling();

        h.Back = back;
        return back;
    }

    // Re-rounds a button that has been resized since it was built: the frame, the
    // inner fill and the back all carry the same corner, so they are re-cut
    // together or the borders stop tracking one another.
    public static void SetRadius(Handle h, float radius)
    {
        if (h == null || h.Root == null || Mathf.Approximately(h.Radius, radius)) return;
        h.Radius = radius;

        if (h.Frame != null) RoundedSprite.Apply(h.Frame, radius);
        if (h.Inner != null) RoundedSprite.Apply(h.Inner, Style.InnerRadius(radius));

        if (h.Back == null) return;
        RoundedSprite.Apply(h.Back, radius);
        Material backMat = PanelUI.BackMaterial;
        if (backMat != null) h.Back.material = backMat;
    }

    // A square at the right end of a row, drawn the way every other surface here
    // is: a black frame with a fill inside it. Unchecked leaves the fill the
    // colour of the row, so the square reads as an outline; checked fills it
    // black. The square is the only thing that carries the state, so a checked
    // row keeps its ordinary background.
    public static Image AddCheck(Handle h, float side)
    {
        if (h == null || h.Root == null) return null;
        if (h.CheckFrame != null) return h.CheckFrame;

        RectTransform rt = EnsureChild(h.Root.transform, "Check");
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(side, side);
        rt.anchoredPosition = new Vector2(-Style.ButtonTextPad, 0f);

        Image frame = Ensure<Image>(rt.gameObject);
        RoundedSprite.Apply(frame, 0f);
        frame.color = Style.Black;
        frame.raycastTarget = false;
        h.CheckFrame = frame;

        RectTransform fillRt = EnsureChild(rt, "Fill");
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = new Vector2(Style.SmallBorder, Style.SmallBorder);
        fillRt.offsetMax = new Vector2(-Style.SmallBorder, -Style.SmallBorder);
        Image fill = Ensure<Image>(fillRt.gameObject);
        RoundedSprite.Apply(fill, 0f);
        fill.raycastTarget = false;
        h.CheckFill = fill;

        SetChecked(h, false);
        return frame;
    }

    public static void SetChecked(Handle h, bool on)
    {
        if (h == null || h.CheckFill == null) return;
        h.CheckFill.color = on ? Style.Black : Style.White;
    }

    // Holds a button at its resting colours, so pressing it swells the button
    // without inverting it. A checked row would otherwise flip to black under the
    // finger and take the square's meaning with it: an unchecked square on a
    // black row reads as a filled one.
    public static void SetUntinted(Handle h)
    {
        if (h == null || h.Inner == null) return;
        SetStateTarget(h, h.Inner, Style.White, Style.White, Style.Black, Style.Black);
    }

    public static void SetSelected(Handle h, bool selected)
    {
        if (h == null) return;

        Color fill = selected ? h.SelectFill : h.RestFill;
        Color press = selected ? h.RestFill : h.SelectFill;
        Color textColor = selected ? h.SelectText : h.RestText;
        Color pressText = selected ? h.RestText : h.SelectText;

        if (h.Highlight != null) h.Highlight.SetState(fill, press, textColor, pressText);
        else if (h.Inner != null) h.Inner.color = fill;

        if (h.Text != null) h.Text.color = textColor;
    }

    private static void SetStateTarget(Handle h, Graphic target,
        Color rest, Color select, Color restText, Color selectText)
    {
        h.RestFill = rest;
        h.SelectFill = select;
        h.RestText = restText;
        h.SelectText = selectText;
        if (h.Highlight == null) return;

        h.Highlight.target = target;
        if (h.Text != null) h.Highlight.SetTextTarget(h.Text);
        h.Highlight.SetState(rest, select, restText, selectText);
        target.color = rest;
    }

    private static void Build(Handle h, string label,
        TextAlignmentOptions alignment, float padLeft, float padRight, float radius)
    {
        GameObject root = h.Root;
        h.Radius = radius;

        Image frame = Ensure<Image>(root);
        RoundedSprite.Apply(frame, radius);
        frame.color = Style.Black;
        h.Frame = frame;

        Button button = Ensure<Button>(root);
        button.transition = Selectable.Transition.None;
        button.targetGraphic = frame;
        h.Button = button;

        RectTransform innerRt = EnsureChild(root.transform, "Inner");
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(Style.SmallBorder, Style.SmallBorder);
        innerRt.offsetMax = new Vector2(-Style.SmallBorder, -Style.SmallBorder);
        Image inner = Ensure<Image>(innerRt.gameObject);
        RoundedSprite.Apply(inner, Style.InnerRadius(radius));
        inner.raycastTarget = false;
        h.Inner = inner;

        RectTransform textRt = EnsureChild(root.transform, "Text");
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(padLeft, 0f);
        textRt.offsetMax = new Vector2(-padRight, 0f);
        TextMeshProUGUI text = Ensure<TextMeshProUGUI>(textRt.gameObject);
        if (label != null) text.text = label;
        Style.ApplyBody(text);
        text.alignment = alignment;
        text.color = Style.Black;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        h.Text = text;

        PointerHighlight hl = Ensure<PointerHighlight>(root);
        hl.SetLiftTarget(root.transform);
        h.Highlight = hl;

        SetStateTarget(h, inner, Style.White, Style.Black, Style.Black, Style.White);

        if (root.transform.Find("ButtonBack") != null) AddBack(h);
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static RectTransform EnsureChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing as RectTransform ?? existing.gameObject.AddComponent<RectTransform>();
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child.AddComponent<RectTransform>();
    }
}
