using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WingetNudge.Core.Tests.Support;

/// <summary>One commitlint rule, as the config writes it.</summary>
/// <param name="Name">The rule name.</param>
/// <param name="Level">0 disables the rule, 1 warns and 2 refuses the commit.</param>
/// <param name="Applicable">Whether the rule applies always or never.</param>
/// <param name="Argument">The third element, as written, or an empty string when there is none.</param>
public sealed record CommitlintRule(string Name, int Level, string Applicable, string Argument);

/// <summary>A commitlint config read as text.</summary>
/// <remarks>
/// The scope list is the point. A config that writes its scopes inline restates the data file, and
/// a config that maps a property the data file does not carry leaves every scope undefined without
/// a word, so both come back as a diagnostic.
/// </remarks>
public sealed partial class CommitlintConfig
{
    private CommitlintConfig(
        IReadOnlyDictionary<string, CommitlintRule> rules,
        string scopeBinding,
        string scopeSource,
        string scopeProperty,
        IReadOnlyList<string> diagnostics
    )
    {
        Rules = rules;
        ScopeBinding = scopeBinding;
        ScopeSource = scopeSource;
        ScopeProperty = scopeProperty;
        Diagnostics = diagnostics;
    }

    /// <summary>Every rule the config sets, by name.</summary>
    public IReadOnlyDictionary<string, CommitlintRule> Rules { get; }

    /// <summary>The name the config binds the scope list to.</summary>
    public string ScopeBinding { get; }

    /// <summary>The file the scope list is read from.</summary>
    public string ScopeSource { get; }

    /// <summary>The property of each entry the config takes as the scope.</summary>
    public string ScopeProperty { get; }

