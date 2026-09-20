using System.Text;

namespace WingetNudge.Core.Tests.Support;

/// <summary>The repository this suite runs from, found by walking up from the test binary.</summary>
/// <remarks>
/// Three markers rather than one, so a stray copy of any single file cannot pass for the root. The
/// walk also works inside a git worktree, where the markers sit at the worktree root.
/// </remarks>
public static class Repository
{
    /// <summary>The files that together identify the root.</summary>
    public static readonly string[] Markers = ["cake.cs", "CONTRIBUTING.md", "WingetNudge.slnx"];

    /// <summary>The repository root.</summary>
    public static string Root => Find(AppContext.BaseDirectory);

    /// <summary>The first directory at or above a starting point that holds every marker.</summary>
    /// <param name="start">The directory the walk begins in.</param>
    /// <returns>The full path of the directory.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// No directory on the walk holds every marker.
    /// </exception>
    public static string Find(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null)
        {
            if (Markers.All(marker => File.Exists(Path.Combine(directory.FullName, marker))))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No directory at or above {start} holds {string.Join(", ", Markers)}."
        );
    }

    /// <summary>Reads one file of the repository.</summary>
    /// <param name="relativePath">The path from the root, with forward slashes.</param>
    /// <returns>The file, with line endings left as written.</returns>
    public static string Read(string relativePath) =>
        File.ReadAllText(
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
}

/// <summary>Walks source text past whitespace, comments, literals and balanced brackets.</summary>
/// <remarks>
/// C# and JavaScript share enough lexical structure for one reader to cover both. A Cake script and
/// a commitlint config each write what this suite reads as plain literals, which a reader this size
/// takes exactly. Anything richer is reported by the caller rather than guessed at, so a
/// declaration the reader cannot take never shrinks the set it returns.
/// </remarks>
public static class SourceText
{
    /// <summary>Advances past whitespace and comments.</summary>
    /// <param name="text">The source.</param>
    /// <param name="index">Where to start.</param>
    /// <returns>The index of the next character that is neither whitespace nor a comment.</returns>
    public static int SkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] != '/' || index + 1 >= text.Length)
            {
                break;
            }

            if (text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (text[index + 1] != '*')
            {
                break;
            }

            int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = close < 0 ? text.Length : close + 2;
        }

        return index;
    }

    /// <summary>Whether a literal starts at an index, prefixes included.</summary>
    /// <param name="text">The source.</param>
    /// <param name="index">Where to look.</param>
    /// <returns>True when a string, character or template literal starts there.</returns>
    public static bool IsLiteralStart(string text, int index)
    {
        while (index < text.Length && (text[index] == '$' || text[index] == '@'))
        {
            index++;
        }

        return index < text.Length && text[index] is '"' or '\'' or '`';
    }

    /// <summary>Advances past one literal.</summary>
    /// <param name="text">The source.</param>
    /// <param name="index">The first character of the literal, prefixes included.</param>
    /// <returns>The index after the literal.</returns>
    public static int SkipLiteral(string text, int index)
    {
        bool interpolated = false;
        bool verbatim = false;
        while (index < text.Length && (text[index] == '$' || text[index] == '@'))
        {
            interpolated |= text[index] == '$';
            verbatim |= text[index] == '@';
            index++;
        }

        if (index >= text.Length)
        {
            return index;
        }

        if (text[index] is '\'' or '`')
        {
            return SkipDelimited(text, index, text[index], text[index] == '`');
        }

        if (text[index] != '"')
        {
            return index;
        }

        int quotes = 0;
        while (index + quotes < text.Length && text[index + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            return SkipRaw(text, index, quotes);
        }

        return verbatim ? SkipVerbatim(text, index) : SkipDelimited(text, index, '"', interpolated);
    }

    /// <summary>Advances past a balanced bracket group.</summary>
    /// <param name="text">The source.</param>
    /// <param name="index">The opening bracket.</param>
    /// <returns>The index after the matching closing bracket.</returns>
    public static int SkipBalanced(string text, int index)
    {
        int depth = 0;
        while (index < text.Length)
        {
            index = SkipTrivia(text, index);
            if (index >= text.Length)
            {
                break;
            }

            if (IsLiteralStart(text, index))
            {
                index = SkipLiteral(text, index);
                continue;
            }

            if (text[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (text[index] is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
    }

    /// <summary>Finds every call of a named function that is written in code.</summary>
    /// <remarks>
    /// A name inside a comment or a literal is text about a call rather than a call, and a member
    /// access of the same name belongs to another object.
    /// </remarks>
    /// <param name="text">The source.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The offset of each name, in the order they appear.</returns>
    public static IReadOnlyList<int> InvocationOffsets(string text, string name)
    {
        List<int> offsets = [];
        int index = 0;
        while (index < text.Length)
        {
            int skipped = SkipTrivia(text, index);
            if (skipped != index)
            {
                index = skipped;
                continue;
            }

            if (IsLiteralStart(text, index))
            {
                index = SkipLiteral(text, index);
                continue;
            }

            if (!IsWordStart(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && IsWordPart(text[index]))
            {
                index++;
            }

            bool member = start > 0 && text[start - 1] == '.';
            int open = SkipTrivia(text, index);
            bool named =
                index - start == name.Length
                && string.CompareOrdinal(text, start, name, 0, name.Length) == 0;
            if (!member && named && open < text.Length && text[open] == '(')
            {
                offsets.Add(start);
            }
        }

        return offsets;
    }

    /// <summary>Reads a plain literal, resolving the escapes a one-line string can carry.</summary>
    /// <param name="text">The source.</param>
    /// <param name="start">The opening quote.</param>
    /// <param name="end">The index after the closing quote.</param>
    /// <returns>The text between the quotes, unescaped.</returns>
    public static string LiteralValue(string text, int start, int end)
    {
        string body = text[(start + 1)..(end - 1)];
        StringBuilder value = new(body.Length);
        for (int index = 0; index < body.Length; index++)
        {
            if (body[index] != '\\' || index + 1 >= body.Length)
            {
                value.Append(body[index]);
                continue;
            }

            index++;
            value.Append(
                body[index] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    char other => other,
                }
            );
        }

        return value.ToString();
    }

    private static bool IsWordStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsWordPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static int SkipDelimited(string text, int index, char quote, bool holes)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
            {
                return index + 1;
            }

            if (holes && quote == '"' && text[index] == '{')
            {
                index =
                    index + 1 < text.Length && text[index + 1] == '{'
                        ? index + 2
                        : SkipHole(text, index + 1);
                continue;
            }

            if (
                holes
                && quote == '`'
                && text[index] == '$'
                && index + 1 < text.Length
                && text[index + 1] == '{'
            )
            {
                index = SkipHole(text, index + 2);
                continue;
            }

            index++;
        }

        return index;
    }

    private static int SkipVerbatim(string text, int index)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] != '"')
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '"')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    private static int SkipRaw(string text, int index, int quotes)
    {
        index += quotes;
        while (index < text.Length)
        {
            if (text[index] != '"')
            {
                index++;
                continue;
            }

            int run = 0;
            while (index + run < text.Length && text[index + run] == '"')
            {
                run++;
            }

            if (run >= quotes)
            {
                return index + run;
            }

            index += run;
        }

        return index;
    }

    private static int SkipHole(string text, int index)
    {
        int depth = 1;
        while (index < text.Length)
        {
            if (IsLiteralStart(text, index))
            {
                index = SkipLiteral(text, index);
                continue;
            }

            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
    }
}
