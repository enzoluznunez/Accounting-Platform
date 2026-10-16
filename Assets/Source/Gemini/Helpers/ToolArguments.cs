using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Google.GenAI.Types;
using GType = Google.GenAI.Types.Type;

[AttributeUsage(AttributeTargets.Field)]
public sealed class DocAttribute : Attribute {
    public readonly string Text;
    public DocAttribute(string text) { Text = text; }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class OptionalAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field)]
public sealed class ValuesAttribute : Attribute {
    public readonly string[] Allowed;
    public ValuesAttribute(params string[] allowed) { Allowed = allowed; }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class LimitsAttribute : Attribute {
    public readonly double Min;
    public readonly double Max;
    public LimitsAttribute(double min, double max) { Min = min; Max = max; }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class DefaultsToAttribute : Attribute {
    public readonly object Value;
    public DefaultsToAttribute(object value) { Value = value; }
}

public static class ToolArguments {

    private static readonly Dictionary<System.Type, Schema> cache = new Dictionary<System.Type, Schema>();

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ClearCache() => cache.Clear();

    public static Schema Schema(System.Type args) {
        if (args == null) return null;
        if (cache.TryGetValue(args, out var cached)) return cached;
        var built = Build(args);
        cache[args] = built;
        return built;
    }

    private static Schema Build(System.Type args) {
        var properties = new Dictionary<string, Schema>();
        var required = new List<string>();

        foreach (FieldInfo f in Fields(args)) {
            System.Type t = Underlying(f.FieldType);
            Schema schema;
            if (IsComplex(t)) schema = Build(t);
            else {
                schema = new Schema { Type = JsonType(t) };
                if (t.IsArray || IsList(t)) {
                    System.Type el = ElementType(t);
                    schema.Items = IsComplex(el) ? Build(el) : new Schema { Type = JsonType(el) };
                }
            }

            var values = f.GetCustomAttribute<ValuesAttribute>();
            if (values != null) schema.Enum = new List<string>(values.Allowed);
            else if (t.IsEnum) schema.Enum = EnumNames(t);

            var limits = f.GetCustomAttribute<LimitsAttribute>();
            if (limits != null) { schema.Minimum = limits.Min; schema.Maximum = limits.Max; }

            var fallback = f.GetCustomAttribute<DefaultsToAttribute>();
            if (fallback != null) schema.Default = fallback.Value;

            var doc = f.GetCustomAttribute<DocAttribute>();
            if (doc != null) schema.Description = doc.Text;
            properties[f.Name] = schema;
            if (f.GetCustomAttribute<OptionalAttribute>() == null) required.Add(f.Name);
        }

        return new Schema {
            Type = GType.Object,
            Properties = properties,
            Required = required.Count > 0 ? required : null
        };
    }

    public static object Bind(System.Type args, Dictionary<string, object> values, out string error) {
        error = null;
        object instance = Activator.CreateInstance(args);

        foreach (FieldInfo f in Fields(args)) {
            object value = null;
            bool present = values != null && values.TryGetValue(f.Name, out value) && value != null;

            if (!present) {
                if (f.GetCustomAttribute<OptionalAttribute>() == null) {
                    error = $"Provide '{f.Name}'.";
                    return null;
                }
                continue;
            }

            object bound;
            try {
                bound = Convert(value, Underlying(f.FieldType));
            }
            catch (Exception) {
                error = Rejected(f);
                return null;
            }

            var allowed = f.GetCustomAttribute<ValuesAttribute>();
            if (allowed != null) {
                if (!TryCanonical(allowed, bound as string, out string canonical)) {
                    error = Rejected(f);
                    return null;
                }
                bound = canonical;
            }
            f.SetValue(instance, bound);
        }
        return instance;
    }

    private static bool TryCanonical(ValuesAttribute values, string bound, out string canonical) {
        canonical = null;
        if (bound == null) return false;
        string trimmed = bound.Trim();
        for (int i = 0; i < values.Allowed.Length; i++)
            if (string.Equals(values.Allowed[i], trimmed, StringComparison.OrdinalIgnoreCase)) {
                canonical = values.Allowed[i];
                return true;
            }
        return false;
    }

    private static string Rejected(FieldInfo f) {
        System.Type t = Underlying(f.FieldType);
        var values = f.GetCustomAttribute<ValuesAttribute>();
        List<string> allowed = values != null ? new List<string>(values.Allowed)
                             : t.IsEnum ? EnumNames(t)
                             : null;
        if (allowed != null)
            return $"'{f.Name}' must be one of: {string.Join(", ", allowed)}.";

        if (IsComplex(t)) {
            var names = new List<string>();
            foreach (FieldInfo nested in Fields(t)) names.Add(nested.Name);
            return $"'{f.Name}' must be an object with fields: {string.Join(", ", names)}. " +
                   "It is not a string; send it as a nested object.";
        }

        return $"'{f.Name}' is not a valid {JsonType(t).ToString().ToLowerInvariant()}.";
    }

    private static IEnumerable<FieldInfo> Fields(System.Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Instance);

    private static System.Type Underlying(System.Type t) => Nullable.GetUnderlyingType(t) ?? t;

    private static bool IsList(System.Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

    private static System.Type ElementType(System.Type t) =>
        t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0];

    private static List<string> EnumNames(System.Type t) {
        string[] names = System.Enum.GetNames(t);
        var list = new List<string>(names.Length);
        for (int i = 0; i < names.Length; i++) list.Add(names[i].ToLowerInvariant());
        return list;
    }

    private static GType JsonType(System.Type t) {
        if (t.IsEnum) return GType.String;
        if (t == typeof(int) || t == typeof(long)) return GType.Integer;
        if (t == typeof(float) || t == typeof(double)) return GType.Number;
        if (t == typeof(bool)) return GType.Boolean;
        if (t == typeof(string)) return GType.String;
        if (t.IsArray || IsList(t)) return GType.Array;
        return GType.String;
    }

    private static bool IsComplex(System.Type t) =>
        t.IsClass && t != typeof(string) && !t.IsArray && !IsList(t);

    private static Dictionary<string, object> AsMap(object value) {
        var map = new Dictionary<string, object>();
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object) {
            foreach (var prop in je.EnumerateObject()) map[prop.Name] = prop.Value;
        }
        else if (value is Dictionary<string, object> d) {
            foreach (var kv in d) map[kv.Key] = kv.Value;
        }
        return map;
    }

    private static object Convert(object value, System.Type target) {
        if (target.IsArray || IsList(target)) return ConvertList(value, target);

        if (IsComplex(target)) {
            bool isObject = (value is JsonElement obj && obj.ValueKind == JsonValueKind.Object)
                            || value is Dictionary<string, object>;
            if (!isObject) throw new InvalidOperationException("not an object");
            object bound = Bind(target, AsMap(value), out string error);
            if (bound == null) throw new InvalidOperationException(error ?? "bad object");
            return bound;
        }

        if (target.IsEnum) {
            string name = value is JsonElement e
                ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                : value.ToString();
            return System.Enum.Parse(target, name.Trim(), true);
        }

        if (value is JsonElement je) {
            if (target == typeof(string))
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
            if (target == typeof(int))
                return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : int.Parse(je.GetString());
            if (target == typeof(long))
                return je.ValueKind == JsonValueKind.Number ? je.GetInt64() : long.Parse(je.GetString());
            if (target == typeof(float))
                return je.ValueKind == JsonValueKind.Number
                    ? (float)je.GetDouble()
                    : float.Parse(je.GetString(), System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(double))
                return je.ValueKind == JsonValueKind.Number
                    ? je.GetDouble()
                    : double.Parse(je.GetString(), System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(bool)) {
                if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False) return je.GetBoolean();
                if (je.ValueKind == JsonValueKind.Number) return je.GetInt32() != 0;
                return bool.Parse(je.GetString());
            }
        }

        if (target == typeof(string)) return value.ToString();
        return System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object ConvertList(object value, System.Type target) {
        System.Type element = ElementType(target);
        var items = new List<object>();

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array) {
            foreach (var el in je.EnumerateArray()) items.Add(Convert(el, element));
        }
        else if (value is System.Collections.IEnumerable en && !(value is string)) {
            foreach (var el in en) items.Add(Convert(el, element));
        }
        else {
            items.Add(Convert(value, element));
        }

        if (target.IsArray) {
            Array array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(items[i], i);
            return array;
        }

        var list = (System.Collections.IList)Activator.CreateInstance(target);
        for (int i = 0; i < items.Count; i++) list.Add(items[i]);
        return list;
    }
}
