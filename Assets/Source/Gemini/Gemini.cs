using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using GTool = Google.GenAI.Types.Tool;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Networking;

public enum GeminiStatus {
    Off,
    Connecting,
    Live,
    Reconnecting,
    Failed,
    MicDenied
}

public static partial class Gemini {

    public const string ModelId = "gemini-3.1-flash-live-preview";

    private static Client client;
    private static LiveConnectConfig config;
    private static string promptBody;
    private static string promptTail;
    private static readonly ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();
    private static SemaphoreSlim sendSignal;
    private static SemaphoreSlim actionSignal;

    private static readonly ConcurrentQueue<string> protocolLog = new ConcurrentQueue<string>();
    private static volatile bool setupCompleted;
    private static volatile bool generationActive;
    private static volatile bool injecting;
    private static volatile bool turnPending;
    private static volatile bool shutdownAfterTurn;

    private static CancellationTokenSource sessionCts;
    private static Task sessionTask;
    private static volatile AsyncSession liveSession;
    private static bool micGranted;
    private static bool _listening;
    private static volatile GeminiStatus _status = GeminiStatus.Off;
    private static int sessionGeneration;
    private static int connectionCounter;
    private static int activeConnection;
    private static volatile string resumeHandle;

    private static int toolRoundsThisTurn;
    private static int toolRoundId;
    private static volatile bool resumedConnection;
    private static bool webSearchEnabled;

    private static volatile bool keepAlive;
    private static volatile bool goAwayPending;
    private static DateTime goAwayGraceUtc;
    private static int pendingClosingNotice;
    private static int pendingClosedNotice;
    private static DateTime lastInactiveUtc;

    private static readonly TimeSpan IdleReset = TimeSpan.FromHours(2);

    private const int MaxQueuedFrames = 25;
    private const int GoAwayGraceMs = 8000;
    private const int PreviousDrainMs = 5000;

    public static bool Busy => generationActive || turnPending || injecting;
    public static GeminiStatus Status => _status;
    public static int ToolRoundId => Volatile.Read(ref toolRoundId);

    public static void RequestActionPush() => actionSignal?.Release();
    public static void SetKeepAlive(bool value) {
        keepAlive = value;
        if (value) actionSignal?.Release();
    }

    public static void RequestShutdownAfterTurn() => shutdownAfterTurn = true;

    public static void CancelShutdownRequest() => shutdownAfterTurn = false;

    private static bool ConsumeShutdownRequest() {
        if (!shutdownAfterTurn) return false;
        shutdownAfterTurn = false;
        return true;
    }

    public static bool ConsumeClosingNotice() => Interlocked.Exchange(ref pendingClosingNotice, 0) == 1;
    public static bool ConsumeClosedNotice() => Interlocked.Exchange(ref pendingClosedNotice, 0) == 1;

    public static void NoteProtocol(string desc) {
        protocolLog.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {desc}");
        while (protocolLog.Count > 16 && protocolLog.TryDequeue(out _)) { }
    }

    private static string DumpProtocol() => string.Join("\n  ", protocolLog.ToArray());

    private sealed class PendingCall {
        public string Name;
        public DateTime SentUtc;
    }

    private const int ToolStallMs = 20000;

    private static readonly ConcurrentDictionary<string, PendingCall> pendingCalls =
        new ConcurrentDictionary<string, PendingCall>();

    public static void NoteToolDispatched(string id, string name) {
        if (string.IsNullOrEmpty(id)) return;
        pendingCalls[id] = new PendingCall { Name = name, SentUtc = DateTime.UtcNow };
    }

    public static void NoteToolSettled(string id) {
        if (string.IsNullOrEmpty(id)) return;
        pendingCalls.TryRemove(id, out _);
    }

    private static void ClearPendingCalls() => pendingCalls.Clear();

    private static void CheckStalledTools() {
        if (pendingCalls.IsEmpty) return;
        DateTime now = DateTime.UtcNow;

        foreach (var pair in pendingCalls) {
            PendingCall call = pair.Value;
            if (call == null) { pendingCalls.TryRemove(pair.Key, out _); continue; }
            if ((now - call.SentUtc).TotalMilliseconds < ToolStallMs) continue;
            if (!pendingCalls.TryRemove(pair.Key, out _)) continue;

            Debug.LogError($"[Gemini][stall] no tool response was ever sent for {call.Name} " +
                           $"(id={pair.Key}) after {ToolStallMs / 1000}s; the turn cannot complete");
        }
    }

    private static readonly HashSet<string> cancelledCalls = new HashSet<string>();
    private static readonly object cancelGate = new object();

    public static void CancelToolCalls(IEnumerable<string> ids) {
        if (ids == null) return;
        lock (cancelGate)
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id)) cancelledCalls.Add(id);
    }

    public static bool ConsumeToolCallCancelled(string id) {
        if (string.IsNullOrEmpty(id)) return false;
        lock (cancelGate) return cancelledCalls.Remove(id);
    }

    private static bool Current(int gen) => gen == Volatile.Read(ref sessionGeneration);

    private static bool CurrentConnection(int conn) => conn == Volatile.Read(ref activeConnection);

    private static void RetireConnection() => Volatile.Write(ref activeConnection, 0);

    private static void SetStatus(int gen, GeminiStatus status) {
        if (Current(gen)) _status = status;
    }

    private static Task<bool> ensureMicPermission() {
        var tcs = new TaskCompletionSource<bool>();
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Microphone)) {
            tcs.SetResult(true);
            return tcs.Task;
        }
        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => tcs.TrySetResult(true);
        callbacks.PermissionDenied += _ => tcs.TrySetResult(false);
        callbacks.PermissionDeniedAndDontAskAgain += _ => tcs.TrySetResult(false);
        Permission.RequestUserPermission(Permission.Microphone, callbacks);
