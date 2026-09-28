using System.Text;

namespace PgPort.Services;

/// <summary>
/// Minimal PostgreSQL lexer: enough to split statements and find the leading keyword while
/// correctly skipping string literals, quoted identifiers, comments and dollar-quoted bodies.
/// </summary>
public static class SqlText
{
    public sealed record Analysis(string Statement, string? LeadingKeyword, int StatementCount);

    /// <summary>
    /// Returns the trimmed single statement (trailing semicolons removed), its first keyword
    /// (lower-case) and how many non-empty statements the input contained.
    /// </summary>
    public static Analysis Analyze(string sql)
    {
        var statements = Split(sql);
        var first = statements.Count > 0 ? statements[0] : string.Empty;
        return new Analysis(first, LeadingKeyword(first), statements.Count);
    }

    /// <summary>
    /// Transaction-control statements could end the READ ONLY transaction the query runs in,
    /// so they are rejected outright.
    /// </summary>
    public static bool IsTransactionControl(string? leadingKeyword) =>
        leadingKeyword is "begin" or "start" or "commit" or "end" or "rollback" or "abort"
            or "savepoint" or "release" or "prepare";

    /// <summary>Statements that can be safely wrapped as a subquery for paging.</summary>
    public static bool IsWrappable(string? leadingKeyword) =>
        leadingKeyword is "select" or "with" or "values" or "table";

    public static List<string> Split(string sql)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                current.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = SkipBlockComment(sql, i);
                current.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '\'')
            {
                var escapeStyle = i > 0 && (sql[i - 1] == 'E' || sql[i - 1] == 'e') && !IsIdentChar(i > 1 ? sql[i - 2] : ' ');
                var end = SkipQuoted(sql, i, '\'', escapeStyle);
                current.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '"')
            {
                var end = SkipQuoted(sql, i, '"', backslashEscapes: false);
                current.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '$' && TryReadDollarTag(sql, i, out var tag))
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                var end = close < 0 ? sql.Length : close + tag.Length;
                current.Append(sql, i, end - i);
                i = end;
            }
            else if (c == ';')
            {
                Flush();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        Flush();
        return result;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (HasCode(text)) result.Add(text);
            current.Clear();
        }
    }

    /// <summary>True if the text contains anything other than whitespace and comments.</summary>
    private static bool HasCode(string text) => StripLeadingComments(text).Length > 0;

    public static string? LeadingKeyword(string statement)
    {
        var s = StripLeadingComments(statement);
        var len = 0;
        while (len < s.Length && char.IsLetter(s[len])) len++;
        return len == 0 ? null : s[..len].ToLowerInvariant();
    }

    private static string StripLeadingComments(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == '-')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end + 1;
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i = SkipBlockComment(s, i);
                continue;
            }
            break;
        }
        return s[i..];
    }

    // Postgres block comments nest.
    private static int SkipBlockComment(string s, int start)
    {
        var depth = 0;
        var i = start;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') { depth++; i += 2; }
            else if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '/')
            {
                depth--; i += 2;
                if (depth == 0) return i;
            }
            else i++;
        }
        return s.Length;
    }

    private static int SkipQuoted(string s, int start, char quote, bool backslashEscapes)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (backslashEscapes && s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; } // doubled quote
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static bool TryReadDollarTag(string s, int start, out string tag)
    {
        tag = string.Empty;
        // A '$' preceded by an identifier char is part of an identifier (or $1 param), not a tag.
        if (start > 0 && IsIdentChar(s[start - 1])) return false;

        var i = start + 1;
        if (i < s.Length && char.IsDigit(s[i])) return false; // positional parameter like $1
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        if (i < s.Length && s[i] == '$')
        {
            tag = s.Substring(start, i - start + 1);
            return true;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
