#:sdk Cake.Sdk
#:property NuGetLockFilePath=cake.packages.lock.json
#:property RestoreLockedMode=true

using System.Text.Json;
using System.Text.RegularExpressions;

// The gate: every check a change must pass before it leaves the machine. CONTRIBUTING.md lists
// what each task covers. The pre-push hook runs the check target, and continuous integration runs
// the code target in one job and the same workflow linters from their official images in another.

string target = Argument("target", "check");

// The workflow linters a local run uses, pinned in a data file rather than here: a formatter moves
// source, and a pin that moves is a pin a tool cannot read. Continuous integration runs the same
// versions from the images .github/workflows/ci.yml pins by digest, and the workflows task refuses
// a mismatch between the two. The actionlint image bundles ShellCheck, and actionlint skips its
// shell checks without a word when shellcheck is not on PATH, so the file pins that too.
LinterPin[] linters =
    JsonSerializer.Deserialize<LinterPin[]>(
        System.IO.File.ReadAllText(".github/gate-tools.json"),
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        }
    ) ?? throw new CakeException(".github/gate-tools.json names no tools.");

// Release throughout, so the tests and the installer reuse one build. RestoreLockedMode fails a
// restore whose package graph disagrees with a packages.lock.json rather than re-resolving it.
DotNetMSBuildSettings locked = new DotNetMSBuildSettings().WithProperty(
    "RestoreLockedMode",
    "true"
);

// ///// Code /////

Task("format")
    .Description("C# formatting, through CSharpier")
    .Does(() => DotNetTool("WingetNudge.slnx", "csharpier", "check ."));

Task("prettier")
    .Description("Markdown, YAML and JSON formatting")
    .Does(() => Command(["bunx", "bunx.exe"], "--no-install prettier --check ."));

// Both configurations: everything ships from Release, and the demo inventory behind #if DEBUG
// compiles only in Debug, so a Release-only gate would never analyze or even parse it.
Task("build")
    .Description("Every project in Release and Debug, analyzer warnings as errors")
    .Does(() =>
    {
        foreach (string configuration in (string[])["Release", "Debug"])
        {
            DotNetBuild(
                "WingetNudge.slnx",
                new DotNetBuildSettings { Configuration = configuration, MSBuildSettings = locked }
            );
        }
    });

Task("tests")
    .Description("The Core suite")
    .IsDependentOn("build")
    .Does(() =>
        DotNetTest(
            "WingetNudge.slnx",
            new DotNetTestSettings
            {
                PathType = DotNetTestPathType.Solution,
                Configuration = "Release",
                NoBuild = true,
            }
        )
    );

// An empty global property outranks the thumbprint Directory.Signing.props imports, so the package
// builds unsigned on every machine. Signing belongs to a release, and this task checks it links.
Task("installer")
    .Description("The MSI, unsigned")
    .IsDependentOn("build")
    .Does(() =>
        DotNetBuild(
            "installer/WingetNudge.Installer.wixproj",
            new DotNetBuildSettings
            {
                Configuration = "Release",
                MSBuildSettings = new DotNetMSBuildSettings()
                    .WithProperty("RestoreLockedMode", "true")
                    .WithProperty("SigningCertificateThumbprint", ""),
            }
        )
    );

// ///// Policy /////

// The cooldown is the only thing between this repository and a version published minutes ago, and
// it lives in two files. Every exemption is named here, so adding one is an edit to this list.
string[] allowedCooldownExemptions =
[
    "matchDepNames=[ghcr.io/zizmorcore/zizmor] -> minimumReleaseAgeBehaviour=timestamp-optional",
];

