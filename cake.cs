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
    "ghcr.io/zizmorcore/zizmor: minimumReleaseAgeBehaviour=timestamp-optional",
];

Task("policy")
    .Description("The three-day cooldown, in renovate.json and bunfig.toml")
    .Does(() =>
    {
        using JsonDocument document = JsonDocument.Parse(
            System.IO.File.ReadAllText(".github/renovate.json")
        );
        JsonElement renovate = document.RootElement;

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

        string[] exemptions =
        [
            .. renovate
                .GetProperty("packageRules")
                .EnumerateArray()
                .Select(CooldownExemption)
                .OfType<string>(),
        ];
        if (!exemptions.SequenceEqual(allowedCooldownExemptions, StringComparer.Ordinal))
        {
            throw new CakeException(
                $".github/renovate.json exempts [{string.Join("; ", exemptions)}] from the cooldown, and cake.cs allows [{string.Join("; ", allowedCooldownExemptions)}]."
            );
        }

        // Renovate cannot apply its own cooldown to lock file maintenance, which re-resolves every
        // transitive dependency weekly. Bun's own gate is what holds that run back.
        if (
            !System
                .IO.File.ReadAllText("bunfig.toml")
                .Contains("minimumReleaseAge = 259200", StringComparison.Ordinal)
        )
        {
            throw new CakeException("bunfig.toml does not hold the same three days.");
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

/// <summary>
/// Names how a Renovate package rule weakens the cooldown, or returns null when it leaves the
/// cooldown alone. A rule that only disables updates for a dependency weakens nothing.
/// </summary>
/// <param name="rule">One entry of packageRules.</param>
/// <returns>The matcher and the weakening, or null.</returns>
string? CooldownExemption(JsonElement rule)
{
    string? weakening = null;
    if (rule.TryGetProperty("minimumReleaseAge", out JsonElement age))
    {
        weakening =
            $"minimumReleaseAge={(age.ValueKind == JsonValueKind.Null ? "null" : age.GetString())}";
    }
    else if (
        rule.TryGetProperty("minimumReleaseAgeBehaviour", out JsonElement behaviour)
        && behaviour.GetString() != "timestamp-required"
    )
    {
        weakening = $"minimumReleaseAgeBehaviour={behaviour.GetString()}";
    }

    if (weakening is null)
    {
        return null;
    }

    foreach (
        string matcher in (string[])
            ["matchDepNames", "matchPackageNames", "matchManagers", "matchDatasources"]
    )
    {
        if (rule.TryGetProperty(matcher, out JsonElement values))
        {
            string subject = string.Join(
                ",",
                values.EnumerateArray().Select(value => value.GetString())
            );
            return $"{subject}: {weakening}";
        }
    }

    return $"every dependency: {weakening}";
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
