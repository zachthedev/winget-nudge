using System.Text.RegularExpressions;

namespace WingetNudge.Core.Tests.Support;

/// <summary>One task a Cake script declares.</summary>
/// <param name="Name">The task name.</param>
/// <param name="Description">The one line the task says about itself.</param>
/// <param name="Checks">Whether the task performs a check of its own, which a .Does call is.</param>
/// <param name="DependsOn">The tasks it runs after, in declaration order.</param>
public sealed record GateTask(string Name, string Description, bool Checks, IReadOnlyList<string> DependsOn);

/// <summary>The checks one target reaches.</summary>
/// <param name="Names">Each task that performs a check, in the order the script declares them.</param>
/// <param name="Problems">What the walk could not resolve.</param>
public sealed record GateChecks(IReadOnlyList<string> Names, IReadOnlyList<string> Problems);

/// <summary>A Cake script read as text: its default target and every task it declares.</summary>
/// <remarks>
/// A task declaration is a chain of calls over plain string literals. A chain this reader cannot
/// take yields a diagnostic naming the offset and no task, so an unreadable declaration is loud
/// rather than a task quietly missing from the set.
/// </remarks>
public sealed partial class GateScript
{
    private readonly Dictionary<string, GateTask> _byName;

    private GateScript(string defaultTarget, IReadOnlyList<GateTask> tasks, IReadOnlyList<string> diagnostics)
    {
        DefaultTarget = defaultTarget;
        Tasks = tasks;
        Diagnostics = diagnostics;
        _byName = tasks.ToDictionary(task => task.Name, StringComparer.Ordinal);
    }

    /// <summary>The target the script runs when the command line names none.</summary>
    public string DefaultTarget { get; }

    /// <summary>Every task the script declares, in declaration order.</summary>
    public IReadOnlyList<GateTask> Tasks { get; }

    /// <summary>What the reader could not take, each naming an offset in the source.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Reads a Cake script.</summary>
    /// <param name="source">The script text.</param>
    /// <returns>The tasks and the default target, with a diagnostic for anything unreadable.</returns>
    public static GateScript Parse(string source)
    {
        List<string> diagnostics = [];
        Match target = DefaultTargetPattern().Match(source);
        if (!target.Success)
        {
            diagnostics.Add("The build script names no default target: it makes no Argument(\"target\", ...) call.");
        }

        List<GateTask> tasks = [];
        foreach (int offset in SourceText.InvocationOffsets(source, "Task"))
        {
            GateTask? task = ReadTask(source, offset, diagnostics);
            if (task is null)
            {
                continue;
            }

            if (tasks.Any(declared => declared.Name == task.Name))
            {
                diagnostics.Add(Diagnostic(offset, $"a second task is named {task.Name}."));
                continue;
            }

            foreach (char hostile in "|`\r\n")
            {
                if (task.Description.Contains(hostile, StringComparison.Ordinal))
                {
                    diagnostics.Add(
                        Diagnostic(
                            offset,
                            $"the description of {task.Name} holds a character a table cell cannot carry."
                        )
                    );
                }
            }

            tasks.Add(task);
        }

        return new GateScript(target.Success ? target.Groups[1].Value : "", tasks, diagnostics);
    }

    /// <summary>The checks a target reaches, directly or through its dependencies.</summary>
    /// <remarks>
    /// The names come back in declaration order, which is a stable order for a message and says
    /// nothing about the order Cake runs them in.
    /// </remarks>
    /// <param name="target">The task the run starts from.</param>
    /// <returns>Each check and anything the walk could not resolve.</returns>
    public GateChecks Checks(string target)
    {
        List<string> problems = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<string> reached = new(StringComparer.Ordinal);
        Visit(target, null);
        return new GateChecks(
            [.. Tasks.Where(task => reached.Contains(task.Name)).Select(task => task.Name)],
            problems
        );

        void Visit(string name, string? dependent)
        {
            if (!visited.Add(name))
            {
                return;
            }

            if (!_byName.TryGetValue(name, out GateTask? task))
            {
                problems.Add(
                    dependent is null
                        ? $"The build script declares no task named {name}."
                        : $"The task {dependent} depends on {name}, which the build script declares nowhere."
                );
                return;
            }

            foreach (string dependency in task.DependsOn)
            {
                Visit(dependency, name);
            }

            if (task.Checks)
            {
                reached.Add(name);
            }
        }
    }

