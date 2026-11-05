using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using UnityEngine;

public abstract class Function {
    public abstract FunctionDeclaration Declaration { get; }
    public string Name { get; private set; }
    protected abstract Task<Dictionary<string, object>> Execute(Dictionary<string, object> args);

    public virtual bool IsAvailable() => true;

    public static async Task Run(AsyncSession session, FunctionCall call) {
        Dictionary<string, object> result;
        if (!_registry.TryGetValue(call.Name, out var tool)) {
            Debug.LogWarning($"[Function] Unknown tool: {call.Name}");
            result = new Dictionary<string, object> {
                { "error", $"There is no tool called '{call.Name}'." },
                { "tools", new List<object>(_registry.Keys) }
            };
        }
        else {
            try {
                result = await tool.Execute(call.Args).ConfigureAwait(false);
            }
            catch (Exception e) {
                Debug.LogError($"[Function] {call.Name} failed: {e}");
                result = new Dictionary<string, object> { { "error", e.Message } };
            }
        }

        result = Sanitize(result) as Dictionary<string, object> ?? result;

        if (Gemini.ConsumeToolCallCancelled(call.Id)) {
            Debug.LogWarning($"[Function] {call.Name} id={call.Id} was cancelled by the server (turn interrupted); suppressing its tool response");
            Gemini.NoteToolSettled(call.Id);
            return;
        }

        try {
            await Respond(session, call, result).ConfigureAwait(false);
            Gemini.NoteToolSettled(call.Id);
        }
        catch (Exception e) {
            Debug.LogError($"[Function] SendToolResponse failed: {e}");
            if (Gemini.Status != GeminiStatus.Live) return;
            try {
                Gemini.NoteToolSettled(call.Id);
                await Respond(session, call, new Dictionary<string, object> {
                    { "error", "The tool result could not be delivered. The action may still have applied; " +
                               "verify with DescribeSheet before retrying." }
                }).ConfigureAwait(false);
            }
            catch (Exception e2) {
                Debug.LogError($"[Function] SendToolResponse fallback failed: {e2}");
            }
        }
    }

    private static Task Respond(AsyncSession session, FunctionCall call, Dictionary<string, object> result) {
        try {
            string head = BriefJson(result, 400, out long totalBytes);
            Gemini.NoteProtocol($"toolResponse {call.Name} id={call.Id} {totalBytes}B :: {head}");
        }
        catch (Exception e) { Gemini.NoteProtocol($"toolResponse {call.Name} SERIALIZE-FAIL {e.Message}"); }
        return session.SendToolResponseAsync(new LiveSendToolResponseParameters {
            FunctionResponses = new List<FunctionResponse> {
                new FunctionResponse {
                    Id = call.Id,
                    Name = call.Name,
                    Response = result
                }
            }
        });
    }

    private static object Sanitize(object value) {
        switch (value) {
            case double d when double.IsNaN(d) || double.IsInfinity(d): return null;
            case float f when float.IsNaN(f) || float.IsInfinity(f): return null;
            case Dictionary<string, object> dict:
                foreach (var key in dict.Keys.ToList()) dict[key] = Sanitize(dict[key]);
                return dict;
            case List<object> list:
                for (int i = 0; i < list.Count; i++) list[i] = Sanitize(list[i]);
                return list;
            default: return value;
        }
    }

    protected static string BriefJson(object value, int maxBytes, out long totalBytes) {
        var sink = new TruncatingStream(maxBytes);
        using (var writer = new Utf8JsonWriter(sink))
            JsonSerializer.Serialize(writer, value);
        totalBytes = sink.Total;
        return sink.Head;
    }

    // Keeps only the first maxBytes of what is written but counts everything, so a
    // large result is never materialised as a full string just to be truncated.
    private sealed class TruncatingStream : System.IO.Stream {
        private readonly byte[] _head;
        private int _headLen;
        private long _total;

        public TruncatingStream(int maxBytes) { _head = new byte[maxBytes]; }

        public long Total => _total;
        public string Head => System.Text.Encoding.UTF8.GetString(_head, 0, _headLen);

        public override void Write(byte[] buffer, int offset, int count) {
            int keep = Math.Min(count, _head.Length - _headLen);
            if (keep > 0) {
                Buffer.BlockCopy(buffer, offset, _head, _headLen, keep);
                _headLen += keep;
            }
            _total += count;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _total;
        public override long Position { get => _total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    static Dictionary<string, Function> _registry;
    public static IReadOnlyDictionary<string, Function> Registry => _registry;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init() {
        _registry = new Dictionary<string, Function>();
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Function)) && !t.IsAbstract);
        foreach (var type in types) {
            if (type.IsGenericTypeDefinition) continue;

            Function instance;
            try { instance = (Function)Activator.CreateInstance(type); }
            catch (Exception e) {
                Debug.LogError($"[Function] {type.Name} could not be constructed and will not be registered: {e.Message}");
                continue;
            }

            string name = instance.Declaration?.Name;
            if (string.IsNullOrEmpty(name)) {
                Debug.LogError($"[Function] {type.Name} has no Declaration.Name and will not be registered.");
                continue;
            }
            if (_registry.ContainsKey(name))
                Debug.LogError($"[Function] duplicate tool name '{name}'; {type.Name} overwrites the earlier registration.");

            instance.Name = name;
            _registry[name] = instance;
        }
        Debug.Log($"[Function] registered {_registry.Count} tools");
    }
}
