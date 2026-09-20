using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

/// <summary>Repository files as a case reads them, and the edits a case makes to one.</summary>
internal static class Documents
{
    /// <summary>Reads a repository file with one line ending, whatever a checkout wrote.</summary>
    public static string Read(string relativePath) =>
        Repository.Read(relativePath).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Makes one edit, and refuses an edit that would land nowhere.</summary>
    /// <remarks>
    /// A case whose edit misses leaves the original text, which then passes the assertion the case
    /// exists to fail.
    /// </remarks>
    /// <param name="text">The text to edit.</param>
    /// <param name="find">What the edit replaces.</param>
    /// <param name="replacement">What it becomes.</param>
    /// <returns>The edited text.</returns>
    public static string Edit(string text, string find, string replacement)
    {
        if (!text.Contains(find, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This case edits \"{find}\", which its source does not hold."
            );
        }

        return text.Replace(find, replacement, StringComparison.Ordinal);
    }

    /// <summary>One section of a document, from its heading to the next one.</summary>
    /// <param name="document">The document.</param>
    /// <param name="heading">The heading line the section starts with.</param>
    /// <returns>The section, or an empty string when the document holds no such heading.</returns>
    public static string Section(string document, string heading)
    {
        string[] lines = document.Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == heading);
        if (start < 0)
        {
            return "";
        }

        int end = Array.FindIndex(
            lines,
            start + 1,
            line => line.StartsWith("## ", StringComparison.Ordinal)
        );
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    /// <summary>A section with every run of whitespace levelled, so a rewrap changes nothing.</summary>
    /// <param name="document">The document.</param>
    /// <param name="heading">The heading line the section starts with.</param>
    /// <returns>The section on one line.</returns>
    public static string Prose(string document, string heading) =>
        Regex
            .Replace(
                Section(document, heading),
                @"\s+",
                " ",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            )
            .Trim();
}

/// <summary>A build script the cases work against, and the one the repository ships.</summary>
internal static class Scripts
{
    /// <summary>
    /// A script with the shape the gate has: a diamond over build, a chain three deep through
    /// check, two aggregates, an interpolated argument, and the name Task written where it is not
    /// a declaration.
    /// </summary>
    public const string Synthetic = """
        string target = Argument("target", "check");

        // A Task("clean") a contributor runs by hand would go here.
        string note = "Task(\"ghost\")";

        Task("format").Description("C# formatting").Does(() => Run("csharpier", "check ."));

        Task("build")
            .Description("Every project")
            .Does(() => Run("dotnet", $"build {Solution} {string.Join(", ", flags)}"));

        Task("tests").Description("The Core suite").IsDependentOn("build").Does(() => Run("test"));

        Task("installer").Description("The MSI").IsDependentOn("build").Does(() => Run("wix"));

        Task("code")
            .Description("Everything CI runs")
            .IsDependentOn("format")
            .IsDependentOn("tests")
            .IsDependentOn("installer");

        Task("workflows").Description("The workflow linters").Does(() => Run("actionlint"));

        Task("check").Description("The whole gate").IsDependentOn("code").IsDependentOn("workflows");

        RunTarget(target);
        """;

    /// <summary>A document whose gate table agrees with the synthetic script.</summary>
    public const string Synopsis = """
        # Contributing

        ## The gate

        | Task        | What it checks       |
        | ----------- | -------------------- |
        | `format`    | C# formatting        |
        | `build`     | Every project        |
        | `tests`     | The Core suite       |
        | `installer` | The MSI              |
        | `workflows` | The workflow linters |

        `--target=<task>` runs one task and the tasks it depends on. `--target=code` runs everything
        but `workflows`.

        ## Commit messages

        The header and every body line stay within 72 characters.
        """;
}

