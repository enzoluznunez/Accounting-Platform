using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using UnityEngine;

public static partial class Gemini {

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
}
