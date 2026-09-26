using System;
using System.Threading.Tasks;
using UnityEngine;

public enum InitPolicy { OnFirstConnect, AtLaunch }

public enum ConnectPolicy { OnFirstUse, OnIntent }

public class GeminiClient : MonoBehaviour {

    [Tooltip("Enable Google Search grounding. Requires a paid-tier (billing-enabled) Gemini API key; leave off on the free tier or the session will be rejected.")]
    public bool enableWebSearch = false;

    [Tooltip("When the Gemini SDK, mic permission and audio are initialised. OnFirstConnect defers all of it until the assistant is first needed.")]
    public InitPolicy initPolicy = InitPolicy.AtLaunch;

    [Tooltip("When a session handshake is opened. OnIntent connects when the tool panel opens; OnFirstUse waits until the assistant is switched on.")]
    public ConnectPolicy connectPolicy = ConnectPolicy.OnIntent;

    private bool _ready;
    private bool _active;
    private bool _intentHeld;
    private int _connectQueuedFrame = -1;
    private GeminiStatus _lastStatus = GeminiStatus.Off;

    public bool Ready => _ready;
    public bool Active => _active;
    public event Action<bool> ActiveChanged;
    public event Action<GeminiStatus> StatusChanged;
    public event Action ContextWarning;
    public event Action ContextExhausted;

    async void Start() {
        if (initPolicy == InitPolicy.AtLaunch) {
            await Gemini.EnsureInit(enableWebSearch);
            if (this == null) return;
        }

        _ready = true;
        Gemini.SetKeepAlive(_active);
        if (_active && isActiveAndEnabled) {
            await ConnectNow("active");
            return;
        }
    }

    private async Task ConnectNow(string reason) {
        await Gemini.EnsureInit(enableWebSearch);
        if (this == null || !isActiveAndEnabled) return;

        Gemini.Connect();
        if (_active) Gemini.Listen();
    }

    private static bool CanWarm =>
        Gemini.Status == GeminiStatus.Off || Gemini.Status == GeminiStatus.Failed;

    private void QueueConnect() => _connectQueuedFrame = Time.frameCount;

    public void SetActive(bool active) {
        bool changed = _active != active;
        _active = active;

        if (_ready) {
            Gemini.SetKeepAlive(active);
            if (active) {
                _ = ConnectNow("activate");
            }
            else {
                Gemini.Mute();
                Gemini.Disconnect();
            }
        }

        if (changed) ActiveChanged?.Invoke(active);
    }

    public void NotifyIntent() {
        _intentHeld = true;
        if (!_ready || _active) return;
        if (connectPolicy == ConnectPolicy.OnFirstUse) return;
        if (!CanWarm) return;

        QueueConnect();
    }

    public void NotifyIntentEnded() {
        _intentHeld = false;
        if (!_ready || _active) return;
        _connectQueuedFrame = -1;
        if (Gemini.Status != GeminiStatus.Off)
            Debug.Log("[Gemini][session] tool panel closed; dropping the intent socket");
        Gemini.Disconnect();
    }

    void Update() {
        if (!_ready) return;

        if (_connectQueuedFrame >= 0 && Time.frameCount > _connectQueuedFrame) {
            _connectQueuedFrame = -1;
            if (CanWarm) _ = ConnectNow("queued");
        }

        var status = Gemini.Status;
        if (status != _lastStatus) {
            _lastStatus = status;
            StatusChanged?.Invoke(status);
        }

        if (Gemini.ConsumeClosingNotice()) ContextWarning?.Invoke();
        if (Gemini.ConsumeClosedNotice()) ContextExhausted?.Invoke();

    }

    void OnDisable() {
        if (_ready) Gemini.Disconnect();
    }

    void OnDestroy() {
        Gemini.Destroy();
    }

    void OnApplicationPause(bool paused) {
        if (!_ready) return;

        if (paused) {
            _connectQueuedFrame = -1;
            Gemini.Disconnect();
            return;
        }

        if (_active) {
            _ = ConnectNow("resume");
        }
        else if (_intentHeld && CanWarm && connectPolicy != ConnectPolicy.OnFirstUse) {
            QueueConnect();
        }
    }
}
