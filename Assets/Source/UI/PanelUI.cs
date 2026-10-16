using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Oculus.Interaction;

public abstract class PanelUI : MonoBehaviour
{
    public float zOffsetFromCamera = 0.45f;

    [SerializeField] private Collider grabCollider;

    private Transform _grabPivot;

    protected Canvas _canvas;

    protected RectTransform CanvasRect => _canvas != null ? _canvas.transform as RectTransform : null;

    public bool IsVisible => _canvas != null && _canvas.gameObject.activeInHierarchy;

    public abstract void ShowPanel();
    public abstract void HidePanel();

    public void TogglePanel()
    {
        if (_canvas == null) return;
        if (IsVisible) HidePanel();
        else ShowPanel();
    }

    private bool _layoutDirty = true;

    protected void InvalidateLayout() => _layoutDirty = true;

    protected virtual void ResolveDeferredLayout() { }

    protected void ShowCanvas()
    {
        PlaceInFrontOfCamera();
        _canvas.gameObject.SetActive(true);

        if (_layoutDirty)
        {
            _layoutDirty = false;
            ResolveDeferredLayout();
        }

        SetGrabbable(true);
    }

    protected void HideCanvas()
    {
        _canvas.gameObject.SetActive(false);
        SetGrabbable(false);
    }

    private Prewarm.CanvasWarmState _prewarmState;

    public void PrewarmCanvas(bool on) => Prewarm.WarmCanvas(ref _prewarmState, _canvas, on);




    protected void InitPanel(Vector2 size)
    {
        _canvas = GetComponentInChildren<Canvas>(true);
        if (_canvas != null) _canvas.sortingOrder = Style.SortPanels;
        ApplyPanelSize(size);
        ApplyPanelSprites(transform);
        ApplyTitleBars(transform);
        ApplyDividers(transform);
        EnsureTwoGrabTransform();
        SetGrabbable(false);
    }

    private void EnsureTwoGrabTransform()
    {
        Grabbable grabbable = GetComponent<Grabbable>();
        if (grabbable == null) return;

        TwoGrabRotateTransformer spin = GrabTransformers.EnsureYaw(gameObject);

        spin.InjectOptionalPivotTransform(EnsureGrabPivot());
        spin.InjectOptionalRotationAxis(TwoGrabRotateTransformer.Axis.Up);

        grabbable.InjectOptionalTwoGrabTransformer(spin);
        spin.Initialize(grabbable);
    }

    private Transform EnsureGrabPivot()
    {
        if (_grabPivot != null) return _grabPivot;

        _grabPivot = new GameObject("GrabPivot").transform;
        _grabPivot.SetParent(transform, false);
        return _grabPivot;
    }

    public static void ApplyDividers(Transform root)
    {
        List<Transform> bars = FindTransform.FindAllDeep(root, "TitleBar");
        for (int i = 0; i < bars.Count; i++) AddDivider(bars[i]);

        List<Transform> headers = FindTransform.FindAllDeep(root, "Header");
        for (int i = 0; i < headers.Count; i++) AddDivider(headers[i]);
    }

    private static void AddDivider(Transform bar)
    {
        if (bar == null) return;

        RectTransform rt = bar.Find("Divider") as RectTransform;
        if (rt == null)
        {
            GameObject obj = new GameObject("Divider", typeof(RectTransform));
            rt = obj.GetComponent<RectTransform>();
            rt.SetParent(bar, false);

            LayoutElement le = obj.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            Image line = obj.AddComponent<Image>();
            line.color = Style.Black;
            line.raycastTarget = false;
            ApplyFront(line);
        }

        LayoutGroup parentLayout = bar.parent != null ? bar.parent.GetComponent<LayoutGroup>() : null;
        float padLeft = parentLayout != null ? parentLayout.padding.left : 0f;
        float padRight = parentLayout != null ? parentLayout.padding.right : 0f;

        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = new Vector2(-padLeft, 0f);
        rt.offsetMax = new Vector2(padRight, Style.SmallBorder);
    }

    private void SetGrabbable(bool on)
    {
        if (grabCollider != null) grabCollider.enabled = on;
    }

    private RectTransform[] _grabRects;
    private bool _grabBoundsDirty;

    protected void SetGrabRects(params RectTransform[] rects) => _grabRects = rects;

    protected void InvalidateGrabBounds() => _grabBoundsDirty = true;

    protected virtual void OnLateUpdate() { }

    private Grabbable _grabbable;
    private bool _grabbableSearched;
    private bool _grabbedLook;
    private Vector3 _restScale = Vector3.one;

