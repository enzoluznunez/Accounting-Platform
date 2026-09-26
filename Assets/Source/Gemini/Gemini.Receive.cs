using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using UnityEngine;

public static partial class Gemini {

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
}
