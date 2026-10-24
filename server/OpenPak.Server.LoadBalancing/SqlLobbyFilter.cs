using System.Globalization;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// The WHERE-clause language a Photon SQL lobby (LobbyType 2) matches rooms with: columns
/// <c>C0</c>..<c>C9</c> are room properties of those names, compared with integer or string
/// literals, joined by AND / OR / NOT and parentheses. Unrailed! sends filters such as
/// <c>C0 &lt; 3 AND C1 = 2 AND (C3 = 0 OR C3 = 3 )</c> and <c>AND NOT (C2 = "x")</c>.
/// A column the room does not have is NULL, and NULL compares false either way, as in SQL.
/// A filter that does not parse matches nothing, so a bad filter never puts a player in a
/// stranger's room.
/// </summary>
public static class SqlLobbyFilter
{
    public static bool TryParse(string filter, out Func<IReadOnlyDictionary<object, object>, bool> match)
    {
        try
        {
            var p = new Parser(Tokenize(filter));
            var e = p.Or();
            if (!p.AtEnd) throw new FormatException("trailing tokens");
            match = props => e(props) == true;
            return true;
        }
        catch (FormatException)
        {
            match = _ => false;
            return false;
        }
    }

    public static bool Matches(string filter, IReadOnlyDictionary<object, object> props) =>
        TryParse(filter, out var m) && m(props);

    // An expression yields true, false or null (unknown), SQL's three-valued logic.
    private delegate bool? Expr(IReadOnlyDictionary<object, object> props);

    private enum Kind { Word, Number, String, Op, Open, Close }
    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new(Kind.Open, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new(Kind.Close, ")")); i++; continue; }
            if (c is '\'' or '"')
            {
                int end = s.IndexOf(c, i + 1);
                if (end < 0) throw new FormatException("unterminated string");
                tokens.Add(new(Kind.String, s[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            if (c is '<' or '>' or '=' or '!')
            {
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "<=" or ">=" or "<>" or "!=") { tokens.Add(new(Kind.Op, two)); i += 2; continue; }
                if (c == '!') throw new FormatException("bare !");
                tokens.Add(new(Kind.Op, c.ToString())); i++; continue;
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int start = i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                tokens.Add(new(Kind.Number, s[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new(Kind.Word, s[start..i]));
                continue;
            }
            throw new FormatException($"unexpected '{c}'");
        }
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;
        public bool AtEnd => _i >= tokens.Count;

        private bool Keyword(string k)
        {
            if (!AtEnd && tokens[_i].Kind == Kind.Word && tokens[_i].Text.Equals(k, StringComparison.OrdinalIgnoreCase))
            {
                _i++;
                return true;
            }
            return false;
        }

        private Token Next() => AtEnd ? throw new FormatException("unexpected end") : tokens[_i++];

        public Expr Or()
        {
            var left = And();
            while (Keyword("OR"))
            {
                var l = left; var r = And();
                left = p => { bool? a = l(p); if (a == true) return true; bool? b = r(p); if (b == true) return true; return a is null || b is null ? null : false; };
            }
            return left;
        }

        private Expr And()
        {
            var left = Not();
            while (Keyword("AND"))
            {
                var l = left; var r = Not();
                left = p => { bool? a = l(p); if (a == false) return false; bool? b = r(p); if (b == false) return false; return a is null || b is null ? null : true; };
            }
            return left;
        }

        private Expr Not()
        {
            if (Keyword("NOT")) { var e = Not(); return p => !e(p); }
            return Primary();
        }

        private Expr Primary()
        {
            if (!AtEnd && tokens[_i].Kind == Kind.Open)
            {
                _i++;
                var e = Or();
                if (Next().Kind != Kind.Close) throw new FormatException("missing )");
                return e;
            }
            var left = Operand();
            var op = Next();
            if (op.Kind != Kind.Op) throw new FormatException("expected a comparison");
            var right = Operand();
            return p => Compare(left(p), op.Text, right(p));
        }

        private Func<IReadOnlyDictionary<object, object>, object?> Operand()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Kind.Number:
                    var n = double.Parse(t.Text, CultureInfo.InvariantCulture);
                    return _ => n;
                case Kind.String:
                    return _ => t.Text;
                case Kind.Word:
                    return p => p.TryGetValue(t.Text, out var v) ? v : null;
                default:
                    throw new FormatException("expected a value");
            }
        }
    }

    private static bool? Compare(object? a, string op, object? b)
    {
        if (a is null || b is null) return null;
        int c;
        if (AsNumber(a) is double x && AsNumber(b) is double y) c = x.CompareTo(y);
        else c = string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        return op switch
        {
            "=" => c == 0,
            "<>" or "!=" => c != 0,
            "<" => c < 0,
            "<=" => c <= 0,
            ">" => c > 0,
            ">=" => c >= 0,
            _ => null,
        };
    }

    private static double? AsNumber(object v) => v switch
    {
        bool b => b ? 1 : 0,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => null,
    };
}