Task("policy")
    .Description("The three-day cooldown, in renovate.json and bunfig.toml")
    .Does(() =>
    {
        using JsonDocument document = JsonDocument.Parse(
            System.IO.File.ReadAllText(".github/renovate.json")
        );
        JsonElement renovate = document.RootElement;

        // A preset can carry any rule this task then never sees, so the config owns every rule it
        // has.
        if (renovate.TryGetProperty("extends", out JsonElement _))
        {
            throw new CakeException(
                ".github/renovate.json extends a preset, whose rules this check cannot read."
            );
        }

        string? age = renovate.GetProperty("minimumReleaseAge").GetString();
        if (age != "3 days")
        {
            throw new CakeException(
                $".github/renovate.json waits {age}, and this repository waits 3 days."
            );
        }

        // Without strict, a version inside the wait still opens a pull request; it is only held
        // back from merging.
        string? filter = renovate.GetProperty("internalChecksFilter").GetString();
        if (filter != "strict")
        {
            throw new CakeException(
                $".github/renovate.json sets internalChecksFilter to {filter}, and the gate requires strict."
            );
        }

        // Rules nest, and a rule inside a rule is still a rule, so the walk recurses rather than
        // reading the top level alone.
        string[] exemptions =
        [
            .. PackageRules(renovate).Select(CooldownExemption).OfType<string>(),
        ];
        if (!exemptions.SequenceEqual(allowedCooldownExemptions, StringComparer.Ordinal))
        {
            throw new CakeException(
                $".github/renovate.json exempts [{string.Join("; ", exemptions)}] from the cooldown, and cake.cs allows [{string.Join("; ", allowedCooldownExemptions)}]."
            );
        }

        // Renovate cannot apply its own cooldown to lock file maintenance, which re-resolves every
        // transitive dependency weekly. Bun's own gate is what holds that run back, and bun ignores
        // this key in silence when it sits under another table, so the table is what is read.
        if (BunInstallSetting("minimumReleaseAge") != "259200")
        {
            throw new CakeException(
                "bunfig.toml does not set minimumReleaseAge to 259200 under [install]."
            );
        }
    });

Task("code")
    .Description("Everything continuous integration runs in its gate job")
    .IsDependentOn("format")
    .IsDependentOn("prettier")
    .IsDependentOn("policy")
    .IsDependentOn("tests")
    .IsDependentOn("installer");

// ///// Workflows /////

Task("workflows")
    .Description("actionlint and zizmor over .github, at the versions CI runs")
    .Does(() =>
    {
        string[] ci = System.IO.File.ReadAllLines(".github/workflows/ci.yml");
        foreach (LinterPin linter in linters)
        {
            RequirePinned(linter, ci);
        }

        // -pyflakes= because no Windows package manager ships pyflakes, and actionlint skips
        // that pass without a word when it is missing.
        Command(["actionlint", "actionlint.exe"], "-pyflakes=");

        // --strict-collection fails on a file zizmor cannot parse. Without it the file is dropped
        // with a warning and the run reports no findings for a workflow it never read. --offline
        // keeps a local run's findings independent of a token; CI runs the online audits. --config
        // names the committed file so ZIZMOR_CONFIG in the environment cannot swap it.
        Command(
            ["zizmor", "zizmor.exe"],
            "--no-progress --offline --strict-collection --config .github/zizmor.yml "
                + ".github/workflows"
        );
    });

Task("check").Description("The whole gate").IsDependentOn("code").IsDependentOn("workflows");

RunTarget(target);

// ///// Pins /////

void RequirePinned(LinterPin linter, string[] ci)
{
    string install = $"winget install --id {linter.Winget} --version {linter.Version} --exact";
    FilePath? executable = Context.Tools.Resolve([linter.Tool, $"{linter.Tool}.exe"]);
    if (executable is null)
    {
        throw new CakeException($"{linter.Tool} is not installed. Install it with: {install}");
    }

    int exit = StartProcess(
        executable,
        new ProcessSettings { Arguments = linter.VersionArgument, RedirectStandardOutput = true },
        out IEnumerable<string> output
    );
    string found = Regex.Match(string.Join('\n', output), @"\d+\.\d+\.\d+").Value;
    if (exit != 0 || found != linter.Version)
    {
        throw new CakeException(
            $"{linter.Tool} {found} is installed, and the gate pins {linter.Version}. Install it with: {install}"
        );
    }

    if (linter.Image is null)
    {
        return;
    }

    // Docker resolves name:tag@digest by the digest and ignores the tag, and a pinned string
    // inside a comment says nothing about the line that runs. So every reference to this image
    // on a line continuous integration executes has to be the pinned one, digest included.
    string expected = $"{linter.Image}:{linter.Version}@{linter.Digest}";
    string[] references =
    [
        .. ci.Where(line => !line.TrimStart().StartsWith('#'))
            .SelectMany(line =>
                Regex
                    .Matches(line, Regex.Escape(linter.Image) + @":\S+?@sha256:[0-9a-f]{64}")
                    .Select(match => match.Value)
            )
            .Distinct(StringComparer.Ordinal),
    ];

    if (references.Length == 0)
    {
        throw new CakeException(
            $".github/gate-tools.json pins {linter.Tool} {linter.Version}, and .github/workflows/ci.yml runs {linter.Image} nowhere."
        );
    }

    foreach (string reference in references)
    {
        if (reference != expected)
        {
            throw new CakeException(
                $".github/gate-tools.json pins {expected}, and .github/workflows/ci.yml runs {reference}."
            );
        }
    }
}