public sealed class GateScriptReaderTests
{
    public static TheoryData<string, string, string> Unreadable() =>
        new()
        {
            {
                "a task name held in a variable",
                """Task(name).Description("C# formatting").Does(() => Run());""",
                "name)"
            },
            {
                "a task name built by interpolation",
                """Task($"{prefix}-format").Description("C# formatting").Does(() => Run());""",
                "$\""
            },
            {
                "a description built by concatenation",
                """Task("format").Description("C# " + "formatting").Does(() => Run());""",
                "\"C# \""
            },
            {
                "a description held in a constant",
                """Task("format").Description(FormatSummary).Does(() => Run());""",
                "FormatSummary"
            },
            {
                "a dependency built by interpolation",
                """Task("tests").Description("The Core suite").IsDependentOn($"{prefix}build").Does(() => Run());""",
                "$\"{prefix}build\""
            },
            {
                "a chained call the reader does not know",
                """Task("format").Description("C# formatting").WithCriteria(true).Does(() => Run());""",
                "WithCriteria"
            },
        };

    public static TheoryData<string, string, string> Refused() =>
        new()
        {
            {
                "a task that says nothing about itself",
                """Task("format").Does(() => Run());""",
                "declares no description"
            },
            {
                "a description holding a pipe",
                """Task("format").Description("C# | formatting").Does(() => Run());""",
                "a table cell cannot carry"
            },
            {
                "a description holding a backtick",
                """Task("format").Description("C# formatting, through `csharpier`").Does(() => Run());""",
                "a table cell cannot carry"
            },
            {
                "a description holding a line break",
                """Task("format").Description("C# formatting\nthrough CSharpier").Does(() => Run());""",
                "a table cell cannot carry"
            },
            {
                "two tasks under one name",
                """
                    Task("format").Description("C# formatting").Does(() => Run());
                    Task("format").Description("Something else").Does(() => Run());
                    """,
                "a second task is named format"
            },
        };

    public static TheoryData<string, string, string, string[]> Reachability() =>
        new()
        {
            {
                "the default target reaches every check",
                Scripts.Synthetic,
                "check",
                ["format", "build", "tests", "installer", "workflows"]
            },
            {
                "an aggregate reaches the checks below it and is no check itself",
                Scripts.Synthetic,
                "code",
                ["format", "build", "tests", "installer"]
            },
            {
                "a task depended on twice is reached once",
                Scripts.Synthetic,
                "installer",
                ["build", "installer"]
            },
            {
                "a check with no dependencies reaches itself",
                Scripts.Synthetic,
                "format",
                ["format"]
            },
            {
                "a chain three deep reaches every step",
                Documents
                    .Edit(
                        Scripts.Synthetic,
                        """Task("build")""",
                        """
                        Task("restore").Description("Packages").Does(() => Run("restore"));

                        Task("build")
                        """
                    )
                    .Replace(
                        """.Description("Every project")""",
                        """.Description("Every project").IsDependentOn("restore")""",
                        StringComparison.Ordinal
                    ),
                "tests",
                ["restore", "build", "tests"]
            },
            {
                "the workflow linters pulled into the code target",
                Documents.Edit(
                    Scripts.Synthetic,
                    """.IsDependentOn("installer");""",
                    """
                    .IsDependentOn("installer")
                    .IsDependentOn("workflows");
                    """
                ),
                "code",
                ["format", "build", "tests", "installer", "workflows"]
            },
        };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Parse_ReportsADeclarationItCannotRead(
        string scenario,
        string declaration,
        string marker
    )
    {
        string source = $"string target = Argument(\"target\", \"check\");\n\n{declaration}\n";

        GateScript script = GateScript.Parse(source);

        script.Tasks.Should().BeEmpty(scenario);
        script
            .Diagnostics.Should()
            .ContainSingle(scenario)
            .Which.Should()
            .Contain(
                $"at offset {source.IndexOf(marker, StringComparison.Ordinal)}:",
                "a reader that cannot take a declaration names where it stopped"
            );
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Parse_ReportsADeclarationThatNoTableCanCarry(
        string scenario,
        string declaration,
        string named
    )
    {
        GateScript script = GateScript.Parse(
            $"string target = Argument(\"target\", \"check\");\n\n{declaration}\n"
        );

        script
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Contains(named, StringComparison.Ordinal), scenario);
    }

