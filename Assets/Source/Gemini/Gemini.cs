using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

public static class Gemini {

    // Core state.

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

    // Init and config: the API key, mic permission, and the Live session config.

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

    // Session lifecycle: connecting, reconnecting after GoAway, and shutting down.

    public static void BeginGoAwayReconnect() {
        if (goAwayPending) return;
        goAwayGraceUtc = DateTime.UtcNow.AddMilliseconds(GoAwayGraceMs);
        goAwayPending = true;
    }

    private static async Task GoAwayPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (s != liveSession) return;
            CheckStalledTools();
            if (!goAwayPending) continue;

            bool quiet = !generationActive && !turnPending && !injecting;
            if (!quiet && DateTime.UtcNow < goAwayGraceUtc) continue;

            goAwayPending = false;
            NoteProtocol($"-> goAway reconnect (quiet={quiet})");
            Debug.Log($"[Gemini][session] cycling socket after GoAway (quiet={quiet})");
            try { await s.CloseAsync().ConfigureAwait(false); } catch { }
            return;
        }
    }

    private static int lastInstructionChars;

    private static void RefreshSystemInstruction() {
        if (config == null || promptBody == null) return;

        string instruction = promptBody + promptTail;
        lastInstructionChars = instruction.Length;
        config.SystemInstruction = new Content {
            Parts = new List<Part> { new Part { Text = instruction } }
        };
    }

    private static async Task RunSessionAsync(int gen, CancellationToken token) {
        int backoffMs = 500;
        int failures = 0;

        while (!token.IsCancellationRequested) {
            goAwayPending = false;
            AsyncSession s = null;
            try {
                RefreshSystemInstruction();
                config.SessionResumption = new SessionResumptionConfig { Handle = resumeHandle };
                resumedConnection = resumeHandle != null;
                setupCompleted = false;
                generationActive = false;
                turnPending = false;
                Interlocked.Exchange(ref toolRoundsThisTurn, 0);
                ResetTranscripts();
                ClearPendingCalls();
                Debug.Log($"[Gemini][diag] connecting: model={ModelId}, tools={config.Tools?.Sum(t => t.FunctionDeclarations?.Count ?? 0)}, promptChars={lastInstructionChars}, resume={resumeHandle != null}, serverTrim={SafetyNetTrigger}/{SafetyNetTarget}");
                s = await client.Live.ConnectAsync(model: ModelId, config: config).ConfigureAwait(false);
                if (!Current(gen)) break;
                _status = GeminiStatus.Live;
                liveSession = s;
                int conn = Interlocked.Increment(ref connectionCounter);
                Volatile.Write(ref activeConnection, conn);
                Debug.Log($"[Gemini][session] live (gen={gen}, conn={conn}, resumed={resumeHandle != null}, contextBefore={contextTokens})");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var send = SendPump(s, token);
                var recv = ReceivePump(s, gen, conn, token);
                var push = ActionPushPump(s, token);
                var away = GoAwayPump(s, token);
                await Task.WhenAny(send, recv).ConfigureAwait(false);
                if (CurrentConnection(conn)) RetireConnection();
                if (ReferenceEquals(liveSession, s)) liveSession = null;
                sendSignal?.Release();
                actionSignal?.Release();
                try { await s.CloseAsync().ConfigureAwait(false); } catch { }
                try { await Task.WhenAll(send, recv, push, away).ConfigureAwait(false); } catch { }
                s = null;
                sw.Stop();

                if (sw.ElapsedMilliseconds >= 2000) {
                    failures = 0;
                    backoffMs = 500;
                }
                else {
                    failures++;
                    backoffMs = Mathf.Min(backoffMs * 2, 5000);
                    if (resumeHandle != null) {
                        Debug.LogWarning("[Gemini] session died immediately; dropping stale resume handle and starting fresh");
                        resumeHandle = null;
                    }
                }
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception e) {
                Debug.LogError($"[Gemini] session error: {e}");
                if (s == null) resumeHandle = null;
                failures++;
                backoffMs = Mathf.Min(backoffMs * 2, 5000);
            }
            finally {
                if (s != null) {
                    if (ReferenceEquals(liveSession, s)) liveSession = null;
                    try { await s.CloseAsync().ConfigureAwait(false); } catch { }
                }
            }

            if (token.IsCancellationRequested) break;
            if (Exhausted) {
                Interlocked.Exchange(ref pendingClosedNotice, 1);
                break;
            }
            if (!keepAlive) break;

            if (failures >= 5) {
                Debug.LogError("[Gemini] giving up after repeated session failures");
                SetStatus(gen, GeminiStatus.Failed);
                break;
            }

            SetStatus(gen, GeminiStatus.Reconnecting);
            try { await Task.Delay(backoffMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        if (Current(gen) && _status != GeminiStatus.Failed)
            _status = GeminiStatus.Off;
    }

    public static void Connect() {
        if (client == null) return;

        bool running = sessionTask != null && !sessionTask.IsCompleted;
        if (running && (_status == GeminiStatus.Live || _status == GeminiStatus.Connecting)) return;

        if (lastInactiveUtc != default && DateTime.UtcNow - lastInactiveUtc > IdleReset) {
            resumeHandle = null;
            ResetWindow();
        }

        if (Exhausted) ClearExhaustion();

        _status = GeminiStatus.Connecting;
        shutdownAfterTurn = false;

        Task previous = sessionTask;
        sessionCts?.Cancel();
        int gen = Interlocked.Increment(ref sessionGeneration);
        var cts = new CancellationTokenSource();
        sessionCts = cts;
        sessionTask = RunAfterAsync(previous, gen, cts.Token);
    }

    private static async Task RunAfterAsync(Task previous, int gen, CancellationToken token) {
        if (previous != null && !previous.IsCompleted) {
            Debug.Log("[Gemini][session] waiting for the previous session loop to drain");
            var drained = await Task.WhenAny(previous, Task.Delay(PreviousDrainMs)).ConfigureAwait(false);
            if (!ReferenceEquals(drained, previous))
                Debug.LogWarning($"[Gemini][session] previous loop did not drain within {PreviousDrainMs} ms; starting anyway");
        }

        if (!Current(gen)) {
            Debug.Log($"[Gemini][session] generation {gen} superseded before it started");
            return;
        }

        await RunSessionAsync(gen, token).ConfigureAwait(false);
    }

    public static void Disconnect() {
        Mute();
        RetireConnection();
        Interlocked.Increment(ref sessionGeneration);
        CancellationTokenSource cts = sessionCts;
        sessionCts = null;
        if (cts != null)
            Task.Run(() => {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        lastInactiveUtc = DateTime.UtcNow;
        if (_status != GeminiStatus.MicDenied && _status != GeminiStatus.Failed)
            _status = GeminiStatus.Off;
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

    private static bool Current(int gen) => gen == Volatile.Read(ref sessionGeneration);

    private static bool CurrentConnection(int conn) => conn == Volatile.Read(ref activeConnection);

    private static void RetireConnection() => Volatile.Write(ref activeConnection, 0);

    private static void SetStatus(int gen, GeminiStatus status) {
        if (Current(gen)) _status = status;
    }

    // Audio control.

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

    // Send: mic frames, injected prompts and clips, and pushed state.

    private const int FrameBytes = 640;
    private const int RawLogInterval = 500;

    private static readonly object micGate = new object();
    private static byte[] micAccum = new byte[FrameBytes * 4];
    private static int micAccumLen;

    private static int rawFrames;
    private static int rawMin = int.MaxValue;
    private static int rawMax;

    private static void ResetMicAccumulator() {
        lock (micGate) micAccumLen = 0;
    }

    private static void sendTick(byte[] data) {
        if (data == null || data.Length == 0) return;

        rawFrames++;
        if (data.Length < rawMin) rawMin = data.Length;
        if (data.Length > rawMax) rawMax = data.Length;
        if (rawFrames % RawLogInterval == 0) {
            Debug.Log($"[Gemini] mic in: {rawFrames} frames, raw {rawMin}-{rawMax} B (~{rawMin / 32f:0.#}-{rawMax / 32f:0.#} ms); sending fixed {FrameBytes} B (~{FrameBytes / 32f:0.#} ms)");
            rawMin = int.MaxValue;
            rawMax = 0;
        }

        lock (micGate) {
            int needed = micAccumLen + data.Length;
            if (needed > micAccum.Length)
                Array.Resize(ref micAccum, Math.Max(micAccum.Length * 2, needed));
            Buffer.BlockCopy(data, 0, micAccum, micAccumLen, data.Length);
            micAccumLen = needed;

            int off = 0;
            while (micAccumLen - off >= FrameBytes) {
                var frame = new byte[FrameBytes];
                Buffer.BlockCopy(micAccum, off, frame, 0, FrameBytes);
                off += FrameBytes;
                sendQueue.Enqueue(frame);
                while (sendQueue.Count > MaxQueuedFrames && sendQueue.TryDequeue(out _)) { }
                sendSignal?.Release();
            }

            if (off == 0) return;
            micAccumLen -= off;
            if (micAccumLen > 0) Buffer.BlockCopy(micAccum, off, micAccum, 0, micAccumLen);
        }
    }

    public static async Task<bool> InjectPromptAsync(string text, CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = liveSession;
        if (s == null || _status != GeminiStatus.Live || !setupCompleted) {
            Debug.LogWarning("[Gemini][inject] no live session; prompt dropped.");
            return false;
        }

        injecting = true;
        try {
            ResetMicAccumulator();
            while (sendQueue.TryDequeue(out _)) { }

            NoteProtocol($"-> clientContent {text.Length}B (turnComplete=true)");
            await s.SendClientContentAsync(new LiveSendClientContentParameters {
                Turns = new List<Content> {
                    new Content {
                        Role = "user",
                        Parts = new List<Part> { new Part { Text = text } }
                    }
                },
                TurnComplete = true
            }).ConfigureAwait(false);

            turnPending = true;
            return true;
        }
        catch (Exception e) {
            Debug.LogWarning($"[Gemini][inject] failed: {e.Message}");
            return false;
        }
        finally {
            injecting = false;
        }
    }

    public static async Task InjectClipAsync(byte[] pcm16kMono, CancellationToken token = default) {
        if (pcm16kMono == null || pcm16kMono.Length == 0) return;
        injecting = true;
        try {
            ResetMicAccumulator();
            while (sendQueue.TryDequeue(out _)) { }
            for (int off = 0; off < pcm16kMono.Length; off += FrameBytes) {
                if (token.IsCancellationRequested) break;
                int len = Math.Min(FrameBytes, pcm16kMono.Length - off);
                var frame = new byte[len];
                Array.Copy(pcm16kMono, off, frame, 0, len);
                sendQueue.Enqueue(frame);
                while (sendQueue.Count > MaxQueuedFrames && sendQueue.TryDequeue(out _)) { }
                sendSignal?.Release();
                try { await Task.Delay(20, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            while (sendQueue.Count > 0 && !token.IsCancellationRequested) {
                try { await Task.Delay(10, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            var s = liveSession;
            if (s != null) {
                try {
                    NoteProtocol("audioStreamEnd");
                    await s.SendRealtimeInputAsync(new LiveSendRealtimeInputParameters {
                        AudioStreamEnd = true
                    }).ConfigureAwait(false);
                    turnPending = true;
                }
                catch (Exception e) {
                    Debug.LogWarning($"[Gemini] audioStreamEnd failed: {e.Message}");
                }
            }
        }
        finally {
            injecting = false;
        }
    }

    private static async Task SendPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try { await sendSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (s != liveSession) return;
            while (sendQueue.TryDequeue(out byte[] data)) {
                if (s != liveSession) return;
                if (!setupCompleted || _status != GeminiStatus.Live) continue;
                try {
                    await s.SendRealtimeInputAsync(new LiveSendRealtimeInputParameters {
                        Audio = new Blob {
                            MimeType = "audio/pcm;rate=16000",
                            Data = data
                        }
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) {
                    Debug.LogError($"[Gemini] send failed: {e.Message}");
                    return;
                }
            }
        }
    }

    private static bool IdleForPush() =>
        keepAlive && setupCompleted && _status == GeminiStatus.Live && !Busy;

    private static async Task ActionPushPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            if (!StateChannel.HasPending || !keepAlive) {
                try { await actionSignal.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            if (s != liveSession) return;
            if (!keepAlive) continue;

            int waited = 0;
            while (!IdleForPush() && waited < 30000) {
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (s != liveSession) return;
                waited += 100;
            }
            if (!IdleForPush()) continue;

            string text = null;
            try {
                await MainThread.Run(() => {
                    if (StateChannel.TryTakeBatch(out string batch)) text = batch;
                }).ConfigureAwait(false);
            }
            catch (Exception e) {
                Debug.LogWarning($"[Gemini] push could not gather: {e.Message}");
                continue;
            }
            if (string.IsNullOrEmpty(text)) continue;

            try {
                NoteProtocol($"-> clientContent {text.Length}B (turnComplete=false)");
                Debug.Log($"[Gemini][push] {text.Replace('\n', ' ')}");
                await s.SendClientContentAsync(new LiveSendClientContentParameters {
                    Turns = new List<Content> {
                        new Content {
                            Role = "user",
                            Parts = new List<Part> { new Part { Text = text } }
                        }
                    },
                    TurnComplete = false
                }).ConfigureAwait(false);
            }
            catch (Exception e) {
                Debug.LogWarning($"[Gemini][push] failed: {e.Message}");
            }
        }
    }

    // Receive: server messages, usage, tool calls and transcripts.

    private static readonly System.Text.StringBuilder inTranscript = new System.Text.StringBuilder();
    private static readonly System.Text.StringBuilder outTranscript = new System.Text.StringBuilder();
    private static readonly List<WordInfo> inWords = new List<WordInfo>();
    private static readonly List<WordInfo> outWords = new List<WordInfo>();
    private static bool pendingUserFlush;
    private static string lastUserText;

    private static void ResetTranscripts() {
        inTranscript.Clear();
        outTranscript.Clear();
        inWords.Clear();
        outWords.Clear();
        pendingUserFlush = false;
        lastUserText = null;
    }

    private const int MaxSpokenChars = 400;

    private static string Spoken(string text) {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxSpokenChars ? flat : flat.Substring(0, MaxSpokenChars) + "...";
    }

    private static void FlushUtterance(System.Text.StringBuilder buf, List<WordInfo> words, string type) {
        if (buf.Length == 0) { words.Clear(); return; }
        string text = buf.ToString().Trim();
        buf.Clear();

        words.Clear();

        if (text.Length == 0) return;

        if (type == "user_utterance") {
            if (text == lastUserText) return;
            lastUserText = text;
        }

        Debug.Log($"[Gemini][{(type == "user_utterance" ? "user" : "ada")}] {Spoken(text)}");
    }

    private static async Task ReceivePump(AsyncSession s, int gen, int conn, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            LiveServerMessage response;
            try { response = await s.ReceiveAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) {
                Debug.LogError($"[Gemini] receive failed: {e.Message}");
                Debug.LogError($"[Gemini][diag] setupCompleted={setupCompleted}; recent protocol:\n  {DumpProtocol()}");
                break;
            }
            if (response == null) break;
            if (!Current(gen)) {
                Debug.LogWarning($"[Gemini][session] dropping message from superseded generation {gen}");
                break;
            }
            if (!CurrentConnection(conn)) {
                Debug.LogWarning($"[Gemini][session] dropping message from retired connection {conn} (gen {gen})");
                break;
            }
            receiveTick(s, conn, response);
        }
    }

    private static void receiveTick(AsyncSession s, int conn, LiveServerMessage response) {
        HandleUsage(response.UsageMetadata, conn);
        HandleControl(s, response);
        HandleTools(s, response);
        HandleContent(response.ServerContent);
        if (pendingUserFlush) {
            pendingUserFlush = false;
            FlushUtterance(inTranscript, inWords, "user_utterance");
        }
    }

    private static void HandleUsage(UsageMetadata usage, int conn) {
        if (usage == null) return;

        string byMod = "";
        var details = usage.PromptTokensDetails;
        if (details != null) {
            var modParts = new List<string>();
            foreach (var d in details) {
                if (d == null) continue;
                modParts.Add($"{d.Modality}:{d.TokenCount}");
            }
            byMod = string.Join(", ", modParts);
        }

        long promptTok = (long?)usage.PromptTokenCount ?? 0;
        long cachedTok = (long?)usage.CachedContentTokenCount ?? 0;
        long thoughtTok = (long?)usage.ThoughtsTokenCount ?? 0;
        long uncachedTok = promptTok > cachedTok ? promptTok - cachedTok : 0;
        int rounds = Volatile.Read(ref toolRoundsThisTurn);
        bool usable = TrackContext(conn, promptTok, uncachedTok, rounds, out long context, out bool exact);
        string how = exact ? "exact" : $"estimated over {rounds} tool rounds";

        Debug.Log($"[Gemini][usage] conn={conn} context={context} ({how}) prompt={promptTok} response={usage.ResponseTokenCount} total={usage.TotalTokenCount} session={sessionPromptTokens} cached={cachedTok} uncached={uncachedTok} thoughts={thoughtTok} sessionUncached={sessionUncachedTokens} promptByModality=[{byMod}] at {DateTime.UtcNow:HH:mm:ss.fff}");

        lastPromptTokens = promptTok;
        if (exact && lastExactContext > 0 && context + 2000 < lastExactContext)
            Debug.Log($"[Gemini][window] context fell {lastExactContext} -> {context}; the server trimmed it");
        if (exact) lastExactContext = context;

        if (usable) ObserveTokens(context);
    }

    private static void HandleControl(AsyncSession s, LiveServerMessage response) {
        if (response.SetupComplete != null) {
            setupCompleted = true;
            Debug.Log("[Gemini][diag] setup complete");
            StateChannel.RequestSnapshot();
        }

        var resume = response.SessionResumptionUpdate;
        if (resume != null && resume.Resumable == true && !string.IsNullOrEmpty(resume.NewHandle))
            resumeHandle = resume.NewHandle;

        if (response.GoAway != null) {
            Debug.Log($"[Gemini] server GoAway (time left: {response.GoAway.TimeLeft})");
            if (keepAlive) BeginGoAwayReconnect();
            else _ = s.CloseAsync();
        }
    }

    private static void HandleTools(AsyncSession s, LiveServerMessage response) {
        if (response.ToolCall != null) {
            generationActive = true;
            var calls = response.ToolCall.FunctionCalls;
            if (calls != null) {
                Interlocked.Increment(ref toolRoundId);
                if (Interlocked.Increment(ref toolRoundsThisTurn) == 1) pendingUserFlush = true;
                NoteProtocol($"<- toolCall {string.Join(",", calls.ConvertAll(c => c.Name))}");
                foreach (var call in calls) {
                    NoteToolDispatched(call.Id, call.Name);
                    _ = Function.Run(s, call);
                }
            }
        }

        if (response.ToolCallCancellation != null) {
            var ids = response.ToolCallCancellation.Ids;
            CancelToolCalls(ids);
            NoteProtocol($"<- toolCallCancellation ids={(ids != null ? string.Join(",", ids) : "")}");
            Debug.Log($"[Gemini][diag] toolCallCancellation ids={(ids != null ? string.Join(",", ids) : "")}");
        }
    }

    private static void HandleContent(LiveServerContent content) {
        if (content == null) return;

        var grounding = content.GroundingMetadata;
        if (grounding != null) {
            var queries = grounding.WebSearchQueries;
            string asked = queries != null ? string.Join(" | ", queries) : "";
            NoteProtocol($"<- groundingMetadata queries=[{asked}]");
            Debug.Log($"[Gemini][search] grounded; webSearchQueries=[{asked}]");
        }

        var inTx = content.InputTranscription;
        if (inTx != null) {
            if (!string.IsNullOrEmpty(inTx.Text)) inTranscript.Append(inTx.Text);
            if (inTx.Words != null) inWords.AddRange(inTx.Words);
            if (inTx.Finished == true) FlushUtterance(inTranscript, inWords, "user_utterance");
        }

        var outTx = content.OutputTranscription;
        if (outTx != null) {
            if (!string.IsNullOrEmpty(outTx.Text)) { outTranscript.Append(outTx.Text); generationActive = true; }
            if (outTx.Words != null) outWords.AddRange(outTx.Words);
            if (outTx.Finished == true) FlushUtterance(outTranscript, outWords, "model_utterance");
        }

        if (content.Interrupted == true) {
            generationActive = false;
            turnPending = false;
            NoteProtocol("<- interrupted");
            Speaker.flush();
            FlushUtterance(outTranscript, outWords, "model_utterance");
            return;
        }

        if (content.TurnComplete == true) {
            generationActive = false;
            turnPending = false;
            NoteProtocol("<- turnComplete");

            var reason = content.TurnCompleteReason;
            if (reason != null) NoteProtocol($"<- turnCompleteReason {reason}");

            FlushUtterance(inTranscript, inWords, "user_utterance");
            FlushUtterance(outTranscript, outWords, "model_utterance");
            pendingUserFlush = false;
            lastUserText = null;
            Interlocked.Exchange(ref toolRoundsThisTurn, 0);
            AgentTurn.Clear();
            if (ConsumeShutdownRequest())
                _ = MainThread.Run(() => {
                    var watch = Scene.Assistant;
                    if (watch != null) watch.SetGeminiActive(false, AssistantCause.Agent);
                });
        }

        var parts = content.ModelTurn?.Parts;
        if (parts == null) return;

        generationActive = true;
        for (int i = 0; i < parts.Count; i++) {
            var data = parts[i].InlineData;
            if (data?.Data != null && data.Data.Length > 0 &&
                data.MimeType != null && data.MimeType.StartsWith("audio/pcm")) {
                Speaker.write(data.Data);
            }
        }
    }

    // Tool calls: dispatched, stalled and cancelled.

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

    // Context window: token accounting and the ceiling.

    public const long ContextWindowTokens = 131072;

    public const long SafetyNetTrigger = 48000;
    public const long SafetyNetTarget = 24000;
    public const long ExhaustTokens = ContextWindowTokens / 2;
    public const long ExhaustWarnTokens = ExhaustTokens - 6000;

    private static int usageConnection;
    private static long contextTokens;
    private static long sessionPromptTokens;
    private static long sessionUncachedTokens;
    private static long lastExactContext;
    private static long lastPromptTokens = -1;

    private static volatile bool exhausted;
    private static volatile bool exhaustWarned;

    public static bool Exhausted => exhausted;

    public static void ResetWindow() {
        exhausted = false;
        exhaustWarned = false;
        ResetContextEstimate();
        StateChannel.ClearPending();
    }

    public static void ResetContextEstimate() {
        usageConnection = 0;
        contextTokens = 0;
        sessionPromptTokens = 0;
        sessionUncachedTokens = 0;
        lastExactContext = 0;
        lastPromptTokens = -1;
        Interlocked.Exchange(ref toolRoundsThisTurn, 0);
    }

    private static bool TrackContext(int conn, long promptTokens, long uncachedTokens, int rounds, out long context, out bool exact) {
        if (conn != usageConnection) {
            usageConnection = conn;
            if (!resumedConnection) contextTokens = 0;
        }

        if (promptTokens > 0) sessionPromptTokens += promptTokens;
        if (uncachedTokens > 0) sessionUncachedTokens += uncachedTokens;

        exact = rounds <= 0;
        context = contextTokens;
        if (promptTokens <= 0) return false;

        long reading = promptTokens / (rounds + 1);
        if (exact || reading > contextTokens) contextTokens = reading;

        context = contextTokens;
        return true;
    }

    public static void ObserveTokens(long context) => ObserveCeiling(context);

    private static void ObserveCeiling(long context) {
        if (context >= ExhaustTokens) { MarkExhausted(context); return; }
        if (context < ExhaustWarnTokens || exhaustWarned) return;

        exhaustWarned = true;
        Debug.LogWarning($"[Gemini][window] approaching context ceiling: {context} of {ExhaustTokens}");
        Interlocked.Exchange(ref pendingClosingNotice, 1);
    }

    public static void ClearExhaustion() {
        exhausted = false;
        exhaustWarned = false;
    }

    private static void MarkExhausted(long context) {
        if (exhausted) return;
        exhausted = true;
        Debug.LogWarning($"[Gemini][window] context ceiling reached: {context} >= {ExhaustTokens}");
        resumeHandle = null;
        RetireConnection();
        var s = liveSession;
        if (s != null) { try { _ = s.CloseAsync(); } catch { } }
    }

    // Protocol log.

    public static void NoteProtocol(string desc) {
        protocolLog.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {desc}");
        while (protocolLog.Count > 16 && protocolLog.TryDequeue(out _)) { }
    }

    private static string DumpProtocol() => string.Join("\n  ", protocolLog.ToArray());
}