    private void ApplyGrabLook()
    {
        if (!_grabbableSearched)
        {
            _grabbableSearched = true;
            _grabbable = GetComponent<Grabbable>();
        }
        if (_grabbable == null) return;

        bool held = _grabbable.SelectingPointsCount > 0;
        if (held == _grabbedLook) return;

        if (held)
        {
            _restScale = transform.localScale;
            transform.localScale = _restScale * Style.EngageScale;
        }
        else
        {
            transform.localScale = _restScale;
        }

        _grabbedLook = held;
    }

    private const float FlipSeconds = 0.35f;
    private Coroutine _flip;

    private bool Held => _grabbable != null && _grabbable.SelectingPointsCount > 0;

    private void ApplyFacing()
    {
        if (_flip != null || !IsVisible || Held) return;

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;
        if (!PanelPlacement.ShouldFlip(transform.position, transform.forward, cam.position)) return;

        _flip = StartCoroutine(FlipRoutine());
    }

    private IEnumerator FlipRoutine()
    {
        Quaternion from = transform.rotation;
        Quaternion to = Quaternion.AngleAxis(180f, Vector3.up) * from;

        float t = 0f;
        while (t < FlipSeconds)
        {
            yield return null;
            if (Held) break;

            t += Time.unscaledDeltaTime;
            transform.rotation = Quaternion.Slerp(from, to, Mathf.Clamp01(t / FlipSeconds));
        }

        if (!Held) transform.rotation = to;
        _flip = null;
        InvalidateGrabBounds();
    }

    private void LateUpdate()
    {
        OnLateUpdate();
        ApplyGrabLook();
        ApplyFacing();

        if (!_grabBoundsDirty || !IsVisible) return;

        if (_grabRects == null || !(grabCollider is BoxCollider))
        {
            _grabBoundsDirty = false;
            return;
        }

        if (RefreshGrabBounds(_grabRects)) _grabBoundsDirty = false;
    }

    protected bool RefreshGrabBounds(params RectTransform[] rects)
    {
        BoxCollider box = grabCollider as BoxCollider;
        if (box == null) return false;

        Transform space = box.transform;
        if (!UIMeasure.TryLocalBounds(CanvasRect, rects, space, out Vector3 center, out Vector3 size)) return false;

        center.z = box.center.z;
        size.z = box.size.z;
        box.center = center;
        box.size = size;

        if (_grabPivot != null) _grabPivot.position = space.TransformPoint(center);
        return true;
    }

    private void ApplyPanelSize(Vector2 size)
    {
        if (_canvas == null) return;
        RectTransform rt = _canvas.transform as RectTransform;
        if (rt != null) rt.sizeDelta = CanvasSize(size);

        RectTransform primaryRt = FindTransform.FindDeep(transform, "PrimaryPanel") as RectTransform;
        if (primaryRt != null)
            primaryRt.sizeDelta = new Vector2(primaryRt.sizeDelta.x, size.y);

        ApplyPanelBorders(transform);
    }

    public static void ApplyPanelSprites(Transform root)
    {
        ApplyTo(root, "PanelBorder", Style.PanelRadius);
        ApplyTo(root, "PanelRoot", Style.PanelInnerRadius);
        EnsurePanelBacks(root);
    }

    private static void EnsurePanelBacks(Transform root)
    {
        List<Transform> borders = FindTransform.FindAllDeep(root, "PanelBorder");
        for (int i = 0; i < borders.Count; i++) EnsurePanelBack(borders[i]);
    }

    private static void EnsurePanelBack(Transform border)
    {
        if (border == null) return;

        RectTransform rt = UILayout.EnsureChild(border, "PanelBack");

        UILayout.Fill(rt, Style.SmallBorder);

        Image back = UILayout.Ensure<Image>(rt.gameObject);

        RoundedSprite.Apply(back, Style.PanelInnerRadius);
        back.color = Style.Black;
        back.raycastTarget = false;

        ApplyBack(back);

        rt.SetAsLastSibling();
    }

    private static Material _frontMaterial;
    private static Material _backMaterial;

    private const string FrontMaterialPath = "Materials/PanelFront";
    private const string BackMaterialPath = "Materials/PanelBack";

    public static Material FrontMaterial => LoadMaterial(ref _frontMaterial, FrontMaterialPath);

    public static Material BackMaterial => LoadMaterial(ref _backMaterial, BackMaterialPath);

    private static Material LoadMaterial(ref Material cached, string path)
    {
        if (cached != null) return cached;

        cached = Resources.Load<Material>(path);
        if (cached == null)
            Debug.LogError($"[PanelUI] No material at Resources/{path}; those graphics keep the default UI material.");
        return cached;
    }

    public static void ApplyBack(Image image)
    {
        if (image == null) return;

        Material mat = BackMaterial;
        if (mat != null) image.material = mat;
    }