    [Theory]
    [MemberData(nameof(Reachability))]
    public void Checks_ReachEveryCheckBelowATargetAndNothingElse(
        string scenario,
        string script,
        string target,
        string[] expected
    )
    {
        GateScript parsed = GateScript.Parse(script);

        GateChecks checks = parsed.Checks(target);

        parsed.Diagnostics.Should().BeEmpty(scenario);
        checks.Problems.Should().BeEmpty(scenario);
        checks.Names.Should().BeEquivalentTo(expected, scenario);
    }

    [Fact]
    public void Parse_ReadsTheDefaultTargetTheScriptNames()
    {
        GateScript.Parse(Scripts.Synthetic).DefaultTarget.Should().Be("check");
        GateScript
            .Parse(
                Documents.Edit(
                    Scripts.Synthetic,
                    """Argument("target", "check")""",
                    """Argument("target", "code")"""
                )
            )
            .DefaultTarget.Should()
            .Be("code");
    }

    [Fact]
    public void Parse_ReportsAScriptThatNamesNoDefaultTarget()
    {
        GateScript script = GateScript.Parse(
            Documents.Edit(Scripts.Synthetic, """Argument("target", "check")""", "\"check\"")
        );

        script.Diagnostics.Should().ContainSingle().Which.Should().Contain("no default target");
    }

    [Fact]
    public void Parse_TakesTheNameTheDependenciesAndTheDescriptionOfEveryTask()
    {
        GateScript script = GateScript.Parse(Scripts.Synthetic);

        script.Diagnostics.Should().BeEmpty();
        script
            .Tasks.Select(task => task.Name)
            .Should()
            .Equal("format", "build", "tests", "installer", "code", "workflows", "check");
        script.Tasks.Single(task => task.Name == "tests").DependsOn.Should().Equal("build");
        script.Tasks.Single(task => task.Name == "tests").Description.Should().Be("The Core suite");
        script.Tasks.Single(task => task.Name == "code").Checks.Should().BeFalse();
        script.Tasks.Single(task => task.Name == "tests").Checks.Should().BeTrue();
    }

    [Fact]
    public void Checks_ReportATargetTheScriptDeclaresNowhere()
    {
        GateChecks checks = GateScript.Parse(Scripts.Synthetic).Checks("clean");

        checks.Names.Should().BeEmpty();
        checks.Problems.Should().ContainSingle().Which.Should().Contain("clean");
    }

    [Fact]
    public void Checks_ReportADependencyOnATaskThatDoesNotExist()
    {
        GateScript script = GateScript.Parse(
            Documents.Edit(
                Scripts.Synthetic,
                """.IsDependentOn("installer");""",
                """.IsDependentOn("licenses");"""
            )
        );

        script.Checks("check").Problems.Should().ContainSingle().Which.Should().Contain("licenses");
    }
}

public sealed class GateTableReaderTests
{
    [Fact]
    public void Parse_TakesEveryRowOfTheTableUnderTheHeading()
    {
        GateTable table = GateTable.Parse(Scripts.Synopsis, GateDocumentation.Heading);

        table.Diagnostics.Should().BeEmpty();
        table
            .Rows.Select(row => row.Task)
            .Should()
            .Equal("format", "build", "tests", "installer", "workflows");
        table.Rows[0].Cell.Should().Be("C# formatting");
    }

    [Fact]
    public void Parse_ReportsADocumentWithNoGateSection()
    {
        GateTable
            .Parse(
                Documents.Edit(Scripts.Synopsis, "## The gate", "## The checks"),
                GateDocumentation.Heading
            )
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(GateDocumentation.Heading);
    }