    /// <summary>What the reader could not take.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Reads a commitlint config.</summary>
    /// <param name="source">The config text.</param>
    /// <returns>The rules and the scope wiring, with a diagnostic for anything unreadable.</returns>
    public static CommitlintConfig Parse(string source)
    {
        List<string> diagnostics = [];
        Dictionary<string, CommitlintRule> rules = ReadRules(source, diagnostics);
        string binding = "";
        string scopeSource = "";
        string scopeProperty = "";

        if (!rules.TryGetValue("scope-enum", out CommitlintRule? scopeEnum))
        {
            diagnostics.Add("The config sets no scope-enum rule, so it accepts any scope.");
            return new CommitlintConfig(rules, binding, scopeSource, scopeProperty, diagnostics);
        }

        if (!IdentifierPattern().IsMatch(scopeEnum.Argument))
        {
            diagnostics.Add(
                $"scope-enum takes {scopeEnum.Argument}, which is a list written in the config rather than a value read from a file."
            );
            return new CommitlintConfig(rules, binding, scopeSource, scopeProperty, diagnostics);
        }

        binding = scopeEnum.Argument;
        string initializer = Initializer(source, binding);
        if (initializer.Length == 0)
        {
            diagnostics.Add(
                $"The config takes its scopes from {binding}, which it never declares."
            );
            return new CommitlintConfig(rules, binding, scopeSource, scopeProperty, diagnostics);
        }

        if (!initializer.Contains("readFileSync", StringComparison.Ordinal))
        {
            diagnostics.Add($"The config builds {binding} without reading a file.");
        }

        Match file = JsonPathPattern().Match(initializer);
        if (file.Success)
        {
            scopeSource = file.Groups[1].Value;
        }
        else
        {
            diagnostics.Add($"The config builds {binding} from no named JSON file.");
        }

        Match mapped = MappedPropertyPattern().Match(initializer);
        if (mapped.Success)
        {
            scopeProperty = mapped.Groups[1].Value;
        }
        else
        {
            diagnostics.Add($"The config maps no property of each entry onto {binding}.");
        }

        return new CommitlintConfig(rules, binding, scopeSource, scopeProperty, diagnostics);
    }

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("""["']([^"']*\.json)["']""")]
    private static partial Regex JsonPathPattern();

    [GeneratedRegex(@"=>\s*[A-Za-z_$][A-Za-z0-9_$]*\s*\.\s*([A-Za-z_$][A-Za-z0-9_$]*)")]
    private static partial Regex MappedPropertyPattern();

    [GeneratedRegex(@"\brules\s*:\s*\{")]
    private static partial Regex RulesBlockPattern();

    private static Dictionary<string, CommitlintRule> ReadRules(
        string source,
        List<string> diagnostics
    )
    {
        Dictionary<string, CommitlintRule> rules = new(StringComparer.Ordinal);
        Match block = RulesBlockPattern().Match(source);
        if (!block.Success)
        {
            diagnostics.Add("The config declares no rules block.");
            return rules;
        }

        int open = block.Index + block.Length - 1;
        int close = SourceText.SkipBalanced(source, open);
        int index = open + 1;
        while (true)
        {
            index = SourceText.SkipTrivia(source, index);
            if (index >= close - 1)
            {
                return rules;
            }

            if (source[index] == ',')
            {
                index++;
                continue;
            }

            string name;
            if (SourceText.IsLiteralStart(source, index))
            {
                int nameEnd = SourceText.SkipLiteral(source, index);
                name = SourceText.LiteralValue(source, index, nameEnd);
                index = nameEnd;
            }
            else
            {
                int nameEnd = index;
                while (
                    nameEnd < close
                    && (char.IsLetterOrDigit(source[nameEnd]) || source[nameEnd] is '_' or '-')
                )
                {
                    nameEnd++;
                }

                name = source[index..nameEnd];
                index = nameEnd;
            }

            index = SourceText.SkipTrivia(source, index);
            if (name.Length == 0 || index >= close || source[index] != ':')
            {
                diagnostics.Add(
                    $"The rules block holds an entry this reader cannot take, at offset {index}."
                );
                return rules;
            }

            index = SourceText.SkipTrivia(source, index + 1);
            if (index >= close || source[index] != '[')
            {
                diagnostics.Add($"The rule {name} is not written as a list, at offset {index}.");
                return rules;
            }

            int valueEnd = SourceText.SkipBalanced(source, index);
            List<string> elements = Elements(source, index, valueEnd);
            if (
                elements.Count < 2
                || !int.TryParse(elements[0], CultureInfo.InvariantCulture, out int level)
            )
            {
                diagnostics.Add($"The rule {name} names no level and no applicability.");
                return rules;
            }

            rules[name] = new CommitlintRule(
                name,
                level,
                elements[1].Trim('"', '\''),
                elements.Count > 2 ? elements[2] : ""
            );
            index = valueEnd;
        }
    }

    private static List<string> Elements(string source, int open, int close)
    {
        List<string> elements = [];
        int depth = 0;
        int start = open + 1;
        int index = start;
        while (index < close - 1)
        {
            if (SourceText.IsLiteralStart(source, index))
            {
                index = SourceText.SkipLiteral(source, index);
                continue;
            }

            if (source[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (source[index] is ')' or ']' or '}')
            {
                depth--;
            }
            else if (source[index] == ',' && depth == 0)
            {
                elements.Add(source[start..index].Trim());
                start = index + 1;
            }

            index++;
        }

        string last = source[start..(close - 1)].Trim();
        if (last.Length > 0)
        {
            elements.Add(last);
        }

        return elements;
    }

    private static string Initializer(string source, string binding)
    {
        Match declaration = Regex.Match(
            source,
            $@"\b(?:const|let|var)\s+{Regex.Escape(binding)}\s*=",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        if (!declaration.Success)
        {
            return "";
        }

        int index = declaration.Index + declaration.Length;
        int depth = 0;
        while (index < source.Length)
        {
            if (SourceText.IsLiteralStart(source, index))
            {
                index = SourceText.SkipLiteral(source, index);
                continue;
            }

            if (source[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (source[index] is ')' or ']' or '}')
            {
                depth--;
            }
            else if (source[index] == ';' && depth == 0)
            {
                break;
            }

            index++;
        }

        return source[(declaration.Index + declaration.Length)..Math.Min(index, source.Length)];
    }
}

/// <summary>The packages a fenced install block tells a contributor to install.</summary>
/// <param name="PackageIds">Each package id, in the order the block lists them.</param>
/// <param name="Diagnostics">What the reader could not take.</param>
public sealed partial record InstallBlock(
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> Diagnostics
)
{
    /// <summary>Reads the first fenced block after a line naming an anchor.</summary>
    /// <remarks>
    /// A document holds several install blocks, so the anchor is what picks one out. A line inside
    /// the block that is not a plain winget install yields a diagnostic rather than being dropped.
    /// </remarks>
    /// <param name="markdown">The document.</param>
    /// <param name="anchor">Text on a line above the block.</param>
    /// <param name="language">The language the fence names.</param>
    /// <returns>The package ids, with a diagnostic for anything unreadable.</returns>
    public static InstallBlock Parse(string markdown, string anchor, string language)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int mention = Array.FindIndex(
            lines,
            line => line.Contains(anchor, StringComparison.Ordinal)
        );
        if (mention < 0)
        {
            return new InstallBlock([], [$"The document never names {anchor}."]);
        }

        string fence = "```" + language;
        int open = Array.FindIndex(lines, mention + 1, line => line.Trim() == fence);
        if (open < 0)
        {
            return new InstallBlock([], [$"No {fence} block follows the line naming {anchor}."]);
        }

        int close = Array.FindIndex(lines, open + 1, line => line.Trim() == "```");
        if (close < 0)
        {
            return new InstallBlock([], [$"The {fence} block after {anchor} never closes."]);
        }

        List<string> ids = [];
        List<string> diagnostics = [];
        for (int index = open + 1; index < close; index++)
        {
            Match install = InstallPattern().Match(lines[index].Trim());
            if (install.Success)
            {
                ids.Add(install.Groups[1].Value);
                continue;
            }

            diagnostics.Add(
                $"Line {index + 1} of the document is inside the install block after {anchor} and installs nothing."
            );
        }

        if (ids.Count == 0 && diagnostics.Count == 0)
        {
            diagnostics.Add($"The install block after {anchor} is empty.");
        }

        return new InstallBlock(ids, diagnostics);
    }

    [GeneratedRegex(@"^winget install --id (\S+) --exact$")]
    private static partial Regex InstallPattern();
}

/// <summary>A data file that lists objects.</summary>
public static class JsonList
{
    /// <summary>One property of every entry, in the order the file lists them.</summary>
    /// <param name="json">The file.</param>
    /// <param name="property">The property to take.</param>
    /// <returns>The value of that property on each entry.</returns>
    public static IReadOnlyList<string> Values(string json, string property)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return
        [
            .. document
                .RootElement.EnumerateArray()
                .Select(entry =>
                    entry.TryGetProperty(property, out JsonElement value)
                        ? value.GetString() ?? ""
                        : ""
                ),
        ];
    }
}

/// <summary>The scope vocabulary a commit header can draw on.</summary>
public static class CommitScopes
{
    /// <summary>Every way a scope file falls short of what a header and the document need.</summary>
    /// <remarks>
    /// The scope property is the one the commitlint config maps, so a rename on either side is a
    /// problem rather than a silent undefined. The covers property is what CONTRIBUTING.md promises
    /// each entry carries, and nothing but a reader enforces it.
    /// </remarks>
    /// <param name="json">The scope file.</param>
    /// <param name="property">The property the commitlint config takes as the scope.</param>
    /// <returns>One line per problem, and an empty list when the file is well formed.</returns>
    public static IReadOnlyList<string> Problems(string json, string property)
    {
        List<string> problems = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return ["The scope file is not a list of entries."];
        }

        List<string> seen = [];
        int position = 0;
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            position++;
            string scope =
                entry.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            if (scope.Trim().Length == 0)
            {
                problems.Add($"Entry {position} carries no {property} a commit header can name.");
                continue;
            }

            if (scope.Any(character => char.IsWhiteSpace(character) || character is '(' or ')'))
            {
                problems.Add($"The scope {scope} holds a character a commit header cannot carry.");
            }

            if (seen.Contains(scope, StringComparer.Ordinal))
            {
                problems.Add($"The scope {scope} is listed twice.");
            }

            seen.Add(scope);
            bool covered =
                entry.TryGetProperty("covers", out JsonElement covers)
                && covers.ValueKind == JsonValueKind.String
                && (covers.GetString() ?? "").Trim().Length > 0;
            if (!covered)
            {
                problems.Add($"The scope {scope} says nothing about what it covers.");
            }
        }

        if (position == 0)
        {
            problems.Add("The scope file lists no scope, so commitlint accepts none.");
        }

        return problems;
    }
}