    [GeneratedRegex("""Argument\(\s*"target"\s*,\s*"([^"]*)"\s*\)""")]
    private static partial Regex DefaultTargetPattern();

    private static string Diagnostic(int offset, string message) => $"The build script at offset {offset}: {message}";

    private static GateTask? ReadTask(string source, int offset, List<string> diagnostics)
    {
        int index = SourceText.SkipTrivia(source, offset + "Task".Length) + 1;
        if (!ReadArgument(source, ref index, "the name of a task", diagnostics, out string name))
        {
            return null;
        }

        string? description = null;
        List<string> dependsOn = [];
        bool checks = false;
        while (true)
        {
            index = SourceText.SkipTrivia(source, index);
            if (index >= source.Length)
            {
                diagnostics.Add(Diagnostic(offset, $"the declaration of {name} never ends."));
                return null;
            }

            if (source[index] == ';')
            {
                break;
            }

            if (source[index] != '.')
            {
                diagnostics.Add(Diagnostic(index, $"{name} is followed by neither a chained call nor a semicolon."));
                return null;
            }

            int callStart = SourceText.SkipTrivia(source, index + 1);
            int callEnd = callStart;
            while (callEnd < source.Length && (char.IsLetterOrDigit(source[callEnd]) || source[callEnd] == '_'))
            {
                callEnd++;
            }

            int open = SourceText.SkipTrivia(source, callEnd);
            if (open >= source.Length || source[open] != '(')
            {
                diagnostics.Add(Diagnostic(callStart, $"{name} chains something that is not a call."));
                return null;
            }

            switch (source[callStart..callEnd])
            {
                case "Description":
                    index = open + 1;
                    if (!ReadArgument(source, ref index, $"the description of {name}", diagnostics, out string value))
                    {
                        return null;
                    }

                    description = value;
                    break;
                case "IsDependentOn":
                    index = open + 1;
                    if (!ReadArgument(source, ref index, $"a dependency of {name}", diagnostics, out string dependency))
                    {
                        return null;
                    }

                    dependsOn.Add(dependency);
                    break;
                case "Does":
                    checks = true;
                    index = SourceText.SkipBalanced(source, open);
                    break;
                default:
                    diagnostics.Add(
                        Diagnostic(
                            callStart,
                            $"{name} chains {source[callStart..callEnd]}, which this reader does not know."
                        )
                    );
                    return null;
            }
        }

        if (description is null)
        {
            diagnostics.Add(Diagnostic(offset, $"{name} declares no description."));
            return null;
        }

        return new GateTask(name, description, checks, dependsOn);
    }

    private static bool ReadArgument(
        string source,
        ref int index,
        string subject,
        List<string> diagnostics,
        out string value
    )
    {
        value = "";
        int start = SourceText.SkipTrivia(source, index);
        bool raw =
            start + 2 < source.Length && source[start] == '"' && source[start + 1] == '"' && source[start + 2] == '"';
        if (start >= source.Length || source[start] != '"' || raw)
        {
            diagnostics.Add(Diagnostic(start, $"{subject} is not a plain string literal."));
            return false;
        }

        int end = SourceText.SkipLiteral(source, start);
        int close = SourceText.SkipTrivia(source, end);
        if (close >= source.Length || source[close] != ')')
        {
            diagnostics.Add(Diagnostic(start, $"{subject} is more than one string literal."));
            return false;
        }

        value = SourceText.LiteralValue(source, start, end);
        index = close + 1;
        return true;
    }
}

/// <summary>One row of the gate table.</summary>
/// <param name="Task">The task the row names.</param>
/// <param name="Cell">What the row says the task checks.</param>
/// <param name="Line">The line of the document the row sits on, counting from one.</param>
public sealed record GateTableRow(string Task, string Cell, int Line);

