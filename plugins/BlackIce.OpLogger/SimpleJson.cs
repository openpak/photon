using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BlackIce.OpLogger;

/// <summary>
/// Tiny reflection-based JSON writer — no external deps in the plugin. Recurses through dictionaries,
/// collections, and object graphs so a captured Photon event/RPC is logged in full: nested RPC payloads,
/// argument arrays, and (critically for protocol recon) the raw bytes of custom types like DamagePacket,
/// emitted as a <c>"hex:…"</c> string. Guards keep one capture line from exploding or throwing: a depth
/// cap, Unity-object pruning (a scene reference is logged as its type+name, not its whole graph), and a
/// per-member try/catch so a single bad getter can't kill the record.
/// </summary>
internal static class SimpleJson
{
    private const int MaxDepth = 8;

    public static string Serialize(object o)
    {
        var sb = new StringBuilder();
        WriteValue(sb, o, 0);
        return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object? v, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case float or double or decimal:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case byte[] bytes: WriteString(sb, "hex:" + ToHex(bytes)); break;   // raw custom-type payloads
            case IDictionary dict: WriteDict(sb, dict, depth); break;
            case IEnumerable seq: WriteArray(sb, seq, depth); break;            // object[] RPC args, lists
            default: WriteObject(sb, v, depth); break;
        }
    }

    static void WriteDict(StringBuilder sb, IDictionary d, int depth)
    {
        if (depth >= MaxDepth) { WriteString(sb, "<max-depth>"); return; }
        sb.Append('{');
        bool first = true;
        foreach (DictionaryEntry e in d)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, e.Key?.ToString() ?? "null");
            sb.Append(':');
            WriteValue(sb, e.Value, depth + 1);
        }
        sb.Append('}');
    }

    static void WriteArray(StringBuilder sb, IEnumerable seq, int depth)
    {
        if (depth >= MaxDepth) { WriteString(sb, "<max-depth>"); return; }
        sb.Append('[');
        bool first = true;
        foreach (var item in seq)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteValue(sb, item, depth + 1);
        }
        sb.Append(']');
    }

    static void WriteObject(StringBuilder sb, object v, int depth)
    {
        var type = v.GetType();

        // Don't reflect into Unity scene objects — that graph is huge and self-referential. Log identity only.
        if (IsUnityObject(type))
        {
            sb.Append("{\"__unity\":");
            WriteString(sb, type.FullName ?? type.Name);
            sb.Append(",\"name\":");
            WriteString(sb, SafeToString(v));
            sb.Append('}');
            return;
        }

        if (depth >= MaxDepth) { WriteString(sb, SafeToString(v)); return; }

        // A custom type (DamagePacket etc.) deserializes to a struct/class — capture its fields AND
        // properties by name, plus a __type tag so the layout is self-describing in the capture.
        sb.Append("{\"__type\":");
        WriteString(sb, type.FullName ?? type.Name);
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sb.Append(',');
            WriteString(sb, f.Name);
            sb.Append(':');
            WriteMember(sb, () => f.GetValue(v), depth);
        }
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;   // skip indexers
            sb.Append(',');
            WriteString(sb, p.Name);
            sb.Append(':');
            WriteMember(sb, () => p.GetValue(v), depth);
        }
        sb.Append('}');
    }

    /// <summary>Serialize one member value, turning any getter exception into a JSON error string instead
    /// of aborting the whole capture line.</summary>
    static void WriteMember(StringBuilder sb, Func<object?> getter, int depth)
    {
        object? value;
        try { value = getter(); }
        catch (Exception ex) { WriteString(sb, "<err:" + ex.GetType().Name + ">"); return; }
        WriteValue(sb, value, depth + 1);
    }

    static bool IsUnityObject(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.FullName == "UnityEngine.Object") return true;
        return false;
    }

    static string SafeToString(object v)
    {
        try { return v.ToString() ?? "null"; }
        catch (Exception ex) { return "<err:" + ex.GetType().Name + ">"; }
    }

    static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString()
            });
        sb.Append('"');
    }
}