    [Fact]
    public void Parse_ReportsAGateSectionWithNoTable()
    {
        string[] lines = Scripts.Synopsis.Split('\n');
        string stripped = string.Join('\n', lines.Where(line => !line.StartsWith('|')));

        GateTable table = GateTable.Parse(stripped, GateDocumentation.Heading);

        table.Rows.Should().BeEmpty();
        table.Diagnostics.Should().ContainSingle().Which.Should().Contain("holds no table");
    }

    [Fact]
    public void Parse_TakesOnlyTheRowsTheTableHolds()
    {
        GateTable
            .Parse(
                Documents.Edit(Scripts.Synopsis, "| `format`    | C# formatting        |\n", ""),
                GateDocumentation.Heading
            )
            .Rows.Should()
            .HaveCount(4, "a row the document drops is not a row this reader invents");
    }

    [Fact]
    public void Parse_ReportsARowThatNamesNoTaskInBackticks()
    {
        GateTable table = GateTable.Parse(
            Documents.Edit(
                Scripts.Synopsis,
                "| `format`    | C# formatting        |",
                "| format      | C# formatting        |"
            ),
            GateDocumentation.Heading
        );

        table.Rows.Should().HaveCount(4);
        table
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("naming no task in backticks");
    }

    [Fact]
    public void Parse_CountsTheLineEveryRowSitsOn()
    {
        GateTable table = GateTable.Parse(Scripts.Synopsis, GateDocumentation.Heading);

        string[] lines = Scripts.Synopsis.Split('\n');
        foreach (GateTableRow row in table.Rows)
        {
            lines[row.Line - 1].Should().Contain($"`{row.Task}`");
        }
    }
}

public sealed class GateTableAgainstTheScriptTests
{
    private sealed record Drift(string Script, string Document, string[] Named);

