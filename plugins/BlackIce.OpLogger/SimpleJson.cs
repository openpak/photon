using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BlackIce.OpLogger;

/// <summary>Tiny reflection-based JSON writer — enough for flat log records (no external deps in the plugin).</summary>
internal static class SimpleJson
{
    public static string Serialize(object o)
    {
        var sb = new StringBuilder();
        WriteValue(sb, o);
        return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case float or double:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case IDictionary dict: WriteDict(sb, dict); break;
            default: WriteObject(sb, v); break;
        }
    }

    static void WriteDict(StringBuilder sb, IDictionary d)
    {
        sb.Append('{');
        bool first = true;
        foreach (DictionaryEntry e in d)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, e.Key?.ToString() ?? "null");
            sb.Append(':');
            WriteValue(sb, e.Value);
        }
        sb.Append('}');
    }

    static void WriteObject(StringBuilder sb, object v)
    {
        sb.Append('{');
        bool first = true;
        foreach (PropertyInfo p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, p.Name);
            sb.Append(':');
            WriteValue(sb, p.GetValue(v));
        }
        sb.Append('}');
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
