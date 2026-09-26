using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Prewarm : MonoBehaviour
{
    public struct CanvasWarmState
    {
        internal CanvasGroup group;
        internal bool added;
        internal float alpha;
        internal bool interactable;
        internal bool blocksRaycasts;
        internal bool active;
    }

    public static void WarmCanvas(ref CanvasWarmState state, Canvas canvas, bool on)
    {
        if (canvas == null) return;

        if (on)
        {
            if (state.active || canvas.gameObject.activeSelf) return;

            state.group = canvas.GetComponent<CanvasGroup>();
            state.added = state.group == null;
            if (state.added) state.group = canvas.gameObject.AddComponent<CanvasGroup>();

            state.alpha = state.group.alpha;
            state.interactable = state.group.interactable;
            state.blocksRaycasts = state.group.blocksRaycasts;

            state.group.alpha = 0f;
            state.group.interactable = false;
            state.group.blocksRaycasts = false;

            canvas.gameObject.SetActive(true);
            state.active = true;
            return;
        }

        if (!state.active) return;
        state.active = false;
        canvas.gameObject.SetActive(false);

        if (state.group != null)
        {
            if (state.added) Destroy(state.group);
            else
            {
                state.group.alpha = state.alpha;
                state.group.interactable = state.interactable;
                state.group.blocksRaycasts = state.blocksRaycasts;
            }
        }
        state.group = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("Prewarm");
        DontDestroyOnLoad(go);
        go.AddComponent<Prewarm>();
    }

    private IEnumerator Start()
    {
        yield return null;

        GameObject rig = BuildWarmRig();

        ToolPanelUI tool = Scene.ToolPanel;
        Tooltip tooltip = FindAnyObjectByType<Tooltip>(FindObjectsInactive.Include);

        if (tool != null)
        {
            tool.PrewarmCanvas(true);
            tool.PrewarmContent(true);
        }
        if (tooltip != null) tooltip.PrewarmCanvas(true);

        yield return null;

        if (tool != null)
        {
            tool.PrewarmContent(false);
            tool.PrewarmCanvas(false);
        }
        if (tooltip != null) tooltip.PrewarmCanvas(false);
        if (rig != null) Destroy(rig);

        Destroy(gameObject);
    }

    private static GameObject BuildWarmRig()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null)
        {
            Debug.LogWarning("[Prewarm] No main camera yet; shader warm draws were skipped.");
            return null;
        }

        var root = new GameObject("PrewarmRig");
        root.transform.SetParent(cam, false);
        root.transform.localPosition = new Vector3(0f, 0f, 1f);

        var canvasGo = new GameObject("WarmCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(root.transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)canvasGo.transform;
        rect.sizeDelta = new Vector2(2f, 2f);
        rect.localScale = Vector3.one * 0.001f;

        AddImage(canvasGo.transform, null);
        AddImage(canvasGo.transform, PanelUI.FrontMaterial);
        AddImage(canvasGo.transform, PanelUI.BackMaterial);
        AddLabel(canvasGo.transform, Style.BodyFont);
        AddLabel(canvasGo.transform, Style.TitleFont);

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(root.transform, false);
        quad.transform.localScale = Vector3.one * 0.002f;
        Material sheet = Resources.Load<Material>("Materials/SheetMaterial");
        if (sheet != null) quad.GetComponent<MeshRenderer>().sharedMaterial = sheet;
        else quad.SetActive(false);

        return root;
    }

    private static void AddImage(Transform parent, Material material)
    {
        var go = new GameObject("WarmImage", typeof(Image));
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).sizeDelta = new Vector2(2f, 2f);
        if (material != null) go.GetComponent<Image>().material = material;
    }

    private static void AddLabel(Transform parent, TMP_FontAsset font)
    {
        var go = new GameObject("WarmLabel", typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).sizeDelta = new Vector2(2f, 2f);
        var label = go.GetComponent<TextMeshProUGUI>();
        label.text = "Warm";
        label.fontSize = 1f;
        if (font != null) label.font = font;
    }
}