/// <summary>Every package rule in a config, including the ones nested inside a rule.</summary>
/// <param name="element">The config, or one rule of it.</param>
/// <returns>Each rule, outermost first.</returns>
IEnumerable<JsonElement> PackageRules(JsonElement element)
{
    if (!element.TryGetProperty("packageRules", out JsonElement rules))
    {
        yield break;
    }

    foreach (JsonElement rule in rules.EnumerateArray())
    {
        yield return rule;
        foreach (JsonElement nested in PackageRules(rule))
        {
            yield return nested;
        }
    }
}

/// <summary>
/// Names how a Renovate package rule weakens the cooldown, or returns null when it leaves the
/// cooldown alone. A rule that only disables updates for a dependency weakens nothing.
/// </summary>
/// <remarks>
/// The name carries every matcher the rule holds, not the first one found: a rule matches when any
/// of its matchers does, so widening an exempt rule with another matcher would otherwise keep the
/// name it was allowed under.
/// </remarks>
/// <param name="rule">One entry of packageRules.</param>
/// <returns>The matchers and the weakenings, or null.</returns>
string? CooldownExemption(JsonElement rule)
{
    string[] weakenings =
    [
        .. rule.EnumerateObject()
            .Where(property =>
                property.Name switch
                {
                    "minimumReleaseAge" => true,
                    "minimumReleaseAgeBehaviour" => property.Value.GetString()
                        != "timestamp-required",
                    "internalChecksFilter" => property.Value.GetString() != "strict",
                    _ => false,
                }
            )
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
                $"{property.Name}={(property.Value.ValueKind == JsonValueKind.Null ? "null" : property.Value.ToString())}"
            ),
    ];

    if (weakenings.Length == 0)
    {
        return null;
    }

    string[] matchers =
    [
        .. rule.EnumerateObject()
            .Where(property => property.Name.StartsWith("match", StringComparison.Ordinal))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
                $"{property.Name}=[{(property.Value.ValueKind == JsonValueKind.Array ? string.Join(",", property.Value.EnumerateArray().Select(value => value.ToString())) : property.Value.ToString())}]"
            ),
    ];

    string subject = matchers.Length == 0 ? "every dependency" : string.Join(" ", matchers);
    return $"{subject} -> {string.Join(" ", weakenings)}";
}

/// <summary>Reads one key of bunfig.toml's [install] table.</summary>
/// <remarks>
/// Bun ignores a key it does not expect, including one under the wrong table, without a word, so a
/// search of the whole file would pass on a setting that does nothing.
/// </remarks>
/// <param name="key">The key to read.</param>
/// <returns>The value as written, or null when the table does not hold that key.</returns>
string? BunInstallSetting(string key)
{
    string table = "";
    foreach (string raw in System.IO.File.ReadAllLines("bunfig.toml"))
    {
        string line = raw.Split('#')[0].Trim();
        if (line.StartsWith('[') && line.EndsWith(']'))
        {
            table = line[1..^1].Trim();
            continue;
        }

        string[] parts = line.Split('=', 2);
        if (table == "install" && parts.Length == 2 && parts[0].Trim() == key)
        {
            return parts[1].Trim();
        }
    }

    return null;
}

/// <summary>A workflow linter the gate runs, pinned to the version CI runs.</summary>
/// <param name="Tool">The executable name.</param>
/// <param name="VersionArgument">The argument that prints the version.</param>
/// <param name="Version">The pinned version.</param>
/// <param name="Winget">The winget package that installs it.</param>
/// <param name="Image">The image CI runs it from, or null when it ships inside another.</param>
/// <param name="Digest">The digest CI pins that image to, or null when there is no image.</param>
sealed record LinterPin(
    string Tool,
    string VersionArgument,
    string Version,
    string Winget,
    string? Image,
    string? Digest
);