    public static void ApplyFront(Image image)
    {
        if (image == null) return;

        Material mat = FrontMaterial;
        if (mat != null) image.material = mat;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _frontMaterial = null;
        _backMaterial = null;
    }

    private static void ApplyTo(Transform root, string name, float radius)
    {
        List<Transform> found = FindTransform.FindAllDeep(root, name);
        for (int i = 0; i < found.Count; i++)
            RoundedSprite.Apply(found[i].GetComponent<Image>(), radius);
    }

    public static void ApplyPanelBorders(Transform root)
    {
        List<Transform> roots = FindTransform.FindAllDeep(root, "PanelRoot");
        for (int i = 0; i < roots.Count; i++)
        {
            if (!(roots[i] is RectTransform rt)) continue;
            UILayout.Fill(rt, Style.SmallBorder);
        }
    }

    public static float TitleBarHeight => Style.SmallPadding * 2f + Style.Button.y;

    private static void EnsureBarBottomSquare(Transform bar, Color tint)
    {
        RectTransform rt = UILayout.EnsureChild(bar, "TitleBarSquare");

        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = new Vector2(0f, Style.PanelInnerRadius);
        rt.SetAsFirstSibling();

        LayoutElement le = UILayout.Ensure<LayoutElement>(rt.gameObject);
        le.ignoreLayout = true;

        Image img = UILayout.Ensure<Image>(rt.gameObject);
        img.color = tint;
        img.raycastTarget = false;
        ApplyFront(img);
    }

    public static void ApplyTitleBars(Transform root)
    {
        float height = TitleBarHeight;
        List<Transform> bars = FindTransform.FindAllDeep(root, "TitleBar");

        for (int i = 0; i < bars.Count; i++)
        {
            Transform bar = bars[i];

            LayoutElement le = bar.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.minHeight = height;
                le.preferredHeight = height;
                le.flexibleHeight = 0f;
            }

            Image barFill = bar.GetComponent<Image>();
            if (barFill != null)
            {
                Color tint = Style.Alpha(Style.Black, Style.SurfaceTint);
                barFill.color = tint;
                RoundedSprite.Apply(barFill, Style.PanelInnerRadius);
                EnsureBarBottomSquare(bar, tint);
            }

            HorizontalLayoutGroup hlg = bar.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                hlg.padding = Style.PanePadding();
                hlg.childForceExpandHeight = false;
            }

            bool hasSpacer = StretchSpacer(bar) != null;

            List<Transform> titles = FindTransform.FindAllDeep(bar, "Title");
            for (int t = 0; t < titles.Count; t++)
            {
                if (!StyleHeaderLabel(titles[t].GetComponent<TextMeshProUGUI>())) continue;

                LayoutElement titleLE = UILayout.Ensure<LayoutElement>(titles[t].gameObject);
                titleLE.preferredWidth = -1f;
                titleLE.flexibleWidth = hasSpacer ? 0f : 1f;
            }
        }

        List<Transform> headers = FindTransform.FindAllDeep(root, "Header");
        for (int i = 0; i < headers.Count; i++)
            StyleHeaderLabel(headers[i].GetComponent<TextMeshProUGUI>());
    }

    protected static void ApplyPaneLayout(HorizontalOrVerticalLayoutGroup layout)
    {
        layout.padding = Style.PanePadding();
        layout.spacing = Style.SmallPadding;
    }

    private static LayoutElement StretchSpacer(Transform bar)
    {
        Transform spacer = bar.Find("Spacer");
        if (spacer == null) return null;

        LayoutElement le = UILayout.Ensure<LayoutElement>(spacer.gameObject);
        le.flexibleWidth = 1f;
        return le;
    }

    private static bool StyleHeaderLabel(TextMeshProUGUI label)
    {
        if (label == null) return false;

        Style.ApplyTitle(label);
        label.alignment = TextAlignmentOptions.Left;
        label.color = Style.Black;
        return true;
    }

    protected virtual Vector2 CanvasSize(Vector2 panelSize) => panelSize;

    protected virtual float XOffsetFromCamera => 0f;

    protected void PlaceInFrontOfCamera()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null)
        {
            Debug.LogWarning($"[{GetType().Name}] No camera tagged MainCamera; '{name}' opens wherever the scene left it.");
            return;
        }

        Vector3 forward = CameraRig.Flatten(cam.forward, Vector3.forward);
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        transform.position = cam.position + forward * zOffsetFromCamera + right * XOffsetFromCamera;

        Vector3 face = CameraRig.Flatten(transform.position - cam.position, forward);
        transform.rotation = Quaternion.LookRotation(face);
    }

}