#else
        tcs.SetResult(true);
#endif
        return tcs.Task;
    }

    private static Task<string> loadApiKey() {
        var path = Application.streamingAssetsPath + "/gemini.key";
        if (!path.Contains("://"))
            path = "file://" + path;

        var tcs = new TaskCompletionSource<string>();
        var req = UnityWebRequest.Get(path);
        var op = req.SendWebRequest();
        op.completed += _ => {
            if (req.result == UnityWebRequest.Result.Success) {
                tcs.SetResult(req.downloadHandler.text.Trim());
            }
            else {
                Debug.LogError($"[Gemini] Failed to load API key from {path}: {req.error}");
                tcs.SetResult("");
            }
            req.Dispose();
        };
        return tcs.Task;
    }

    private static Task initTask;
    private static readonly object initGate = new object();

    public static Task EnsureInit(bool webSearch) {
        lock (initGate) {
            if (initTask == null) initTask = Init(webSearch);
            return initTask;
        }
    }

    public static async Task Init(bool webSearch) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try {
            webSearchEnabled = webSearch;
            var keyTask = loadApiKey();
            var micTask = ensureMicPermission();
            client = new Client(apiKey: await keyTask);
            ResetWindow();

            RebuildConfig();

            micGranted = await micTask;
            if (!micGranted) {
                Debug.LogWarning("[Gemini] Microphone permission denied; voice input disabled.");
                _status = GeminiStatus.MicDenied;
            }

            Speaker.init();
            sendSignal = new SemaphoreSlim(0);
            actionSignal = new SemaphoreSlim(0);
            if (micGranted) {
                Voip.init();
                Voip.turret += sendTick;
            }
        }
        catch (Exception e) {
            Debug.LogError(e);
            _status = GeminiStatus.Failed;
        }
        clock.Stop();
        Debug.Log($"[Gemini] init took {clock.ElapsedMilliseconds} ms");
    }

    private static void RebuildConfig() {
        var tools = new List<GTool>();
        if (webSearchEnabled) tools.Add(new GTool { GoogleSearch = new GoogleSearch() });
        tools.Add(new GTool { FunctionDeclarations = SystemPrompt.ToolDeclarations() });

        promptBody = SystemPrompt.PromptBody(webSearchEnabled);
        promptTail = SystemPrompt.PromptTail();

        config = new LiveConnectConfig {
            SystemInstruction = new Content {
                Parts = new List<Part> { new Part { Text = promptBody + promptTail } }
            },
            ContextWindowCompression = new ContextWindowCompressionConfig {
                TriggerTokens = SafetyNetTrigger,
                SlidingWindow = new SlidingWindow { TargetTokens = SafetyNetTarget }
            },
            ResponseModalities = new List<Modality> { Modality.Audio },
            SpeechConfig = new SpeechConfig {
                LanguageCode = "en-US",
                VoiceConfig = new VoiceConfig {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = "Charon" }
                }
            },
            Tools = tools,
            RealtimeInputConfig = new RealtimeInputConfig {
                TurnCoverage = TurnCoverage.TurnIncludesOnlyActivity,
                AutomaticActivityDetection = new AutomaticActivityDetection {
                    StartOfSpeechSensitivity = StartSensitivity.StartSensitivityLow,
                    EndOfSpeechSensitivity = EndSensitivity.EndSensitivityLow,
                    SilenceDurationMs = 800
                }
            },
            InputAudioTranscription = new AudioTranscriptionConfig {
                LanguageCodes = new List<string> { "en-US" },
                WordTimestamp = true
            },
            OutputAudioTranscription = new AudioTranscriptionConfig {
                WordTimestamp = true
            }
        };
    }

    public static void Destroy() {
        try {
            RetireConnection();
            Interlocked.Increment(ref sessionGeneration);
            sessionCts?.Cancel();
            sessionCts = null;
            sessionTask = null;
            bool hadMic = micGranted;
            if (hadMic) Voip.turret -= sendTick;
            runAudio(() => {
                if (hadMic) Voip.destroy();
                Speaker.destroy();
            });
            _listening = false;
            _status = GeminiStatus.Off;
        }
        catch (Exception e) {
            Debug.LogError(e);
        }
    }

    private static readonly object audioGate = new object();
    private static Task audioTail = Task.CompletedTask;

    private static void runAudio(Action action) {
        lock (audioGate) {
            audioTail = audioTail.ContinueWith(_ => {
                try { action(); }
                catch (Exception e) { Debug.LogError(e); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public static void Listen() {
        if (!micGranted) {
            _status = GeminiStatus.MicDenied;
            return;
        }
        if (_listening) return;
        _listening = true;
        ResetMicAccumulator();
        while (sendQueue.TryDequeue(out _)) { }
        runAudio(() => {
            Speaker.start();
            Voip.start();
        });
    }

    public static void Mute() {
        if (!_listening) return;
        _listening = false;
        ResetMicAccumulator();
        while (sendQueue.TryDequeue(out _)) { }
        runAudio(() => {
            Speaker.stop();
            Voip.stop();
        });
    }
}