    private static readonly Dictionary<string, Drift> Drifts = new(StringComparer.Ordinal)
    {
        ["a check the table never names"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                """.IsDependentOn("installer");""",
                """
                .IsDependentOn("installer")
                .IsDependentOn("licenses");

                Task("licenses").Description("Third-party notices").Does(() => Run("licenses"));
                """
            ),
            Scripts.Synopsis,
            ["licenses"]
        ),
        ["a row naming a check that does not exist"] = new(
            Documents.Edit(Scripts.Synthetic, "\"workflows\"", "\"linters\""),
            Scripts.Synopsis,
            ["workflows", "linters"]
        ),
        ["a row for a task that performs no check"] = new(
            Scripts.Synthetic,
            Documents.Edit(
                Scripts.Synopsis,
                "| `workflows` | The workflow linters |",
                "| `code`      | Everything CI runs   |\n| `workflows` | The workflow linters |"
            ),
            ["code"]
        ),
        ["a row for a check the default target never reaches"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                """Argument("target", "check")""",
                """Argument("target", "code")"""
            ),
            Scripts.Synopsis,
            ["workflows"]
        ),
        ["a row that no longer says what its task checks"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                "\"Every project\"",
                "\"Every project in Release and Debug\""
            ),
            Scripts.Synopsis,
            ["build", "Every project in Release and Debug"]
        ),
        ["a cell that says more than the task claims"] = new(
            Scripts.Synthetic,
            Documents.Edit(
                Scripts.Synopsis,
                "| `tests`     | The Core suite       |",
                "| `tests`     | The Core suite and its fakes |"
            ),
            ["tests", "The Core suite and its fakes"]
        ),
        ["a task that says nothing about itself"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                """Task("installer").Description("The MSI")""",
                """Task("installer")"""
            ),
            Scripts.Synopsis,
            ["installer"]
        ),
        ["a description no table cell can carry"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                "\"The Core suite\"",
                "\"The Core suite | and its fakes\""
            ),
            Scripts.Synopsis,
            ["tests"]
        ),
    };

    private static readonly Dictionary<string, Drift> Agreements = new(StringComparer.Ordinal)
    {
        ["a cell that puts a name in backticks"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                "\"The MSI\"",
                "\"The MSI, built from installer/WingetNudge.Installer.wixproj\""
            ),
            Documents.Edit(
                Scripts.Synopsis,
                "| `installer` | The MSI              |",
                "| `installer` | The MSI, built from `installer/WingetNudge.Installer.wixproj` |"
            ),
            []
        ),
        ["a table prettier has padded"] = new(
            Scripts.Synthetic,
            Documents.Edit(
                Scripts.Synopsis,
                "| `tests`     | The Core suite       |",
                "| `tests` |    The Core suite    |"
            ),
            []
        ),
        ["a task the gate gains with a row to match"] = new(
            Documents.Edit(
                Scripts.Synthetic,
                """.IsDependentOn("installer");""",
                """
                .IsDependentOn("installer")
                .IsDependentOn("licenses");

                Task("licenses").Description("Third-party notices").Does(() => Run("licenses"));
                """
            ),
            Documents.Edit(
                Scripts.Synopsis,
                "| `workflows` | The workflow linters |",
                "| `workflows` | The workflow linters |\n| `licenses`  | Third-party notices  |"
            ),
            []
        ),
    };

    public static TheoryData<string> DriftNames => [.. Drifts.Keys];

    public static TheoryData<string> AgreementNames => [.. Agreements.Keys];

    [Fact]
    public void Mismatches_AreNoneWhenTheTableAndTheScriptAgree()
    {
        GateDocumentation.Mismatches(Scripts.Synthetic, Scripts.Synopsis).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(DriftNames))]
    public void Mismatches_NameWhatTheTableAndTheScriptDisagreeAbout(string scenario)
    {
        Drift drift = Drifts[scenario];

        IReadOnlyList<string> mismatches = GateDocumentation.Mismatches(
            drift.Script,
            drift.Document
        );

        foreach (string named in drift.Named)
        {
            mismatches
                .Should()
                .Contain(mismatch => mismatch.Contains(named, StringComparison.Ordinal), scenario);
        }
    }

    [Theory]
    [MemberData(nameof(AgreementNames))]
    public void Mismatches_AreNoneWhenOnlyTheMarkupDiffers(string scenario)
    {
        Drift agreement = Agreements[scenario];

        GateDocumentation
            .Mismatches(agreement.Script, agreement.Document)
            .Should()
            .BeEmpty(scenario);
    }

    [Theory]
    [InlineData("C# formatting", "C# formatting")]
    [InlineData(
        "The three-day cooldown, in `renovate.json`",
        "The three-day cooldown, in renovate.json"
    )]
    [InlineData("  The   Core\n  suite  ", "The Core suite")]
    public void Normalize_LevelsTheMarkupATableCellMayCarry(string cell, string expected)
    {
        GateDocumentation.Normalize(cell).Should().Be(expected);
    }
}