/// <summary>The gate table of a document.</summary>
/// <param name="Rows">Each row, in the order the document lists them.</param>
/// <param name="Diagnostics">What the reader could not take.</param>
public sealed partial record GateTable(IReadOnlyList<GateTableRow> Rows, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Reads the first table under a heading.</summary>
    /// <remarks>
    /// A row whose first cell is not a task name in backticks yields a diagnostic, so a row added
    /// in another shape is reported rather than dropped.
    /// </remarks>
    /// <param name="markdown">The document.</param>
    /// <param name="heading">The heading line the section starts with.</param>
    /// <returns>The rows, with a diagnostic for anything unreadable.</returns>
    public static GateTable Parse(string markdown, string heading)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == heading);
        if (start < 0)
        {
            return new GateTable([], [$"The document holds no {heading} heading."]);
        }

        int end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        end = end < 0 ? lines.Length : end;

        int first = Array.FindIndex(lines, start, end - start, line => line.StartsWith('|'));
        if (first < 0)
        {
            return new GateTable([], [$"The {heading} section holds no table."]);
        }

        int last = first;
        while (last < end && lines[last].StartsWith('|'))
        {
            last++;
        }

        List<string> diagnostics = [];
        if (last - first < 3)
        {
            diagnostics.Add($"The table under {heading} holds a header and no rows.");
        }

        List<GateTableRow> rows = [];
        for (int index = first + 2; index < last; index++)
        {
            Match row = RowPattern().Match(lines[index]);
            if (row.Success)
            {
                rows.Add(new GateTableRow(row.Groups[1].Value, row.Groups[2].Value, index + 1));
                continue;
            }

            diagnostics.Add($"Line {index + 1} of the document is a table row naming no task in backticks.");
        }

        return new GateTable(rows, diagnostics);
    }

    [GeneratedRegex("""^\|\s*`([^`]+)`\s*\|\s*(.*?)\s*\|\s*$""")]
    private static partial Regex RowPattern();
}

/// <summary>What the gate section of a document says against what the build script declares.</summary>
public static partial class GateDocumentation
{
    /// <summary>The heading the gate section starts with.</summary>
    public const string Heading = "## The gate";

    /// <summary>Strips the markup a cell may carry and levels the whitespace.</summary>
    /// <remarks>
    /// Applied to both sides, so a cell may put a file name in backticks while the description a
    /// terminal prints leaves it bare. A description carrying a backtick is a diagnostic of its
    /// own, which is what stops this rule from hiding a real difference.
    /// </remarks>
    /// <param name="text">A cell or a description.</param>
    /// <returns>The comparable form.</returns>
    public static string Normalize(string text) =>
        WhitespaceRuns().Replace(text.Replace("`", "", StringComparison.Ordinal), " ").Trim();

    /// <summary>Every way the gate table and the build script disagree.</summary>
    /// <param name="script">The build script text.</param>
    /// <param name="document">The document text.</param>
    /// <returns>One line per disagreement, and an empty list when they agree.</returns>
    public static IReadOnlyList<string> Mismatches(string script, string document)
    {
        GateScript gate = GateScript.Parse(script);
        GateTable table = GateTable.Parse(document, Heading);
        GateChecks checks = gate.Checks(gate.DefaultTarget);
        List<string> mismatches = [.. gate.Diagnostics, .. table.Diagnostics, .. checks.Problems];

        foreach (string check in checks.Names)
        {
            if (!table.Rows.Any(row => row.Task == check))
            {
                mismatches.Add($"The gate runs {check}, and the table has no row for it.");
            }
        }

        foreach (GateTableRow row in table.Rows)
        {
            GateTask? declared = gate.Tasks.FirstOrDefault(task => task.Name == row.Task);
            if (declared is null)
            {
                mismatches.Add($"The table has a row for {row.Task}, and the build script declares no such task.");
                continue;
            }

            if (!declared.Checks)
            {
                mismatches.Add($"The table has a row for {row.Task}, which runs no check of its own.");
            }
            else if (!checks.Names.Contains(row.Task))
            {
                mismatches.Add($"The table has a row for {row.Task}, which the default target never reaches.");
            }

            if (Normalize(row.Cell) != Normalize(declared.Description))
            {
                mismatches.Add(
                    $"The row for {row.Task} reads \"{row.Cell}\", and the task describes itself as \"{declared.Description}\"."
                );
            }
        }

        return mismatches;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();
}
