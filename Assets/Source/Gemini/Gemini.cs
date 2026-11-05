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

    private const string ModelId = "gemini-3.1-flash-live-preview";

    private static Client _client;
    private static LiveConnectConfig _config;
    private static readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
    private static SemaphoreSlim _sendSignal;
    private static SemaphoreSlim _actionSignal;

    private static readonly ConcurrentQueue<string> _protocolLog = new ConcurrentQueue<string>();
    private static volatile bool _setupCompleted;
    private static volatile bool _generationActive;
    private static volatile bool _shutdownAfterTurn;

    private static CancellationTokenSource _sessionCts;
    private static Task _sessionTask;
    private static volatile AsyncSession _liveSession;
    private static bool _micGranted;
    private static bool _listening;
    private static volatile GeminiStatus _status = GeminiStatus.Off;
    private static int _sessionGeneration;
    private static int _connectionCounter;
    private static int _activeConnection;
    private static volatile string _resumeHandle;

    private static int _toolRoundsThisTurn;
    private static int _toolRoundId;
    private static volatile bool _resumedConnection;
    private static bool _webSearchEnabled;

    private static volatile bool _keepAlive;
    private static volatile bool _goAwayPending;
    private static DateTime _goAwayGraceUtc;
    private static int _pendingClosingNotice;
    private static int _pendingClosedNotice;
    private static DateTime _lastInactiveUtc;

    private static readonly TimeSpan IdleReset = TimeSpan.FromHours(2);

    private const int MaxQueuedFrames = 25;
    private const int GoAwayGraceMs = 8000;
    private const int PreviousDrainMs = 5000;
    private const int InitialBackoffMs = 500;
    private const int MaxBackoffMs = 5000;
    private const int MaxFailures = 5;

    public static GeminiStatus Status => _status;
    public static int ToolRoundId => Volatile.Read(ref _toolRoundId);

    public static void RequestActionPush() => _actionSignal?.Release();
    public static void SetKeepAlive(bool value) {
        _keepAlive = value;
        if (value) _actionSignal?.Release();
    }

    public static void RequestShutdownAfterTurn() => _shutdownAfterTurn = true;

    public static void CancelShutdownRequest() => _shutdownAfterTurn = false;

    private static bool ConsumeShutdownRequest() {
        if (!_shutdownAfterTurn) return false;
        _shutdownAfterTurn = false;
        return true;
    }

    public static bool ConsumeClosingNotice() => Interlocked.Exchange(ref _pendingClosingNotice, 0) == 1;
    public static bool ConsumeClosedNotice() => Interlocked.Exchange(ref _pendingClosedNotice, 0) == 1;

    // Init and config: the API key, mic permission, and the Live session config.

    private static Task<bool> EnsureMicPermission() {
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

    private static Task<string> LoadApiKey() {
        var tcs = new TaskCompletionSource<string>();
        var req = UnityWebRequest.Get(StreamingAssets.Url("gemini.key"));
        var op = req.SendWebRequest();
        op.completed += _ => {
            if (req.result == UnityWebRequest.Result.Success) {
                tcs.SetResult(req.downloadHandler.text.Trim());
            }
            else {
                Debug.LogError($"[Gemini] Failed to load API key from {req.url}: {req.error}");
                tcs.SetResult("");
            }
            req.Dispose();
        };
        return tcs.Task;
    }

    private static Task _initTask;
    private static readonly object _initGate = new object();

    public static Task EnsureInit(bool webSearch) {
        lock (_initGate) {
            if (_initTask == null) _initTask = Init(webSearch);
            return _initTask;
        }
    }

    private static async Task Init(bool webSearch) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try {
            _webSearchEnabled = webSearch;
            var keyTask = LoadApiKey();
            var micTask = EnsureMicPermission();
            _client = new Client(apiKey: await keyTask);
            ResetWindow();

            BuildConfig();

            _micGranted = await micTask;
            if (!_micGranted) {
                Debug.LogWarning("[Gemini] Microphone permission denied; voice input disabled.");
                _status = GeminiStatus.MicDenied;
            }

            Speaker.Init();
            _sendSignal = new SemaphoreSlim(0);
            _actionSignal = new SemaphoreSlim(0);
            if (_micGranted) {
                Voip.Init();
                Voip.OnAudioCaptured += SendTick;
            }
        }
        catch (Exception e) {
            Debug.LogError(e);
            _status = GeminiStatus.Failed;
        }
        clock.Stop();
        Debug.Log($"[Gemini] init took {clock.ElapsedMilliseconds} ms");
    }

    private static void BuildConfig() {
        var tools = new List<GTool>();
        if (_webSearchEnabled) tools.Add(new GTool { GoogleSearch = new GoogleSearch() });
        tools.Add(new GTool { FunctionDeclarations = SystemPrompt.ToolDeclarations() });

        string instruction = SystemPrompt.Instruction(_webSearchEnabled);
        Debug.Log($"[Gemini] system instruction: {instruction.Length} chars");

        _config = new LiveConnectConfig {
            SystemInstruction = new Content {
                Parts = new List<Part> { new Part { Text = instruction } }
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
                LanguageCodes = new List<string> { "en-US" }
            },
            OutputAudioTranscription = new AudioTranscriptionConfig()
        };
    }

    // Session lifecycle: connecting, reconnecting after GoAway, and shutting down.

    private static void BeginGoAwayReconnect() {
        if (_goAwayPending) return;
        _goAwayGraceUtc = DateTime.UtcNow.AddMilliseconds(GoAwayGraceMs);
        _goAwayPending = true;
    }

    private static async Task GoAwayPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (s != _liveSession) return;
            CheckStalledTools();
            if (!_goAwayPending) continue;

            bool quiet = !_generationActive;
            if (!quiet && DateTime.UtcNow < _goAwayGraceUtc) continue;

            _goAwayPending = false;
            NoteProtocol($"-> goAway reconnect (quiet={quiet})");
            Debug.Log($"[Gemini][session] cycling socket after GoAway (quiet={quiet})");
            try { await s.CloseAsync().ConfigureAwait(false); } catch { }
            return;
        }
    }

    private static async Task RunSessionAsync(int gen, CancellationToken token) {
        int backoffMs = InitialBackoffMs;
        int failures = 0;
        void Backoff() {
            failures++;
            backoffMs = Mathf.Min(backoffMs * 2, MaxBackoffMs);
        }

        while (!token.IsCancellationRequested) {
            _goAwayPending = false;
            AsyncSession s = null;
            try {
                _config.SessionResumption = new SessionResumptionConfig { Handle = _resumeHandle };
                _resumedConnection = _resumeHandle != null;
                _setupCompleted = false;
                _generationActive = false;
                Interlocked.Exchange(ref _toolRoundsThisTurn, 0);
                ResetTranscripts();
                ClearPendingCalls();
                Debug.Log($"[Gemini][diag] connecting: model={ModelId}, tools={_config.Tools?.Sum(t => t.FunctionDeclarations?.Count ?? 0)}, resume={_resumeHandle != null}, serverTrim={SafetyNetTrigger}/{SafetyNetTarget}");
                s = await _client.Live.ConnectAsync(model: ModelId, config: _config).ConfigureAwait(false);
                if (!Current(gen)) break;
                _status = GeminiStatus.Live;
                _liveSession = s;
                int conn = Interlocked.Increment(ref _connectionCounter);
                Volatile.Write(ref _activeConnection, conn);
                Debug.Log($"[Gemini][session] live (gen={gen}, conn={conn}, resumed={_resumeHandle != null}, contextBefore={_contextTokens})");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var send = SendPump(s, token);
                var recv = ReceivePump(s, gen, conn, token);
                var push = ActionPushPump(s, token);
                var away = GoAwayPump(s, token);
                await Task.WhenAny(send, recv).ConfigureAwait(false);
                if (CurrentConnection(conn)) RetireConnection();
                if (ReferenceEquals(_liveSession, s)) _liveSession = null;
                _sendSignal?.Release();
                _actionSignal?.Release();
                try { await s.CloseAsync().ConfigureAwait(false); } catch { }
                try { await Task.WhenAll(send, recv, push, away).ConfigureAwait(false); } catch { }
                s = null;
                sw.Stop();

                if (sw.ElapsedMilliseconds >= 2000) {
                    failures = 0;
                    backoffMs = InitialBackoffMs;
                }
                else {
                    Backoff();
                    if (_resumeHandle != null) {
                        Debug.LogWarning("[Gemini] session died immediately; dropping stale resume handle and starting fresh");
                        _resumeHandle = null;
                    }
                }
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception e) {
                Debug.LogError($"[Gemini] session error: {e}");
                if (s == null) _resumeHandle = null;
                Backoff();
            }
            finally {
                if (s != null) {
                    if (ReferenceEquals(_liveSession, s)) _liveSession = null;
                    try { await s.CloseAsync().ConfigureAwait(false); } catch { }
                }
            }

            if (token.IsCancellationRequested) break;
            if (_exhausted) {
                Interlocked.Exchange(ref _pendingClosedNotice, 1);
                break;
            }
            if (!_keepAlive) break;

            if (failures >= MaxFailures) {
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
        if (_client == null) return;

        bool running = _sessionTask != null && !_sessionTask.IsCompleted;
        if (running && (_status == GeminiStatus.Live || _status == GeminiStatus.Connecting)) return;

        if (_lastInactiveUtc != default && DateTime.UtcNow - _lastInactiveUtc > IdleReset) {
            _resumeHandle = null;
            ResetWindow();
        }

        ClearExhaustion();

        _status = GeminiStatus.Connecting;
        _shutdownAfterTurn = false;

        Task previous = _sessionTask;
        _sessionCts?.Cancel();
        int gen = Interlocked.Increment(ref _sessionGeneration);
        var cts = new CancellationTokenSource();
        _sessionCts = cts;
        _sessionTask = RunAfterAsync(previous, gen, cts.Token);
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
        Interlocked.Increment(ref _sessionGeneration);
        CancellationTokenSource cts = _sessionCts;
        _sessionCts = null;
        if (cts != null)
            Task.Run(() => {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        _lastInactiveUtc = DateTime.UtcNow;
        if (_status != GeminiStatus.MicDenied && _status != GeminiStatus.Failed)
            _status = GeminiStatus.Off;
    }

    public static void Destroy() {
        try {
            RetireConnection();
            Interlocked.Increment(ref _sessionGeneration);
            _sessionCts?.Cancel();
            _sessionCts = null;
            _sessionTask = null;
            bool hadMic = _micGranted;
            if (hadMic) Voip.OnAudioCaptured -= SendTick;
            RunAudio(() => {
                if (hadMic) Voip.Destroy();
                Speaker.Destroy();
            });
            _listening = false;
            _status = GeminiStatus.Off;
        }
        catch (Exception e) {
            Debug.LogError(e);
        }
    }

    private static bool Current(int gen) => gen == Volatile.Read(ref _sessionGeneration);

    private static bool CurrentConnection(int conn) => conn == Volatile.Read(ref _activeConnection);

    private static void RetireConnection() => Volatile.Write(ref _activeConnection, 0);

    private static void SetStatus(int gen, GeminiStatus status) {
        if (Current(gen)) _status = status;
    }

    // Audio control.

    private static readonly object _audioGate = new object();
    private static Task _audioTail = Task.CompletedTask;

    private static void RunAudio(Action action) {
        lock (_audioGate) {
            _audioTail = _audioTail.ContinueWith(_ => {
                try { action(); }
                catch (Exception e) { Debug.LogError(e); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public static void Listen() {
        if (!_micGranted) {
            _status = GeminiStatus.MicDenied;
            return;
        }
        if (_listening) return;
        _listening = true;
        ClearPendingMic();
        RunAudio(() => {
            Speaker.Start();
            Voip.Start();
        });
    }

    public static void Mute() {
        if (!_listening) return;
        _listening = false;
        ClearPendingMic();
        RunAudio(() => {
            Speaker.Stop();
            Voip.Stop();
        });
    }

    // Send: mic frames and pushed state.

    private const int FrameBytes = 640;
    private const int RawLogInterval = 500;

    private static readonly object _micGate = new object();
    private static byte[] _micAccum = new byte[FrameBytes * 4];
    private static int _micAccumLen;

    private static int _rawFrames;
    private static int _rawMin = int.MaxValue;
    private static int _rawMax;

    private static void ClearPendingMic() {
        lock (_micGate) _micAccumLen = 0;
        while (_sendQueue.TryDequeue(out _)) { }
    }

    private static void SendTick(byte[] data) {
        if (data == null || data.Length == 0) return;

        _rawFrames++;
        if (data.Length < _rawMin) _rawMin = data.Length;
        if (data.Length > _rawMax) _rawMax = data.Length;
        if (_rawFrames % RawLogInterval == 0) {
            Debug.Log($"[Gemini] mic in: {_rawFrames} frames, raw {_rawMin}-{_rawMax} B (~{_rawMin / 32f:0.#}-{_rawMax / 32f:0.#} ms); sending fixed {FrameBytes} B (~{FrameBytes / 32f:0.#} ms)");
            _rawMin = int.MaxValue;
            _rawMax = 0;
        }

        lock (_micGate) {
            int needed = _micAccumLen + data.Length;
            if (needed > _micAccum.Length)
                Array.Resize(ref _micAccum, Math.Max(_micAccum.Length * 2, needed));
            Buffer.BlockCopy(data, 0, _micAccum, _micAccumLen, data.Length);
            _micAccumLen = needed;

            int off = 0;
            while (_micAccumLen - off >= FrameBytes) {
                var frame = new byte[FrameBytes];
                Buffer.BlockCopy(_micAccum, off, frame, 0, FrameBytes);
                off += FrameBytes;
                _sendQueue.Enqueue(frame);
                while (_sendQueue.Count > MaxQueuedFrames && _sendQueue.TryDequeue(out _)) { }
                _sendSignal?.Release();
            }

            if (off == 0) return;
            _micAccumLen -= off;
            if (_micAccumLen > 0) Buffer.BlockCopy(_micAccum, off, _micAccum, 0, _micAccumLen);
        }
    }

    private static async Task SendPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try { await _sendSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (s != _liveSession) return;
            while (_sendQueue.TryDequeue(out byte[] data)) {
                if (s != _liveSession) return;
                if (!_setupCompleted || _status != GeminiStatus.Live) continue;
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
        _keepAlive && _setupCompleted && _status == GeminiStatus.Live && !_generationActive;

    private static async Task ActionPushPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            if (!StateChannel.HasPending || !_keepAlive) {
                try { await _actionSignal.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            if (s != _liveSession) return;
            if (!_keepAlive) continue;

            int waited = 0;
            while (!IdleForPush() && waited < 30000) {
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (s != _liveSession) return;
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

    private static readonly System.Text.StringBuilder _inTranscript = new System.Text.StringBuilder();
    private static readonly System.Text.StringBuilder _outTranscript = new System.Text.StringBuilder();
    private static bool _pendingUserFlush;
    private static string _lastUserText;

    private static void ResetTranscripts() {
        _inTranscript.Clear();
        _outTranscript.Clear();
        _pendingUserFlush = false;
        _lastUserText = null;
    }

    private const int MaxSpokenChars = 400;

    private static string Spoken(string text) {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxSpokenChars ? flat : flat.Substring(0, MaxSpokenChars) + "...";
    }

    private static void FlushUtterance(System.Text.StringBuilder buf, bool isUser) {
        string text = buf.ToString().Trim();
        buf.Clear();

        if (text.Length == 0) return;

        if (isUser) {
            if (text == _lastUserText) return;
            _lastUserText = text;
        }

        Debug.Log($"[Gemini][{(isUser ? "user" : "ada")}] {Spoken(text)}");
    }

    private static async Task ReceivePump(AsyncSession s, int gen, int conn, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            LiveServerMessage response;
            try { response = await s.ReceiveAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) {
                Debug.LogError($"[Gemini] receive failed: {e.Message}");
                Debug.LogError($"[Gemini][diag] setupCompleted={_setupCompleted}; recent protocol:\n  {DumpProtocol()}");
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
            ReceiveTick(s, conn, response);
        }
    }

    private static void ReceiveTick(AsyncSession s, int conn, LiveServerMessage response) {
        HandleUsage(response.UsageMetadata, conn);
        HandleControl(s, response);
        HandleTools(s, response);
        HandleContent(response.ServerContent);
        if (_pendingUserFlush) {
            _pendingUserFlush = false;
            FlushUtterance(_inTranscript, true);
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
        int rounds = Volatile.Read(ref _toolRoundsThisTurn);
        bool usable = TrackContext(conn, promptTok, uncachedTok, rounds, out long context, out bool exact);
        string how = exact ? "exact" : $"estimated over {rounds} tool rounds";

        Debug.Log($"[Gemini][usage] conn={conn} context={context} ({how}) prompt={promptTok} response={usage.ResponseTokenCount} total={usage.TotalTokenCount} session={_sessionPromptTokens} cached={cachedTok} uncached={uncachedTok} thoughts={thoughtTok} sessionUncached={_sessionUncachedTokens} promptByModality=[{byMod}] at {DateTime.UtcNow:HH:mm:ss.fff}");

        if (exact && _lastExactContext > 0 && context + 2000 < _lastExactContext)
            Debug.Log($"[Gemini][window] context fell {_lastExactContext} -> {context}; the server trimmed it");
        if (exact) _lastExactContext = context;

        if (usable) ObserveCeiling(context);
    }

    private static void HandleControl(AsyncSession s, LiveServerMessage response) {
        if (response.SetupComplete != null) {
            _setupCompleted = true;
            Debug.Log("[Gemini][diag] setup complete");
            StateChannel.RequestSnapshot();
        }

        var resume = response.SessionResumptionUpdate;
        if (resume != null && resume.Resumable == true && !string.IsNullOrEmpty(resume.NewHandle))
            _resumeHandle = resume.NewHandle;

        if (response.GoAway != null) {
            Debug.Log($"[Gemini] server GoAway (time left: {response.GoAway.TimeLeft})");
            if (_keepAlive) BeginGoAwayReconnect();
            else _ = s.CloseAsync();
        }
    }

    private static void HandleTools(AsyncSession s, LiveServerMessage response) {
        if (response.ToolCall != null) {
            _generationActive = true;
            var calls = response.ToolCall.FunctionCalls;
            if (calls != null) {
                Interlocked.Increment(ref _toolRoundId);
                if (Interlocked.Increment(ref _toolRoundsThisTurn) == 1) _pendingUserFlush = true;
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
            string idList = ids != null ? string.Join(",", ids) : "";
            NoteProtocol($"<- toolCallCancellation ids={idList}");
            Debug.Log($"[Gemini][diag] toolCallCancellation ids={idList}");
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
            if (!string.IsNullOrEmpty(inTx.Text)) _inTranscript.Append(inTx.Text);
            if (inTx.Finished == true) FlushUtterance(_inTranscript, true);
        }

        var outTx = content.OutputTranscription;
        if (outTx != null) {
            if (!string.IsNullOrEmpty(outTx.Text)) { _outTranscript.Append(outTx.Text); _generationActive = true; }
            if (outTx.Finished == true) FlushUtterance(_outTranscript, false);
        }

        // An interruption can arrive in the same message as the turn's end, so
        // it only stops the audio here; the turn-end handling below still runs.
        bool interrupted = content.Interrupted == true;
        if (interrupted) {
            _generationActive = false;
            NoteProtocol("<- interrupted");
            Speaker.Flush();
            FlushUtterance(_outTranscript, false);
        }

        if (content.TurnComplete == true) {
            _generationActive = false;
            NoteProtocol("<- turnComplete");

            var reason = content.TurnCompleteReason;
            if (reason != null) NoteProtocol($"<- turnCompleteReason {reason}");

            FlushUtterance(_inTranscript, true);
            FlushUtterance(_outTranscript, false);
            _pendingUserFlush = false;
            _lastUserText = null;
            Interlocked.Exchange(ref _toolRoundsThisTurn, 0);

            // This runs on the receive thread, and AgentTurn's state is read and
            // written by tool calls on the main thread, so it is cleared there.
            // The queue runs in order, so the next turn's calls see it cleared.
            bool shutdown = ConsumeShutdownRequest();
            _ = MainThread.Run(() => {
                AgentTurn.Clear();
                if (!shutdown) return;
                var handButton = Scene.Assistant;
                if (handButton != null) handButton.SetGeminiActive(false, AssistantCause.Agent);
            });
        }

        if (interrupted) return;

        var parts = content.ModelTurn?.Parts;
        if (parts == null) return;

        _generationActive = true;
        for (int i = 0; i < parts.Count; i++) {
            var data = parts[i].InlineData;
            if (data?.Data != null && data.Data.Length > 0 &&
                data.MimeType != null && data.MimeType.StartsWith("audio/pcm", StringComparison.Ordinal)) {
                Speaker.Write(data.Data);
            }
        }
    }

    // Tool calls: dispatched, stalled and cancelled.

    private sealed class PendingCall {
        public string Name;
        public DateTime SentUtc;
    }

    private const int ToolStallMs = 20000;

    private static readonly ConcurrentDictionary<string, PendingCall> _pendingCalls =
        new ConcurrentDictionary<string, PendingCall>();

    private static void NoteToolDispatched(string id, string name) {
        if (string.IsNullOrEmpty(id)) return;
        _pendingCalls[id] = new PendingCall { Name = name, SentUtc = DateTime.UtcNow };
    }

    public static void NoteToolSettled(string id) {
        if (string.IsNullOrEmpty(id)) return;
        _pendingCalls.TryRemove(id, out _);
    }

    private static void ClearPendingCalls() => _pendingCalls.Clear();

    private static void CheckStalledTools() {
        if (_pendingCalls.IsEmpty) return;
        DateTime now = DateTime.UtcNow;

        foreach (var pair in _pendingCalls) {
            PendingCall call = pair.Value;
            if ((now - call.SentUtc).TotalMilliseconds < ToolStallMs) continue;
            if (!_pendingCalls.TryRemove(pair.Key, out _)) continue;

            Debug.LogError($"[Gemini][stall] no tool response was ever sent for {call.Name} " +
                           $"(id={pair.Key}) after {ToolStallMs / 1000}s; the turn cannot complete");
        }
    }

    private static readonly HashSet<string> _cancelledCalls = new HashSet<string>();
    private static readonly object _cancelGate = new object();

    private static void CancelToolCalls(IEnumerable<string> ids) {
        if (ids == null) return;
        lock (_cancelGate)
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id)) _cancelledCalls.Add(id);
    }

    public static bool ConsumeToolCallCancelled(string id) {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_cancelGate) return _cancelledCalls.Remove(id);
    }

    // Context window: token accounting and the ceiling.

    private const long ContextWindowTokens = 131072;

    private const long SafetyNetTrigger = 48000;
    private const long SafetyNetTarget = 24000;
    private const long ExhaustTokens = ContextWindowTokens / 2;
    private const long ExhaustWarnTokens = ExhaustTokens - 6000;

    private static int _usageConnection;
    private static long _contextTokens;
    private static long _sessionPromptTokens;
    private static long _sessionUncachedTokens;
    private static long _lastExactContext;

    private static volatile bool _exhausted;
    private static volatile bool _exhaustWarned;

    private static void ResetWindow() {
        StateChannel.ClearPending();
        _usageConnection = 0;
        _contextTokens = 0;
        _sessionPromptTokens = 0;
        _sessionUncachedTokens = 0;
        _lastExactContext = 0;
        Interlocked.Exchange(ref _toolRoundsThisTurn, 0);
    }

    private static bool TrackContext(int conn, long promptTokens, long uncachedTokens, int rounds, out long context, out bool exact) {
        if (conn != _usageConnection) {
            _usageConnection = conn;
            if (!_resumedConnection) _contextTokens = 0;
        }

        if (promptTokens > 0) _sessionPromptTokens += promptTokens;
        if (uncachedTokens > 0) _sessionUncachedTokens += uncachedTokens;

        exact = rounds <= 0;
        context = _contextTokens;
        if (promptTokens <= 0) return false;

        long reading = promptTokens / (rounds + 1);
        if (exact || reading > _contextTokens) _contextTokens = reading;

        context = _contextTokens;
        return true;
    }

    private static void ObserveCeiling(long context) {
        if (context >= ExhaustTokens) { MarkExhausted(context); return; }
        if (context < ExhaustWarnTokens || _exhaustWarned) return;

        _exhaustWarned = true;
        Debug.LogWarning($"[Gemini][window] approaching context ceiling: {context} of {ExhaustTokens}");
        Interlocked.Exchange(ref _pendingClosingNotice, 1);
    }

    private static void ClearExhaustion() {
        _exhausted = false;
        _exhaustWarned = false;
    }

    private static void MarkExhausted(long context) {
        if (_exhausted) return;
        _exhausted = true;
        Debug.LogWarning($"[Gemini][window] context ceiling reached: {context} >= {ExhaustTokens}");
        _resumeHandle = null;
        RetireConnection();
        var s = _liveSession;
        if (s != null) { try { _ = s.CloseAsync(); } catch { } }
    }

    // Protocol log.

    public static void NoteProtocol(string desc) {
        _protocolLog.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {desc}");
        while (_protocolLog.Count > 16 && _protocolLog.TryDequeue(out _)) { }
    }

    private static string DumpProtocol() => string.Join("\n  ", _protocolLog.ToArray());
}