public sealed class GateTableAgainstTheRepositoryTests
{
    [Fact]
    public void TheGateTableSaysWhatTheBuildScriptDeclares()
    {
        GateDocumentation
            .Mismatches(Documents.Read("cake.cs"), Documents.Read("CONTRIBUTING.md"))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void TheComparisonReadsBothFilesRatherThanTwoEmptyLists()
    {
        GateScript script = GateScript.Parse(Documents.Read("cake.cs"));
        GateTable table = GateTable.Parse(
            Documents.Read("CONTRIBUTING.md"),
            GateDocumentation.Heading
        );

        GateChecks checks = script.Checks(script.DefaultTarget);

        script.Tasks.Should().HaveCountGreaterThanOrEqualTo(7);
        checks.Names.Should().HaveCountGreaterThanOrEqualTo(7);
        table.Rows.Should().HaveCount(checks.Names.Count);
    }

    [Fact]
    public void ARowDroppedFromTheTableIsReported()
    {
        string document = Documents.Read("CONTRIBUTING.md");
        GateTableRow dropped = GateTable.Parse(document, GateDocumentation.Heading).Rows[0];
        string[] lines = document.Split('\n');

        IReadOnlyList<string> mismatches = GateDocumentation.Mismatches(
            Documents.Read("cake.cs"),
            string.Join('\n', lines[..(dropped.Line - 1)].Concat(lines[dropped.Line..]))
        );

        mismatches.Should().ContainSingle().Which.Should().Contain(dropped.Task);
    }

    [Fact]
    public void TheTargetTheDocumentSinglesOutRunsEveryCheckButTheOneItNames()
    {
        string prose = Documents.Prose(
            Documents.Read("CONTRIBUTING.md"),
            GateDocumentation.Heading
        );
        Match claim = Regex.Match(
            prose,
            @"`--target=(\w+)` runs everything but `(\w+)`",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        GateScript script = GateScript.Parse(Documents.Read("cake.cs"));

        claim.Success.Should().BeTrue("the gate section names a target that skips one check");
        GateChecks whole = script.Checks(script.DefaultTarget);
        GateChecks narrowed = script.Checks(claim.Groups[1].Value);

        whole.Names.Should().Contain(claim.Groups[2].Value);
        narrowed.Problems.Should().BeEmpty();
        narrowed
            .Names.Should()
            .BeEquivalentTo(whole.Names.Where(name => name != claim.Groups[2].Value));
    }
}

public sealed class RepositoryRootTests
{
    [Fact]
    public void TheWalkEndsAtTheDirectoryTheRepositoryOccupies()
    {
        string root = Repository.Root;

        foreach (
            string sibling in (string[])
                ["commitlint.config.js", "docs/dev.md", ".github/commit-scopes.json"]
        )
        {
            File.Exists(Path.Combine(root, sibling.Replace('/', Path.DirectorySeparatorChar)))
                .Should()
                .BeTrue($"the root holds {sibling}, which the walk never looks for");
        }
    }

    [Fact]
    public void TheWalkNamesWhereItStartedWhenNothingAboveItIsARepository()
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("winget-nudge-root");
        try
        {
            Action walk = () => Repository.Find(scratch.FullName);

            walk.Should().Throw<DirectoryNotFoundException>().WithMessage($"*{scratch.FullName}*");
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}

public sealed class CommitMessageRulesTests
{
    private const string CommitHeading = "## Commit messages";

    // Both rules tighten the 100 that @commitlint/config-conventional sets.
    private static readonly string[] LengthRules = ["header-max-length", "body-max-line-length"];

    public static TheoryData<string, string, string> BrokenWiring() =>
        new()
        {
            {
                "a scope list written into the config",
                Documents.Edit(Config, ", scopes]", ", [\"core\", \"app\"]]"),
                "rather than a value read from a file"
            },
            {
                "a scope list bound to a name the config never declares",
                Documents.Edit(Config, "const scopes =", "const names ="),
                "which it never declares"
            },
            {
                "a scope list built without reading a file",
                Documents.Edit(Config, "readFileSync(", "scopesOf("),
                "without reading a file"
            },
            {
                "a scope list that maps no property of an entry",
                Documents.Edit(Config, "entry.scope)", "entry)"),
                "maps no property"
            },
            {
                "a config that sets no scope rule",
                Documents.Edit(Config, "\"scope-enum\"", "\"scope-empty\""),
                "accepts any scope"
            },
        };

    public static TheoryData<string, string, bool> Severities() =>
        new()
        {
            { "the config as it stands", Config, true },
            {
                "a header limit that only warns",
                Documents.Edit(Config, "\"header-max-length\": [2", "\"header-max-length\": [1"),
                false
            },
            {
                "a body limit switched off",
                Documents.Edit(
                    Config,
                    "\"body-max-line-length\": [2",
                    "\"body-max-line-length\": [0"
                ),
                false
            },
            {
                "a header limit that applies never",
                Documents.Edit(
                    Config,
                    "\"header-max-length\": [2, \"always\"",
                    "\"header-max-length\": [2, \"never\""
                ),
                false
            },
        };

    public static TheoryData<string, string, string, bool> Limits() =>
        new()
        {
            { "the repository as it stands", Contributing, Config, true },
            {
                "a document stating a limit the config does not enforce",
                Documents.Edit(Contributing, "within 72 characters", "within 100 characters"),
                Config,
                false
            },
            {
                "a config loosening the header limit",
                Contributing,
                Documents.Edit(
                    Config,
                    "\"header-max-length\": [2, \"always\", 72]",
                    "\"header-max-length\": [2, \"always\", 100]"
                ),
                false
            },
            {
                "a config loosening the body limit",
                Contributing,
                Documents.Edit(
                    Config,
                    "\"body-max-line-length\": [2, \"always\", 72]",
                    "\"body-max-line-length\": [2, \"always\", 100]"
                ),
                false
            },
        };

    public static TheoryData<string, string, string> MalformedScopes() =>
        new()
        {
            {
                "an entry carrying a property the config does not read",
                """[{ "name": "core", "covers": "the library" }]""",
                "carries no scope"
            },
            {
                "one scope listed twice",
                """[{ "scope": "core", "covers": "a" }, { "scope": "core", "covers": "b" }]""",
                "is listed twice"
            },
            {
                "a scope holding a space",
                """[{ "scope": "core tools", "covers": "a" }]""",
                "cannot carry"
            },
            {
                "a scope holding a parenthesis",
                """[{ "scope": "core(x)", "covers": "a" }]""",
                "cannot carry"
            },
            {
                "a scope saying nothing about what it covers",
                """[{ "scope": "core", "covers": "" }]""",
                "says nothing about what it covers"
            },
            { "a file listing no scope at all", "[]", "lists no scope" },
        };

    private static string Config => Documents.Read("commitlint.config.js");

    private static string Contributing => Documents.Read("CONTRIBUTING.md");

    [Fact]
    public void TheConfigReadsItsScopesFromTheFileTheDocumentNames()
    {
        string named = Regex
            .Match(
                Documents.Prose(Contributing, CommitHeading),
                @"`([^`]+\.json)`",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            )
            .Groups[1]
            .Value;
        CommitlintConfig config = CommitlintConfig.Parse(Config);

        named.Should().NotBeEmpty("the commit section points at the file the scopes live in");
        config.Diagnostics.Should().BeEmpty();
        config.ScopeSource.Should().Be(named);
        config.ScopeProperty.Should().NotBeEmpty();
        File.Exists(Path.Combine(Repository.Root, named.Replace('/', Path.DirectorySeparatorChar)))
            .Should()
            .BeTrue();
    }

    [Theory]
    [MemberData(nameof(BrokenWiring))]
    public void AScopeListThatIsNotReadFromTheDataFileIsReported(
        string scenario,
        string config,
        string named
    )
    {
        CommitlintConfig parsed = CommitlintConfig.Parse(config);

        parsed
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Contains(named, StringComparison.Ordinal), scenario);
    }

    [Theory]
    [MemberData(nameof(Severities))]
    public void ALengthRuleRefusesACommitRatherThanWarningAboutIt(
        string scenario,
        string config,
        bool enforced
    )
    {
        CommitlintConfig parsed = CommitlintConfig.Parse(config);

        bool refuses = LengthRules.All(name =>
            parsed.Rules.TryGetValue(name, out CommitlintRule? rule)
            && rule.Level == 2
            && rule.Applicable == "always"
        );

        refuses.Should().Be(enforced, scenario);
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void TheDocumentStatesTheLineLengthTheConfigEnforces(
        string scenario,
        string document,
        string config,
        bool agree
    )
    {
        int? stated = StatedLimit(document);
        CommitlintConfig parsed = CommitlintConfig.Parse(config);

        bool enforced =
            stated is not null
            && LengthRules.All(name =>
                parsed.Rules.TryGetValue(name, out CommitlintRule? rule)
                && rule.Argument == stated.Value.ToString(CultureInfo.InvariantCulture)
            );

        enforced.Should().Be(agree, scenario);
    }

    [Fact]
    public void ACommitSectionStatingNoLimitIsNotTakenForAgreement()
    {
        StatedLimit(Documents.Edit(Contributing, "within 72 characters", "short"))
            .Should()
            .BeNull();
    }

    [Fact]
    public void EveryScopeEntryIsOneACommitHeaderCanName()
    {
        CommitlintConfig config = CommitlintConfig.Parse(Config);

        CommitScopes
            .Problems(Documents.Read(config.ScopeSource), config.ScopeProperty)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void TheScopeFileListsMoreThanOneScope()
    {
        CommitlintConfig config = CommitlintConfig.Parse(Config);

        JsonList
            .Values(Documents.Read(config.ScopeSource), config.ScopeProperty)
            .Should()
            .HaveCountGreaterThan(1);
    }

    [Theory]
    [MemberData(nameof(MalformedScopes))]
    public void AScopeFileNoCommitHeaderCanDrawOnIsReported(
        string scenario,
        string json,
        string named
    )
    {
        CommitScopes
            .Problems(json, "scope")
            .Should()
            .Contain(problem => problem.Contains(named, StringComparison.Ordinal), scenario);
    }

    private static int? StatedLimit(string document)
    {
        MatchCollection matches = Regex.Matches(
            Documents.Prose(document, CommitHeading),
            @"within (\d+) characters",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        return matches.Count == 1
            ? int.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }
}

public sealed class DeveloperGuideTests
{
    private const string PinFile = "mise.toml";
    private const string Prerequisites = "winget 1.29 or newer";
    private const string ToolManager = "jdx.mise";

    [Fact]
    public void TheInstallBlockNamesTheToolThatReadsThePinFile()
    {
        InstallBlock block = InstallBlock.Parse(
            Documents.Read("docs/dev.md"),
            PinFile,
            "powershell"
        );

        block.Diagnostics.Should().BeEmpty();
        block.PackageIds.Should().Equal((string[])[ToolManager]);
    }

    [Fact]
    public void TheInstallBlockIsReadInTheOrderTheDocumentLists()
    {
        IReadOnlyList<string> listed = InstallBlock
            .Parse(Documents.Read("docs/dev.md"), Prerequisites, "powershell")
            .PackageIds;
        string swapped = Documents.Edit(
            Documents.Read("docs/dev.md"),
            $"winget install --id {listed[0]} --exact\nwinget install --id {listed[1]} --exact",
            $"winget install --id {listed[1]} --exact\nwinget install --id {listed[0]} --exact"
        );

        InstallBlock
            .Parse(swapped, Prerequisites, "powershell")
            .PackageIds.Should()
            .NotEqual(listed)
            .And.BeEquivalentTo(listed);
    }

    [Fact]
    public void ALineThatInstallsNothingInsideTheBlockIsReported()
    {
        string loosened = Documents.Edit(
            Documents.Read("docs/dev.md"),
            $"winget install --id {ToolManager} --exact",
            $"winget install {ToolManager}"
        );

        InstallBlock
            .Parse(loosened, PinFile, "powershell")
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("installs nothing");
    }

    [Fact]
    public void ADocumentThatNamesThePinFileNowhereIsReported()
    {
        InstallBlock
            .Parse(
                Documents.Edit(Documents.Read("docs/dev.md"), PinFile, "the pin file"),
                PinFile,
                "powershell"
            )
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("never names");
    }

    [Fact]
    public void TheFirstRunBlockTrustsThePinFileAndInstallsFromIt()
    {
        string[] lines = Documents
            .Section(Documents.Read("docs/dev.md"), "## First run")
            .Split('\n');
        int open = Array.IndexOf(lines, "```powershell");
        int close = open < 0 ? -1 : Array.IndexOf(lines, "```", open + 1);

        open.Should().BePositive("the first run section opens with the commands a clone runs");
        close.Should().BeGreaterThan(open, "the command block closes");
        lines[(open + 1)..close]
            .Should()
            .ContainInOrder(
                (string[])["mise trust", "mise install", "dotnet cake.cs"],
                "the gate reads mise.toml's settings once trusted and resolves linters an install put on disk"
            );
    }
}
