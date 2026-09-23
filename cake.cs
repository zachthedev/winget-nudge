#:sdk Cake.Sdk
#:property NuGetLockFilePath=cake.packages.lock.json
#:property RestoreLockedMode=true
// With an apphost, the build writes an unsigned Cake.Sdk.exe beside the assembly under the temp
// directory, and dotnet run starts that. Without one, dotnet run starts the assembly through
// dotnet exec, so the gate's own code runs in the signed dotnet host.
#:property UseAppHost=false
#:package Tomlyn

using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

// The gate: every check a change must pass before it leaves the machine. Each Description says
// what its task covers, and --description lists them. The pre-push hook runs the check target.
// Continuous integration's gate job runs the tools target, which asserts the lockfile and then
// installs from it, and then the check target. The release build in cd.yml runs the installer
// target.

string target = Argument("target", "check");

// Release throughout, so the tests and the installer reuse one build. RestoreLockedMode fails a
// restore whose package graph disagrees with a packages.lock.json rather than re-resolving it.
DotNetMSBuildSettings locked = new DotNetMSBuildSettings().WithProperty("RestoreLockedMode", "true");

// What the gate knows about each tool beside the version mise.toml pins. The aqua repository is
// here because mise.lock's backend and url are the address an install fetches from, and the file a
// bump rewrites wholesale is not where the expected owner can live. The tag prefix and the asset
// for each lockfile platform finish that address, with {version} standing for the pin, so the url
// in mise.lock has to be one exact string. mise fetches the url_api asset id in its place when a
// HEAD on the url fails, and nothing offline ties that id to a release. The exact url keeps that
// HEAD on the pinned asset, and the url_replacements entry refuses the fetch when it fails anyway.
//
// Attested names the two aqua declares a signer workflow for, so mise verifies a GitHub attestation
// and records it. koalaman/shellcheck declares neither a signer workflow nor a checksums file at
// any version constraint, so its entry carries a checksum and no provenance, and asserting one
// would fail a lockfile that is correct. tamasfe/taplo declares no signer workflow either, and its
// entry carries the checksum mise.toml says how to compute.
Dictionary<string, MisePin> misePins = new(StringComparer.Ordinal)
{
    ["actionlint"] = new(
        "rhysd/actionlint",
        "v",
        new(StringComparer.Ordinal)
        {
            ["linux-x64"] = "actionlint_{version}_linux_amd64.tar.gz",
            ["windows-x64"] = "actionlint_{version}_windows_amd64.zip",
        },
        Attested: true
    ),
    ["shellcheck"] = new(
        "koalaman/shellcheck",
        "v",
        new(StringComparer.Ordinal)
        {
            ["linux-x64"] = "shellcheck-v{version}.linux.x86_64.tar.xz",
            ["windows-x64"] = "shellcheck-v{version}.zip",
        },
        Attested: false
    ),
    ["taplo"] = new(
        "tamasfe/taplo",
        "",
        new(StringComparer.Ordinal)
        {
            ["linux-x64"] = "taplo-linux-x86_64.gz",
            ["windows-x64"] = "taplo-windows-x86_64.zip",
        },
        Attested: false
    ),
    ["zizmor"] = new(
        "zizmorcore/zizmor",
        "v",
        new(StringComparer.Ordinal)
        {
            ["linux-x64"] = "zizmor-x86_64-unknown-linux-gnu.tar.gz",
            ["windows-x64"] = "zizmor-x86_64-pc-windows-msvc.zip",
        },
        Attested: true
    ),
};

// ///// Code /////

Task("format")
    .Description("C# formatting, through CSharpier")
    .Does(() =>
        DotNetTool(
            "WingetNudge.slnx",
            "csharpier",
            new ProcessArgumentBuilder().Append("check").Append("."),
            new DotNetToolSettings { ToolPath = Dotnet() }
        )
    );

// --ignore-path names .prettierignore alone, which replaces prettier's default pair, so .gitignore
// takes nothing out of the row. prettier --check names no file it checked and passes on none, so a
// --debug-check pass first lists them, with the same options, and the row refuses an empty list.
Task("prettier")
    .Description("Markdown, YAML and JSON formatting, over the files the row lists first")
    .Does(() =>
    {
        const string options = "--bun --no-install prettier --config .prettierrc --ignore-path .prettierignore";
        FilePath bunx = Bunx();
        int exit = StartProcess(
            bunx,
            new ProcessSettings
            {
                Arguments = $"{options} --debug-check .",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            out IEnumerable<string> listed,
            out IEnumerable<string> errors
        );
        foreach (string line in errors)
        {
            Information("{0}", line);
        }

        if (exit != 0)
        {
            throw new CakeException($"prettier --debug-check exited {exit} listing the files the row checks.");
        }

        RequireChecked("prettier", [.. listed.Select(line => line.Trim()).Where(line => line.Length > 0)]);
        Command(
            ["bunx", "bunx.exe"],
            $"{options} --check .",
            settingsCustomization: settings => settings.WithToolPath(bunx)
        );
    });

// taplo comes from mise like the workflow linters, so it runs from the path mise which resolves
// once the lockfile task has passed its entry. --config names the committed file so TAPLO_CONFIG
// in the environment cannot swap it. The row names every .toml file TreeFiles finds, so taplo walks
// nothing. taplo exits 0 having checked nothing when its walk finds no file, when a named file is
// missing, and when .taplo.toml excludes every named file. So the row reads the list taplo logs,
// with RUST_LOG set so no inherited filter hides it, and refuses any list but the one it named.
Task("toml")
    .Description("TOML formatting, through taplo, over every .toml file in the tree, each named to taplo")
    .IsDependentOn("lockfile")
    .Does(() =>
    {
        MiseConfig config = ReadMiseConfig("mise.toml");
        FilePath taplo = Installed(RequireMise(), "taplo", Pinned(config, "taplo"));
        string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
        string[] files =
        [
            .. TreeFiles()
                .Where(file => file.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal),
        ];
        RequireChecked("toml", files);

        ProcessArgumentBuilder arguments = new ProcessArgumentBuilder()
            .Append("--colors")
            .Append("never")
            .Append("fmt")
            .Append("--check")
            .Append("--config")
            .Append(".taplo.toml");
        foreach (string file in files)
        {
            arguments.AppendQuoted(file);
        }

        int exit = StartProcess(
            taplo,
            new ProcessSettings
            {
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["RUST_LOG"] = "info" },
            },
            out IEnumerable<string> output,
            out IEnumerable<string> errors
        );
        string[] logged = [.. errors.Concat(output)];
        foreach (string line in logged)
        {
            Information("{0}", line);
        }

        if (exit != 0)
        {
            throw new CakeException($"taplo fmt --check exited {exit}.");
        }

        System.Text.RegularExpressions.Match found = System.Text.RegularExpressions.Regex.Match(
            string.Join("\n", logged),
            @"found files total=\d+ excluded=\d+ files=\[([^\]]*)\]"
        );
        if (!found.Success)
        {
            throw new CakeException(
                "taplo exited 0 without logging the files it found, so the row cannot tell it checked any. taplo exits 0 when its file collection fails."
            );
        }

        string[] reported =
        [
            .. System
                .Text.RegularExpressions.Regex.Matches(found.Groups[1].Value, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
        string[] named =
        [
            .. files
                .Select(file => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file)).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
        if (!reported.SequenceEqual(named, StringComparer.OrdinalIgnoreCase))
        {
            throw new CakeException(
                $"taplo checked {(reported.Length == 0 ? "no file" : string.Join(", ", reported.Select(Quoted)))}, and the row named {string.Join(", ", named.Select(Quoted))}. "
                    + "taplo drops a named file its config excludes, and one it cannot find, and still exits 0."
            );
        }
    });

// Both configurations: everything ships from Release, and the demo inventory behind #if DEBUG
// compiles only in Debug, so a Release-only gate would never analyze or even parse it.
Task("build")
    .Description("Every project in Release and Debug, analyzer warnings as errors, lock files honored")
    .Does(() =>
    {
        foreach (string configuration in (string[])["Release", "Debug"])
        {
            DotNetBuild(
                "WingetNudge.slnx",
                new DotNetBuildSettings
                {
                    Configuration = configuration,
                    MSBuildSettings = locked,
                    ToolPath = Dotnet(),
                }
            );
        }
    });

Task("tests")
    .Description("The Core suite, and the versions docs/install.md restates from Directory.Packages.props")
    .IsDependentOn("build")
    .Does(() =>
        DotNetTest(
            "WingetNudge.slnx",
            new DotNetTestSettings
            {
                ToolPath = Dotnet(),
                PathType = DotNetTestPathType.Solution,
                Configuration = "Release",
                NoBuild = true,
            }
        )
    );

// An empty global property outranks the thumbprint Directory.Signing.props imports, so the package
// builds unsigned on every machine. cd.yml builds the release MSI through this task, so a release
// ships unsigned too. A signed local build runs the installer project directly, as docs/dev.md
// shows.
Task("installer")
    .Description("The MSI links, built unsigned whatever Directory.Signing.props says")
    .IsDependentOn("build")
    .Does(() =>
        DotNetBuild(
            "installer/WingetNudge.Installer.wixproj",
            new DotNetBuildSettings
            {
                ToolPath = Dotnet(),
                Configuration = "Release",
                MSBuildSettings = new DotNetMSBuildSettings()
                    .WithProperty("RestoreLockedMode", "true")
                    .WithProperty("SigningCertificateThumbprint", ""),
            }
        )
    );

Task("code")
    .Description("Everything in the gate but the workflow linters")
    .IsDependentOn("lockfile")
    .IsDependentOn("format")
    .IsDependentOn("prettier")
    .IsDependentOn("toml")
    .IsDependentOn("tests")
    .IsDependentOn("installer");

// ///// Workflows /////

// A workflow whose only finding belongs to ShellCheck. actionlint reads it clean on its own. It
// reports SC2086 over the unquoted expansion once ShellCheck runs.
const string shellCheckCanary = """
    name: canary
    on: push
    jobs:
      canary:
        runs-on: ubuntu-latest
        steps:
          - run: echo $GITHUB_REF
    """;

const string shellCheckFinding = "SC2086";

// The host each address in mise.lock has to name, and the path under it. A url elsewhere is an
// install fetching bytes from elsewhere, whatever the rest of the entry says.
const string releaseHost = "github.com";
const string releaseApiHost = "api.github.com";

// The command that rewrites mise.lock from mise.toml. lockfile_platforms in mise.toml is what makes
// a bare lock write every platform the gate reads, so no flag repeats the list here.
const string relock = "mise lock";

// The one url_replacements entry mise.toml carries. mise fetches a release asset through its API
// address in place of mise.lock's url when a HEAD on the url fails, and an asset id names no release
// offline. This entry sends that fetch to a host that cannot resolve, so the install fails. The
// tools task hands mise install the same entry through MISE_URL_REPLACEMENTS, which replaces the
// whole map every config file builds, so neither a value already in the environment nor another
// config file in the checkout can lift it there.
const string fallbackPattern = @"regex:^https://api\.github\.com/repos/[^/]+/[^/]+/releases/assets/.*$";
const string fallbackRefused = "https://url-api-fallback-refused.invalid/";

// What mise.toml's [settings] and [tool_config] hold, compared whole by the lockfile task. mise
// reads every key in both, so a key added there changes an install as surely as a key changed. The
// platform list here is also the one every mise.lock entry and every misePins asset map follows.
static TomlTable ExpectedSettings() =>
    new()
    {
        ["lockfile"] = true,
        ["lockfile_platforms"] = new TomlArray { "linux-x64", "windows-x64" },
        ["locked"] = true,
        ["locked_verify_provenance"] = true,
        ["provenance_api_failures_fatal"] = true,
        ["github_attestations"] = true,
        ["aqua"] = new TomlTable { ["github_attestations"] = true },
        ["url_replacements"] = new TomlTable { [fallbackPattern] = fallbackRefused },
    };

static TomlTable ExpectedToolConfig() => new() { ["locked"] = true };

// The one setting bunfig.toml carries: three days, in seconds, before Bun resolves a newly published
// version. The file itself says why it is committed.
const long bunMinimumReleaseAge = 259200;

static TomlTable ExpectedBunInstall() => new() { ["minimumReleaseAge"] = bunMinimumReleaseAge };

// .prettierrc, byte for byte, the same in every repository. The prettier row names the file with
// --config, and the gate refuses every other file prettier would read as config.
const string prettierConfig = "{\n  \"singleQuote\": true,\n  \"printWidth\": 120\n}\n";

// .prettierignore, byte for byte. The prettier row names it with --ignore-path, which replaces
// prettier's default pair, so .gitignore takes nothing out of the row.
const string prettierIgnore = """
    # C# is formatted by CSharpier, and prettier never sees it.

    # Build output and repository tooling.
    bin/
    obj/
    node_modules/
    TestResults/

    # Written by tools that own their format. NuGet regenerates the lock files, and release-please
    # rewrites the changelog and its manifest on every release.
    *packages.lock.json
    CHANGELOG.md
    .release-please-manifest.json

    # Prettier reads .wxs as WeChat's script language and fails on WiX's XML.
    *.wxs

    # The gate's prettier row passes --ignore-path .prettierignore and reads no .gitignore, and holds
    # this file to the text cake.cs names. These are the local paths .gitignore keeps out that prettier
    # would read: the copies the Claude Code CLI checks out, a contributor's own Claude Code settings,
    # and Visual Studio's folder.
    .claude/worktrees/
    .claude/settings.local.json
    .vs/

    """;

// .taplo.toml, byte for byte. The toml row names every file itself, and taplo still drops a named
// file this file's exclude matches.
const string taploConfig = """
    # Every TOML file this repository authors, formatted by `taplo fmt`. The gate's toml row names each
    # .toml file in the tree to `taplo fmt --check` with this file, and holds this file to the text
    # cake.cs names. mise.lock is written by `mise lock` and carries no .toml extension, so the pattern
    # leaves it alone. A run that names no file walks every directory whatever .gitignore says, so the
    # two that hold TOML files this repository does not author are excluded by name: node_modules, and
    # the worktrees the Claude Code CLI checks out under .claude/worktrees.
    include = ["**/*.toml"]
    exclude = [".claude/worktrees/**", "node_modules/**"]

    """;

// .github/zizmor.yml, byte for byte. A rule there can disable an audit or ignore a finding.
const string zizmorConfig = """
    # zizmor's settings for this repository. Every audit not named here runs at zizmor's defaults. The
    # gate holds this file to the text cake.cs names, since a rule here can disable an audit.
    rules:
      # Every action is pinned to a commit, including the ones GitHub publishes. A tag can be
      # retargeted by its owner with no pull request and no cooldown, and this is what refuses one.
      unpinned-uses:
        config:
          policies:
            '*': hash-pin
      # zizmor asks for seven days by default. The project standard is three, which is the window that
      # catches almost every package published and then pulled while still letting a legitimate release
      # land in the same week. A shorter cooldown still fails. A cooldown block removed entirely passes
      # this audit.
      dependabot-cooldown:
        config:
          days: 3

    """;

// The two mise data files, before anything installs from them. A lockfile that disagrees with its
// pin is the likeliest fault after a bump, and an address in it is what an install fetches, so both
// are read before the install rather than after. bunfig.toml, the config files the other rows read
// and the tree's other config files are read here as well, so a pull request that changes one fails
// the first row. The one process it starts is git, to list the tracked paths. Nothing here resolves
// mise, so the task needs none on the machine, and check runs it ahead of every other task.
Task("lockfile")
    .Description(
        "Every mise.toml pin recorded in mise.lock at the address cake.cs names, with no other mise config or lock file beside them, no tracked node_modules path or .env file, bunfig.toml and every config file a row reads as cake.cs holds them, and no other Prettier or npm config"
    )
    .Does(() => RequireLockfile());

// The tool install, behind the lockfile task. Continuous integration's gate job runs this target
// ahead of the whole gate. The order is a dependency in this file, so this task installs nothing
// before the assertions pass. Outside check, because a local gate runs the tools an earlier install
// put on disk. RunMise carries the environment the install leans on.
Task("tools")
    .Description("The tools mise.lock records, installed once the lockfile task has passed them")
    .IsDependentOn("lockfile")
    .Does(() =>
    {
        FilePath mise = RequireMise();
        int exit = RunMise(mise, ["install"]);
        if (exit != 0)
        {
            throw new CakeException($"mise install exited {exit}.");
        }
    });

Task("workflows")
    .Description(
        "actionlint with ShellCheck over .github/workflows, then zizmor over .github, from the paths mise resolves in locked mode"
    )
    .IsDependentOn("lockfile")
    .Does(() =>
    {
        MiseConfig config = ReadMiseConfig("mise.toml");
        string[] tools = [.. config.Versions.Keys.OrderBy(name => name, StringComparer.Ordinal)];

        FilePath mise = RequireMise();

        Dictionary<string, FilePath> resolved = new(StringComparer.Ordinal);
        foreach (string tool in tools)
        {
            resolved[tool] = Installed(mise, tool, config.Versions[tool]);
        }

        FilePath actionlint = Verified(resolved, "actionlint");

        // actionlint prints no file it checked, and with no file named it finds the workflows
        // through a .git alone, so a git archive extraction fails it. The row names every workflow
        // file under .github/workflows itself, prints them, and refuses an empty list.
        string[] workflows =
        [
            .. TreeFiles()
                .Where(file =>
                    file.StartsWith(".github/workflows/", StringComparison.Ordinal)
                    && !file[".github/workflows/".Length..].Contains('/')
                    && (
                        file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Order(StringComparer.Ordinal),
        ];
        RequireChecked("actionlint", workflows);
        Command(
            ["actionlint", "actionlint.exe"],
            $"{ProvenAnalyzers(actionlint, Verified(resolved, "shellcheck"))} {string.Join(" ", workflows.Select(file => $"\"{file}\""))}",
            settingsCustomization: settings => settings.WithToolPath(actionlint)
        );

        // --strict-collection fails on a file zizmor cannot parse. Without it the file is dropped
        // with a warning and the run reports no findings for a workflow it never read. --config
        // names the committed file so ZIZMOR_CONFIG in the environment cannot swap it. The input is
        // .github with --collect=all: zizmor collects every workflow, .github/dependabot.yml and any
        // composite action there, and reads no ignore file, so no .gitignore, .git/info/exclude or
        // global excludes line can hide one. The walk stops at .github, so node_modules and
        // .claude/worktrees are never read. zizmor logs each file it completes and exits non-zero
        // when it collects none, so this row needs no list of its own.
        //
        // The online audits read the GitHub API. zizmor given neither a token nor --offline skips
        // them and says so at debug level alone, so every run without a token names --offline. On
        // continuous integration the gate starts no gh and runs offline, and the shared workflows
        // job runs the online audits. Locally, the token gh holds goes into zizmor's process
        // settings alone, so no other process the gate starts receives it from the gate. A token
        // the shell exports reaches every process through the inherited environment, and the gate
        // clears nothing. The row prints which mode zizmor runs in and why, never the token, so a
        // log shows the mode rather than leaving it to be read from this file.
        string why = "CI is set, so the gate starts no gh, and the shared workflows job runs the online audits";
        string? token = OnContinuousIntegration() ? null : GitHubToken(out why);
        Information($"zizmor runs {(token is null ? "offline" : "online")}: {why}.");
        Command(
            ["zizmor", "zizmor.exe"],
            $"--no-progress {(token is null ? "--offline " : "")}--strict-collection --collect=all --config .github/zizmor.yml .github",
            settingsCustomization: settings =>
            {
                settings.WithToolPath(Verified(resolved, "zizmor"));
                return token is null ? settings : settings.WithEnvironmentVariable("GH_TOKEN", token);
            }
        );
    });

// lockfile first by name as well as through code, so the order is stated where the gate is
// assembled rather than left to the order code lists its own dependencies in.
Task("check")
    .Description("The whole gate")
    .IsDependentOn("lockfile")
    .IsDependentOn("code")
    .IsDependentOn("workflows");

RunTarget(target);

// ///// Pins /////

// The lockfile task's assertions. RunMise runs them again before every mise call, so no task
// order and no --exclusive run reaches mise past them.
void RequireLockfile()
{
    RequireOnlyPinnedMiseFiles();
    RequireNoRefusedTrackedPaths();
    RequireBunfig();
    RequireHeldFiles();
    RequireNoConfigElsewhere();
    MiseConfig config = ReadMiseConfig("mise.toml");
    Dictionary<string, MiseArtifact> artifacts = MiseArtifacts("mise.lock", config.Platforms, config.Versions);
    foreach (string tool in config.Versions.Keys.OrderBy(name => name, StringComparer.Ordinal))
    {
        RequireRecorded(tool, config.Versions[tool], config.Platforms, artifacts);
    }
}

// mise.toml, read as TOML: the [tools] table is where the tool versions live rather than here,
// because a formatter moves source and a pin that moves is a pin no tool can read. Both legs
// install from this file, so no version has to be asserted against another file's copy of itself.
// lockfile_platforms is the list every mise.lock entry has to carry, so a bump made on one machine
// leaves the other leg an artifact to install from. mise evaluates the templates in [env] and
// [vars] as it loads the file, even for mise --version, and runs [hooks] during an install, so the
// file holds [tools], [settings] and [tool_config] alone. [settings] and [tool_config] have to equal
// ExpectedSettings and ExpectedToolConfig whole.
MiseConfig ReadMiseConfig(string path)
{
    TomlTable table = ReadToml(path);
    string[] unknown =
    [
        .. table.Keys.Where(key => key is not ("tools" or "settings" or "tool_config")).Order(StringComparer.Ordinal),
    ];
    if (unknown.Length > 0)
    {
        throw new CakeException(
            $"{path} holds {string.Join(", ", unknown.Select(Quoted))}, and the gate takes [tools], [settings] and [tool_config] alone. "
                + "mise evaluates [env] and [vars] templates as it loads the file and runs [hooks] during an install."
        );
    }

    Dictionary<string, string> versions = new(StringComparer.Ordinal);
    if (table.TryGetValue("tools", out object? tools) && tools is TomlTable declared)
    {
        foreach (KeyValuePair<string, object> pin in declared)
        {
            // mise takes a table form too, which pins a version the gate would then never assert.
            string version =
                pin.Value as string
                ?? throw new CakeException(
                    $"{path} pins {Quoted(pin.Key)} as something other than a version string, and the gate asserts a string pin alone."
                );

            // The pin goes into the url the lockfile task builds and the path segment Installed compares.
            if (!IsReleaseVersion(version))
            {
                throw new CakeException(
                    $"{path} pins {Quoted(pin.Key)} as {Quoted(version)}, and the gate takes digit groups joined by single dots alone, such as 0.10.0."
                );
            }

            versions[pin.Key] = version;
        }
    }

    if (versions.Count == 0)
    {
        throw new CakeException($"{path} declares no tool under [tools].");
    }

    RequireWhole(path, "settings", table, ExpectedSettings(), "mise");
    RequireWhole(path, "tool_config", table, ExpectedToolConfig(), "mise");
    return new MiseConfig(versions, [.. ((TomlArray)ExpectedSettings()["lockfile_platforms"]).OfType<string>()]);
}

// One table of a config file compared whole against what the gate expects, with every key that is
// added, missing or changed named beside both values. The url_replacements entry sits in
// ExpectedSettings, so a mise.toml without the fallback refusal, or with another entry, fails here.
void RequireWhole(string path, string name, TomlTable file, TomlTable expected, string reader)
{
    TomlTable actual =
        file.TryGetValue(name, out object? value) && value is TomlTable declared ? declared : new TomlTable();
    string[] differing =
    [
        .. actual
            .Keys.Union(expected.Keys)
            .Where(key =>
                !actual.TryGetValue(key, out object? fileValue)
                || !expected.TryGetValue(key, out object? gateValue)
                || Rendered(fileValue) != Rendered(gateValue)
            )
            .Order(StringComparer.Ordinal),
    ];
    if (differing.Length > 0)
    {
        throw new CakeException(
            $"{path}'s [{name}] differs from what cake.cs takes at "
                + string.Join(
                    "; ",
                    differing.Select(key =>
                        $"{Quoted(key)}: the file has {(actual.TryGetValue(key, out object? fileValue) ? Rendered(fileValue) : "nothing")}, "
                        + $"cake.cs takes {(expected.TryGetValue(key, out object? gateValue) ? Rendered(gateValue) : "nothing")}"
                    )
                )
                + $". {reader} reads every key there, so the table has to match cake.cs whole."
        );
    }
}

// A TOML value as one line of text, with keys sorted and strings quoted, so two values render alike
// exactly when they hold the same content.
static string Rendered(object? value) =>
    value switch
    {
        string text => Quoted(text),
        bool flag => flag ? "true" : "false",
        long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TomlArray array => $"[{string.Join(", ", array.Select(Rendered))}]",
        TomlTable table =>
            $"{{{string.Join(", ", table.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{Quoted(pair.Key)} = {Rendered(pair.Value)}"))}}}",
        null => "nothing",
        _ =>
            $"{value.GetType().Name} {Quoted(System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")}",
    };

// mise.lock as mise writes it, read as TOML: [[tools.<name>]] carries the version, the specifiers
// and the backend, and each platform sits under it in a quoted dotted key. mise picks the element
// whose specifiers name the requested version, so a tool with more than one element is refused
// outright: mise.toml pins one version per tool and mise lock writes one element per pin, and the
// gate would otherwise assert one element while mise installs from another. A field the entry lacks
// reads as empty, and every assertion below reads an empty field as a refusal, so an unfamiliar
// shape stops the gate rather than passing it. A platform table lockfile_platforms does not name is
// refused the same way: an install never reads it, so nothing verifies what it records. mise also
// reads a nested [tools.<name>.platforms.<platform>] table, for any platform, and that form reaches
// this reader as a single platforms key the quoted loop never sees. mise lock writes the quoted form
// alone, so an entry carrying the nested one is refused whole. Every key is held to the set mise
// lock writes, so a field only a later mise would read, or a tool mise.toml does not pin, stops the
// gate rather than reaching an install unread.
Dictionary<string, MiseArtifact> MiseArtifacts(string path, string[] platforms, Dictionary<string, string> pinned)
{
    if (!System.IO.File.Exists(path))
    {
        throw new CakeException(
            $"{path} is missing, so nothing records which artifact a version resolved to. Write it with: mise trust; {relock}"
        );
    }

    string[] topKeys = ["lockfile_version", "tools"];
    string[] entryKeys = ["version", "backend", "specifiers", "options"];
    string[] optionKeys = ["version", "version_prefix"];
    string[] platformKeys = ["checksum", "url", "url_api", "provenance"];
    TomlTable table = ReadToml(path);
    RequireOnlyKeys(path, "the top level", table.Keys, topKeys);
    Dictionary<string, MiseArtifact> artifacts = new(StringComparer.Ordinal);
    if (!table.TryGetValue("tools", out object? tools) || tools is not TomlTable locked)
    {
        return artifacts;
    }

    RequireOnlyKeys(path, "[tools]", locked.Keys, pinned.Keys);
    foreach (KeyValuePair<string, object> tool in locked)
    {
        if (tool.Value is not TomlTableArray entries || entries.Count == 0)
        {
            continue;
        }

        if (entries.Count != 1)
        {
            throw new CakeException(
                $"{path} records {entries.Count} entries for {Quoted(tool.Key)}, and mise.toml pins one version, so {relock} writes one. Write it again with: {relock}"
            );
        }

        TomlTable entry = entries[0];
        if (entry.ContainsKey("platforms"))
        {
            throw new CakeException(
                $"{path} records a nested platforms table for {Quoted(tool.Key)}, and {relock} writes the quoted \"platforms.<name>\" form alone. Write it again with: {relock}"
            );
        }

        RequireOnlyKeys(
            path,
            $"the {Quoted(tool.Key)} entry",
            entry.Keys.Where(key => !key.StartsWith("platforms.", StringComparison.Ordinal)),
            entryKeys
        );
        if (entry.TryGetValue("options", out object? options))
        {
            RequireOnlyKeys(
                path,
                $"the {Quoted(tool.Key)} options",
                options is TomlTable optionTable ? optionTable.Keys : entryKeys.Where(key => key == "options"),
                optionKeys
            );

            // mise selects an entry on its options, so a value other than the pin, or a prefix other
            // than the tag prefix misePins names, leaves the install with no entry to take. The
            // version is required, and the prefix is held to the tag prefix when present.
            if (options is TomlTable optionValues)
            {
                RequireOption(path, tool.Key, optionValues, "version", pinned[tool.Key], "the mise.toml pin");
                if (optionValues.ContainsKey("version_prefix") && misePins.TryGetValue(tool.Key, out MisePin? tagged))
                {
                    RequireOption(
                        path,
                        tool.Key,
                        optionValues,
                        "version_prefix",
                        tagged.TagPrefix,
                        "the tag prefix cake.cs names"
                    );
                }
            }
        }

        string version = Text(entry, "version");
        if (!IsReleaseVersion(version))
        {
            throw new CakeException(
                $"{path} records {Quoted(tool.Key)} version {Quoted(version)}, and the gate takes digit groups joined by single dots alone, such as 0.10.0. Write it again with: {relock}"
            );
        }

        string backend = Text(entry, "backend");
        string[] specifiers =
            entry.TryGetValue("specifiers", out object? requested) && requested is TomlArray listed
                ? [.. listed.OfType<string>()]
                : [];
        foreach (KeyValuePair<string, object> field in entry)
        {
            if (!field.Key.StartsWith("platforms.", StringComparison.Ordinal) || field.Value is not TomlTable platform)
            {
                continue;
            }

            string name = field.Key["platforms.".Length..];
            if (!platforms.Contains(name, StringComparer.Ordinal))
            {
                throw new CakeException(
                    $"{path} records {Quoted(tool.Key)} for {Quoted(name)}, and mise.toml's lockfile_platforms names [{string.Join(", ", platforms.Select(Quoted))}]. Write it again with: {relock}"
                );
            }

            RequireOnlyKeys(path, $"the {Quoted(tool.Key)} {Quoted(name)} table", platform.Keys, platformKeys);

            artifacts[$"{tool.Key} {name}"] = new MiseArtifact(
                version,
                specifiers,
                backend,
                Text(platform, "checksum"),
                Text(platform, "provenance"),
                Text(platform, "url"),
                Text(platform, "url_api")
            );
        }
    }

    return artifacts;
}

// One value of a mise.lock entry's options, held to what the gate expects there.
static void RequireOption(string path, string tool, TomlTable options, string key, string expected, string source)
{
    string actual = options.TryGetValue(key, out object? value) ? Rendered(value) : "nothing";
    if (actual != Quoted(expected))
    {
        throw new CakeException(
            $"{path} records {Quoted(tool)} options {Quoted(key)} as {actual}, and {source} is {Quoted(expected)}. "
                + $"mise selects the entry on its options, so an install would find none. Write it again with: {relock}"
        );
    }
}

// A set of keys held to the ones the gate reads. A key outside them is refused by name, so nothing
// mise would read passes the gate unasserted.
static void RequireOnlyKeys(string path, string where, IEnumerable<string> keys, IReadOnlyCollection<string> allowed)
{
    string[] unknown = [.. keys.Where(key => !allowed.Contains(key)).Order(StringComparer.Ordinal)];
    if (unknown.Length > 0)
    {
        throw new CakeException(
            $"{path} holds {string.Join(", ", unknown.Select(Quoted))} in {where}, and the gate takes [{string.Join(", ", allowed.Select(Quoted))}] there alone. Write it again with: {relock}"
        );
    }
}

// One TOML file as the dynamic model. Tomlyn answers null for an empty document, which reads as a
// table with nothing in it, so every assertion downstream refuses it by name. A parse error names
// the file, which Tomlyn's own message does not.
static TomlTable ReadToml(string path)
{
    try
    {
        return TomlSerializer.Deserialize<TomlTable>(System.IO.File.ReadAllText(path), TomlSerializerOptions.Default)
            ?? new TomlTable();
    }
    catch (TomlException error)
    {
        throw new CakeException($"{path}: {Quoted(error.Message)}");
    }
}

// A version as the gate takes one: ASCII digit groups joined by single dots, with no empty group. A
// pin and the version mise.lock records both pass here before any url is built from them, so neither
// carries a separator, an escape, a dot segment or a prerelease tag into the url or the install path.
static bool IsReleaseVersion(string version) =>
    version.Length > 0 && version.Split('.').All(group => group.Length > 0 && group.All(char.IsAsciiDigit));

// One string field of a TOML table, or empty when the table lacks it or holds another type.
static string Text(TomlTable table, string key) =>
    table.TryGetValue(key, out object? value) && value is string text ? text : "";

// The mise that reads those two files. Continuous integration pins its version on the action line
// and verifies the download against the release's signed checksums; a local run takes whichever
// mise is on PATH, and mise itself refuses a lockfile entry it cannot verify.
FilePath RequireMise() => RequireOnPath("mise", "Install it with: winget install --id jdx.mise --exact");

// A program the gate starts by name, as the absolute path PATH names for it. Cake's own locator
// searches tools/** under the working directory before PATH, and this repository tracks a tools
// directory, so its answer could be a committed file. Empty and relative PATH entries, and every
// entry inside the checkout, are skipped. The name takes .exe, the one extension CreateProcess
// starts directly, and each program the gate starts this way ships as one.
FilePath? OnPath(string name)
{
    RequireNoToolNamedFiles();
    string root =
        System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath)
        ) + System.IO.Path.DirectorySeparatorChar;
    foreach (
        string entry in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
    )
    {
        string directory = entry.Trim().Trim('"');
        if (directory.Length == 0 || !System.IO.Path.IsPathFullyQualified(directory))
        {
            continue;
        }

        string full =
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory))
            + System.IO.Path.DirectorySeparatorChar;
        string candidate = System.IO.Path.Combine(full, $"{name}.exe");
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(candidate))
        {
            return new FilePath(candidate);
        }
    }

    return null;
}

FilePath RequireOnPath(string name, string install) =>
    OnPath(name) ?? throw new CakeException($"{name} is not on PATH. {install}");

FilePath Dotnet() => RequireOnPath("dotnet", "Install the .NET SDK global.json names.");

// bunx for the prettier row, the one bun process the gate starts. The row passes --bun, so prettier
// runs under this Bun rather than whichever node PATH names. The tracked-path refusals and the
// config checks run here as well as in the lockfile task, so no --target=prettier or --exclusive
// run reaches bunx past them.
FilePath Bunx()
{
    RequireNoRefusedTrackedPaths();
    RequireBunfig();
    RequireHeldFiles();
    RequireNoConfigElsewhere();
    return RequireOnPath("bunx", "Install Bun at the version package.json names.");
}

// Files named like a program some step starts by name, at the root or anywhere under tools. Cake's
// locator reads tools/** before PATH, and Windows searches the current directory for a bare name,
// so a committed file with one of these names could run in place of the real program. The gate
// finds its own programs through OnPath, and refuses these files for every other caller.
void RequireNoToolNamedFiles()
{
    string[] programs = ["mise", "gh", "bunx", "bun", "dotnet", "node", "git", "csharpier", "sbom-tool"];
    string[] extensions = ["", ".exe", ".bat", ".cmd", ".com"];
    IEnumerable<string> root = System.IO.Directory.EnumerateFiles(".").Select(file => System.IO.Path.GetFileName(file));
    IEnumerable<string> tools = System.IO.Directory.Exists("tools")
        ? System
            .IO.Directory.EnumerateFiles("tools", "*", System.IO.SearchOption.AllDirectories)
            .Select(file => file.Replace('\\', '/'))
        : [];
    string[] found =
    [
        .. root.Concat(tools)
            .Where(file =>
                programs.Any(program =>
                    extensions.Any(extension =>
                        System.IO.Path.GetFileName(file).Equals(program + extension, StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
            .Order(StringComparer.Ordinal),
    ];
    if (found.Length > 0)
    {
        throw new CakeException(
            $"The repository holds {string.Join(", ", found.Select(Quoted))}, named like a program the gate or a tool it starts runs by name. "
                + "Cake looks in tools before PATH, and Windows looks in the current directory, so the file could run in place of that program."
        );
    }
}

// Runs mise with an environment built here from nothing, never the inherited one, and returns its
// exit code with stdout collected when asked. No variable from a shell, an env file or a parent
// process reaches mise, so no MISE_GLOBAL_CONFIG_FILE, MISE_DATA_DIR or other mise setting can
// change what it reads. mise 2026.9.11 needs three ordinary variables on Windows: SYSTEMROOT for
// its network stack, a temp directory, and LOCALAPPDATA for its data, cache and state. It needs no
// PATH. SYSTEMROOT and LOCALAPPDATA come from the known folders rather than this process's
// environment. HTTPS_PROXY, HTTP_PROXY and NO_PROXY pass through when set, and Windows reads each
// name in either case. No certificate override passes. The lockfile task's assertions run first,
// so no task order and no --exclusive run reaches mise past them.
//
// mise.toml sets the first four mise settings as well, and the environment repeats them so no
// config file can lift them: locked mode, the lockfile read, re-verifying each attestation against
// the artifact mise.lock records, and the url_api fallback refused. The next four leave mise
// reading mise.toml alone. Each shuts a file the other three leave read:
// - MISE_OVERRIDE_CONFIG_FILENAMES: every other config file.
// - MISE_OVERRIDE_TOOL_VERSIONS_FILENAMES: .tool-versions.
// - MISE_ENV set empty: the env file a .miserc.toml names.
// - MISE_AUTO_ENV: the platform file a .miserc.toml turns auto_env on for.
// MISE_TRUSTED_CONFIG_PATHS trusts the checkout, whose mise.toml the assertions above have just
// held to the tables that run nothing.
int RunMise(FilePath mise, string[] arguments, List<string>? output = null)
{
    RequireLockfile();
    string root = Context.Environment.WorkingDirectory.FullPath;
    string temp = System.IO.Path.GetTempPath();
    System.Diagnostics.ProcessStartInfo start = new(mise.FullPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = output is not null,
        WorkingDirectory = root,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    start.Environment.Clear();
    start.Environment["SYSTEMROOT"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
    start.Environment["LOCALAPPDATA"] = System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData
    );
    start.Environment["TEMP"] = temp;
    start.Environment["TMP"] = temp;
    foreach (string proxy in (string[])["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"])
    {
        if (System.Environment.GetEnvironmentVariable(proxy) is string address)
        {
            start.Environment[proxy] = address;
        }
    }

    start.Environment["MISE_LOCKED"] = "1";
    start.Environment["MISE_LOCKFILE"] = "1";
    start.Environment["MISE_LOCKED_VERIFY_PROVENANCE"] = "1";
    start.Environment["MISE_URL_REPLACEMENTS"] = JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [fallbackPattern] = fallbackRefused }
    );
    start.Environment["MISE_OVERRIDE_CONFIG_FILENAMES"] = "mise.toml";
    start.Environment["MISE_OVERRIDE_TOOL_VERSIONS_FILENAMES"] = "none";
    start.Environment["MISE_ENV"] = "";
    start.Environment["MISE_AUTO_ENV"] = "false";
    start.Environment["MISE_TRUSTED_CONFIG_PATHS"] = root;

    using System.Diagnostics.Process process =
        System.Diagnostics.Process.Start(start)
        ?? throw new CakeException($"mise did not start from {Quoted(mise.FullPath)}.");
    if (output is not null)
    {
        while (process.StandardOutput.ReadLine() is string line)
        {
            output.Add(line);
        }
    }

    process.WaitForExit();
    return process.ExitCode;
}

// Every mise config or lock file at the root besides mise.toml and mise.lock. mise run here loads
// config from this directory, from its .config, mise and .mise subdirectories and from the
// directories above it, and merges each config file's sibling lockfile ahead of mise.lock. A
// mise.local.toml beside a mise.local.lock would then install from a url the lockfile task never
// read. The match is on the mise and .mise prefixes every such name carries, at the root and
// under .config, so a name a later mise adds is refused as well. .tool-versions carries neither
// prefix, so it is refused by name. The walk is the file system, not git, because mise reads a
// file whether git tracks it or not. No other subdirectory is read by a mise run here, so none is
// walked, and the directories above the checkout are outside what a pull request can write. A name
// with a trailing dot, a trailing space or a stream suffix still starts with the prefix it carries,
// and mise opens the plain names alone, so no name is normalized first.
//
// A symbolic link or junction at the root, or under .config, .mise or mise, is refused as well. mise
// and every check here follow one to wherever it points, so the file behind it is one this walk
// never names.
void RequireOnlyPinnedMiseFiles()
{
    static bool MiseNamed(string name) =>
        name.StartsWith("mise", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".mise", StringComparison.OrdinalIgnoreCase);

    string[] linkRoots = [".", ".config", ".mise", "mise"];
    string[] linked =
    [
        .. linkRoots
            .Where(directory =>
                directory == "."
                || (System.IO.Directory.Exists(directory) && new System.IO.DirectoryInfo(directory).LinkTarget is null)
            )
            .SelectMany(directory =>
                new System.IO.DirectoryInfo(directory)
                    .EnumerateFileSystemInfos()
                    .Where(entry => entry.LinkTarget is not null)
                    .Select(entry => directory == "." ? entry.Name : $"{directory}/{entry.Name}")
            )
            .Order(StringComparer.Ordinal),
    ];
    if (linked.Length > 0)
    {
        throw new CakeException(
            $"The repository holds the link {string.Join(", ", linked.Select(Quoted))}, and the gate takes no symbolic link or junction at the root or under .config, .mise or mise. "
                + "mise follows a link to wherever it points, so the file it reads is one the gate never named."
        );
    }

    string[] root =
    [
        .. System
            .IO.Directory.EnumerateFileSystemEntries(".")
            .Select(entry => System.IO.Path.GetFileName(entry))
            .Where(name =>
                (
                    MiseNamed(name)
                    && !name.Equals("mise.toml", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("mise.lock", StringComparison.OrdinalIgnoreCase)
                ) || name.Equals(".tool-versions", StringComparison.OrdinalIgnoreCase)
            ),
    ];
    string[] config = System.IO.Directory.Exists(".config")
        ?
        [
            .. System
                .IO.Directory.EnumerateFileSystemEntries(".config")
                .Select(entry => $".config/{System.IO.Path.GetFileName(entry)}")
                .Where(name => MiseNamed(name[".config/".Length..])),
        ]
        : [];
    string[] found = [.. root.Concat(config).Order(StringComparer.Ordinal)];
    if (found.Length > 0)
    {
        throw new CakeException(
            $"The repository root holds {string.Join(", ", found.Select(Quoted))} beside mise.toml and mise.lock. "
                + "mise reads each one, and merges a lockfile beside it ahead of mise.lock, so an install could fetch a url the gate never read. "
                + "Remove them, and keep local mise settings in mise's global config."
        );
    }
}

// Every tracked path with a node_modules segment, in any case, since NTFS reads NODE_MODULES as the
// same directory. bunx --no-install starts node_modules/.bin/<name> ahead of anything else, and bun
// install keeps a package it finds already at the version bun.lock records, so a committed
// node_modules/prettier still runs as prettier after the install. A tracked .env or .env.<name> at
// the root is refused as well: Bun loads one into every process it starts, prettier and commitlint
// included, and no bunx flag stops it. git answers what is tracked, so the node_modules an install
// writes, and a contributor's own untracked .env, pass. An extraction from git archive has no .git at
// the root and tracks nothing, so the check starts no git there and passes. No GIT_ variable reaches
// git, so the repository and index it reads are the checkout's own, never ones a shell or hook
// exported.
void RequireNoRefusedTrackedPaths()
{
    string root = Context.Environment.WorkingDirectory.FullPath;
    string dotGit = System.IO.Path.Combine(root, ".git");
    if (!System.IO.Directory.Exists(dotGit) && !System.IO.File.Exists(dotGit))
    {
        Information("No .git at the root, so nothing is tracked and no node_modules path or .env file is refused.");
        return;
    }

    FilePath git = RequireOnPath("git", "Install it with: winget install --id Git.Git --exact");
    System.Diagnostics.ProcessStartInfo start = new(git.FullPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        WorkingDirectory = root,
    };
    start.ArgumentList.Add("ls-files");
    start.ArgumentList.Add("-z");
    foreach (
        string name in start
            .Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            .ToArray()
    )
    {
        start.Environment.Remove(name);
    }

    using System.Diagnostics.Process process =
        System.Diagnostics.Process.Start(start)
        ?? throw new CakeException($"git did not start from {Quoted(git.FullPath)}.");
    string listed = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new CakeException(
            $"git ls-files exited {process.ExitCode} beside a .git at the root, so the gate cannot tell which paths are tracked."
        );
    }

    string[] tracked = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    string[] found =
    [
        .. tracked
            .Where(path =>
                path.Split('/').Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            )
            .Order(StringComparer.Ordinal),
    ];
    if (found.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", found.Take(5).Select(Quoted))}{(found.Length > 5 ? $" and {found.Length - 5} more" : "")} under node_modules, and the gate takes no tracked node_modules path. "
                + "bunx starts node_modules/.bin before anything else, and bun install keeps a package already at the version bun.lock records, so a committed file there runs in place of prettier."
        );
    }

    string[] envFiles =
    [
        .. tracked
            .Where(path =>
                !path.Contains('/')
                && (
                    path.Equals(".env", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Order(StringComparer.Ordinal),
    ];
    if (envFiles.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", envFiles.Select(Quoted))} at the root, and the gate takes no tracked .env file. "
                + "Bun loads one into every process it starts, prettier and commitlint included, and no bunx flag stops it."
        );
    }
}

// bunfig.toml, held whole to the one setting it carries. Bun reads the file in the working
// directory on every start, and no flag stops it. A top-level preload runs a module before the
// first line of whatever Bun starts, and the prettier row's bunx --bun starts prettier under Bun.
// Every other key reaches Bun as well: an [install] registry moves where even a frozen install
// downloads from. So the file is read twice. Tomlyn's model has to hold [install] alone, with
// minimumReleaseAge alone, at bunMinimumReleaseAge. Then the lines themselves, less comments and
// blank ones, have to read exactly the two lines that model writes, in printable ASCII with LF or
// CRLF endings. Bun's parser and Tomlyn could read a duplicate key, a dotted or quoted key, another
// number form, a byte-order mark or a bare carriage return two ways, so each is refused rather than
// resolved.
void RequireBunfig()
{
    const string path = "bunfig.toml";
    string[] expected = ["[install]", $"minimumReleaseAge = {bunMinimumReleaseAge}"];
    string why =
        "Bun runs a top-level preload module before the first line of whatever it starts, and reads every other key there as well.";
    if (!System.IO.File.Exists(path))
    {
        throw new CakeException(
            $"{path} is missing, and it carries the cooldown Bun resolves under: {string.Join(" then ", expected.Select(Quoted))}."
        );
    }

    byte[] bytes = System.IO.File.ReadAllBytes(path);
    int stray = Enumerable
        .Range(0, bytes.Length)
        .FirstOrDefault(
            index =>
                bytes[index] switch
                {
                    (byte)'\t' or (byte)'\n' => false,
                    (byte)'\r' => index + 1 >= bytes.Length || bytes[index + 1] != (byte)'\n',
                    >= 0x20 and <= 0x7E => false,
                    _ => true,
                },
            -1
        );
    if (stray >= 0)
    {
        throw new CakeException(
            $"{path} holds the byte 0x{bytes[stray]:X2} at offset {stray}, and the gate takes printable ASCII, tabs and line endings alone there, so Bun and the gate read one file."
        );
    }

    TomlTable table = ReadToml(path);
    string[] unknown = [.. table.Keys.Where(key => key != "install").Order(StringComparer.Ordinal)];
    if (unknown.Length > 0)
    {
        throw new CakeException(
            $"{path} holds {string.Join(", ", unknown.Select(Quoted))} at the top level, and the gate takes [install] alone. {why}"
        );
    }

    RequireWhole(path, "install", table, ExpectedBunInstall(), "Bun");

    string[] lines =
    [
        .. System
            .Text.Encoding.ASCII.GetString(bytes)
            .Split('\n')
            .Select(line => (line.IndexOf('#') is int hash and >= 0 ? line[..hash] : line).Trim())
            .Where(line => line.Length > 0),
    ];
    if (!lines.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new CakeException(
            $"{path} reads {string.Join(" then ", lines.Select(Quoted))} once comments are set aside, and the gate takes {string.Join(" then ", expected.Select(Quoted))} alone. "
                + "A duplicate, dotted or quoted key, or another way of writing the number, is a line Bun could read otherwise."
        );
    }
}

// Every config file a row reads that could load code or narrow what the row checks, each byte for
// byte against the text cake.cs holds for it. prettier runs the modules .prettierrc names, and a
// line in .prettierignore takes files out of the prettier row. .taplo.toml's exclude takes files out
// of the toml row, and a rule in .github/zizmor.yml can disable an audit or ignore a finding. So any
// change to one of these texts is refused rather than read. .github/actionlint.yaml, in either
// extension, is refused outright: its paths block ignores actionlint's errors by pattern, and the
// repository carries none.
void RequireHeldFiles()
{
    (string Path, string Text, string Why)[] held =
    [
        (".prettierrc", prettierConfig, "prettier runs the modules a config names"),
        (".prettierignore", prettierIgnore, "a line there takes files out of the prettier row"),
        (".taplo.toml", taploConfig, "its exclude takes files out of the toml row"),
        (".github/zizmor.yml", zizmorConfig, "a rule there can disable an audit or ignore a finding"),
    ];
    foreach ((string path, string text, string why) in held)
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] actual = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : [];
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new CakeException(
                $"{path} is {(actual.Length == 0 ? "missing or empty" : Quoted(System.Text.Encoding.UTF8.GetString(actual)))}, and the gate takes the text cake.cs holds for it alone, {Quoted(text)}. "
                    + $"{why[..1].ToUpperInvariant()}{why[1..]}, so the file has to match cake.cs byte for byte."
            );
        }
    }

    string[] actionlintConfig =
    [
        .. ((string[])[".github/actionlint.yaml", ".github/actionlint.yml"]).Where(System.IO.File.Exists),
    ];
    if (actionlintConfig.Length > 0)
    {
        throw new CakeException(
            $"The repository holds {string.Join(", ", actionlintConfig.Select(Quoted))}, and the gate takes no actionlint config. "
                + "Its paths block ignores actionlint's errors by pattern, so a workflow it names is never checked."
        );
    }
}

// Every other file an editor's prettier or bun could read as config, anywhere in the tree. The
// names are CONFIG_FILES in prettier 3.9.8's src/config/prettier-config/config-searcher.js: a
// package.json or package.yaml carrying a prettier key, then .prettierrc and the files below. A
// .prettierrc away from the root is refused as well, and so is every package.yaml, since the gate
// reads no YAML. A package.json is refused when it names prettier, or when it does not read as a
// JSON object, since Bun's reader takes more than JSON does. A .npmrc moves where bun install
// downloads from. The prettier row passes --config, so the gate's own prettier searches for none of
// these, and the refusal is for an editor's prettier and a contributor's own bun install. The walk
// is TreeFiles, because an editor reads an untracked file too, and every name matches without regard
// to case, as NTFS does.
void RequireNoConfigElsewhere()
{
    string[] prettierFiles =
    [
        ".prettierrc",
        ".prettierrc.json",
        ".prettierrc.yml",
        ".prettierrc.yaml",
        ".prettierrc.json5",
        ".prettierrc.js",
        ".prettierrc.ts",
        ".prettierrc.mjs",
        ".prettierrc.mts",
        ".prettierrc.cjs",
        ".prettierrc.cts",
        ".prettierrc.toml",
        "prettier.config.js",
        "prettier.config.ts",
        "prettier.config.mjs",
        "prettier.config.mts",
        "prettier.config.cjs",
        "prettier.config.cts",
    ];
    string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
    string[] found =
    [
        .. TreeFiles()
            .Where(relative =>
            {
                string name = System.IO.Path.GetFileName(relative);
                return name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("package.yaml", StringComparison.OrdinalIgnoreCase)
                    || (
                        prettierFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !(relative == ".prettierrc" && name == ".prettierrc")
                    )
                    || (
                        name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                        && NamesPrettier(System.IO.Path.Combine(root, relative))
                    );
            })
            .Select(Quoted)
            .Order(StringComparer.Ordinal),
    ];
    if (found.Length > 0)
    {
        throw new CakeException(
            $"The tree holds {string.Join(", ", found)}, and the gate takes no config for prettier but the root .prettierrc, and no .npmrc. "
                + "prettier runs the modules a config file names, and a .npmrc moves where bun install downloads from."
        );
    }
}

// Every file in the tree, relative to the root with forward slashes, for the refusals and rows that
// read the tree rather than handing a tool a directory. It skips node_modules, bin, obj and .git
// wherever they sit, and .claude/worktrees and .vs at the root, where agents and IDEs write. A
// directory link is refused, since the files behind it are ones no check here would name. A
// directory the gate cannot list, or one deleted while the walk reads it, is refused by name as
// well, so a row fails rather than checking a shorter list than the tree holds.
List<string> TreeFiles()
{
    string[] skipped = ["node_modules", "bin", "obj", ".git"];
    string[] skippedAtRoot = [".claude/worktrees", ".vs"];
    string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
    System.IO.EnumerationOptions options = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
    List<string> files = [];
    List<string> refused = [];
    Stack<string> pending = new([root]);
    while (pending.TryPop(out string? directory))
    {
        string here = System.IO.Path.GetRelativePath(root, directory).Replace('\\', '/');
        System.IO.FileSystemInfo[] entries;
        try
        {
            entries = [.. new System.IO.DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)];
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.IO.IOException)
        {
            refused.Add($"{Quoted(here)} (unreadable: {error.GetType().Name})");
            continue;
        }

        foreach (System.IO.FileSystemInfo entry in entries)
        {
            string relative = System.IO.Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (entry is not System.IO.DirectoryInfo)
            {
                files.Add(relative);
                continue;
            }

            if (
                skipped.Contains(entry.Name, StringComparer.Ordinal)
                || skippedAtRoot.Contains(relative, StringComparer.Ordinal)
            )
            {
                continue;
            }

            if (entry.LinkTarget is not null)
            {
                refused.Add($"{Quoted(relative)} (a directory link)");
                continue;
            }

            pending.Push(entry.FullName);
        }
    }

    if (refused.Count > 0)
    {
        throw new CakeException(
            $"The tree holds {string.Join(", ", refused.Order(StringComparer.Ordinal))}, and the gate reads every directory it does not skip. "
                + "The files behind a link or an unreadable directory are ones no check here would name."
        );
    }

    return files;
}

// The files a row hands its tool, printed, and refused when there are none. A tool given nothing to
// check can exit 0, and a row that checked nothing proves nothing.
void RequireChecked(string row, IReadOnlyCollection<string> files)
{
    if (files.Count == 0)
    {
        throw new CakeException($"The {row} row found no file to check, and a row that checks nothing proves nothing.");
    }

    Information("{0} checks {1} files: {2}", row, files.Count, string.Join(", ", files));
}

// Whether a package.json names a prettier config for prettier to load, as far as the gate can tell:
// a prettier key at the top, or a file that does not read as a JSON object at all.
static bool NamesPrettier(string path)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Any(property => property.Name == "prettier");
    }
    catch (JsonException)
    {
        return true;
    }
}

// A value read from mise.toml, mise.lock or mise's own output, as every message echoes one: in double
// quotes, with a quote or backslash escaped and every character outside printable ASCII written as
// \uXXXX, cut to 200 characters. A crafted value then reaches the terminal as text and cannot pass
// as part of the message or move the cursor.
static string Quoted(string value) =>
    "\""
    + string.Concat(
        (value.Length > 200 ? value[..200] : value).Select(character =>
            character switch
            {
                '"' or '\\' => $"\\{character}",
                >= ' ' and <= '~' => character.ToString(),
                _ => $"\\u{(int)character:X4}",
            }
        )
    )
    + (value.Length > 200 ? "\"..." : "\"");

// What mise.lock has to say about one pinned tool before anything installs from it. Every branch
// here reads the two data files alone, so a bump that left the lockfile behind is reported by
// name on a machine with no mise at all.
void RequireRecorded(string tool, string version, string[] platforms, Dictionary<string, MiseArtifact> artifacts)
{
    if (!misePins.TryGetValue(tool, out MisePin? pin))
    {
        throw new CakeException(
            $"mise.toml declares {Quoted(tool)}, and cake.cs records no pin for it. Add its aqua repository, tag prefix and assets to misePins."
        );
    }

    // Every lockfile platform needs the asset this file expects there. An asset for a platform the
    // list does not name is an expectation nothing reads, so it is refused too.
    foreach (string platform in platforms)
    {
        if (!pin.Assets.ContainsKey(platform))
        {
            throw new CakeException(
                $"mise.toml's lockfile_platforms names {Quoted(platform)}, and cake.cs names no {Quoted(platform)} asset for {tool}. Add it to misePins."
            );
        }
    }

    foreach (string named in pin.Assets.Keys)
    {
        if (!platforms.Contains(named, StringComparer.Ordinal))
        {
            throw new CakeException(
                $"cake.cs names a {named} asset for {tool}, and mise.toml's lockfile_platforms does not name {named}. Remove it from misePins."
            );
        }
    }

    string backend = $"aqua:{pin.Repository}";
    foreach (string platform in platforms)
    {
        // A platform the lockfile lacks reads as an entry with every field empty, so the checksum
        // assertion is what refuses it.
        MiseArtifact artifact = artifacts.TryGetValue($"{tool} {platform}", out MiseArtifact? recorded)
            ? recorded
            : new MiseArtifact("", [], "", "", "", "", "");

        if (artifact.Checksum.Length == 0)
        {
            throw new CakeException(
                $"mise.lock records {tool} {version} for {platform} with no checksum, so an install from it verifies nothing. Write it again with: {relock}"
            );
        }

        // specifiers is the field mise selects an entry on, and version is what it installs, so
        // both have to be the one pin exactly.
        if (artifact.Version != version || !artifact.Specifiers.SequenceEqual([version], StringComparer.Ordinal))
        {
            throw new CakeException(
                $"mise.toml pins {tool} {version}, and mise.lock records version {Quoted(artifact.Version)} with specifiers [{string.Join(", ", artifact.Specifiers.Select(Quoted))}]. Write it again with: {relock}"
            );
        }

        // The backend decides which registry entry the artifact comes from, and the two addresses
        // are what the bytes arrive over. Every one of them is asserted against misePins in this
        // file, so a rewrite confined to mise.lock cannot move an install to another owner or another
        // version.
        if (artifact.Backend != backend)
        {
            throw new CakeException(
                $"cake.cs resolves {tool} through {backend}, and mise.lock records backend {Quoted(artifact.Backend)}. Write it again with: {relock}"
            );
        }

        // The url is compared whole against the address this file builds, so no URL parser stands
        // between the check and the text mise reads.
        string asset = pin.Assets[platform].Replace("{version}", version, StringComparison.Ordinal);
        string url = $"https://{releaseHost}/{pin.Repository}/releases/download/{pin.TagPrefix}{version}/{asset}";
        RequireAddressText(tool, platform, "url", artifact.Url);
        if (!string.Equals(artifact.Url, url, StringComparison.Ordinal))
        {
            throw new CakeException(
                $"mise.lock records {tool} {platform} url as {Quoted(artifact.Url)}, and cake.cs builds {url} from the pin. "
                    + $"An install fetches the url, so the gate takes that address alone. Write it again with: {relock}"
            );
        }

        // An asset id names no release offline, so url_api is held to its shape alone: one asset of
        // the tool's own repository. mise reaches it only when a HEAD on the url above fails, and the
        // url_replacements entry refuses that fetch.
        string assets = $"https://{releaseApiHost}/repos/{pin.Repository}/releases/assets/";
        RequireAddressText(tool, platform, "url_api", artifact.UrlApi);
        if (
            !artifact.UrlApi.StartsWith(assets, StringComparison.Ordinal)
            || artifact.UrlApi.Length == assets.Length
            || !artifact.UrlApi[assets.Length..].All(char.IsAsciiDigit)
        )
        {
            throw new CakeException(
                $"mise.lock records {tool} {platform} url_api as {Quoted(artifact.UrlApi)}, and the gate takes {assets} followed by an asset id alone. "
                    + $"mise fetches it when a HEAD on the url fails. Write it again with: {relock}"
            );
        }

        // The aqua entries for ShellCheck and taplo declare no signer workflow, so mise has no
        // attestation to verify for either and each entry carries a checksum alone.
        if (!pin.Attested)
        {
            continue;
        }

        // mise lock downloads the artifact and writes provenance only after the attestation
        // verifies against the signer workflow, so the field's presence is the verified signal.
        // An attestation the release does not carry leaves the field out rather than failing the
        // lock, which is the case this assertion exists for.
        if (artifact.Provenance != "github-attestations")
        {
            throw new CakeException(
                $"aqua declares a signer workflow for {tool}, and mise.lock records {platform} provenance {Quoted(artifact.Provenance)}. Write it again with: {relock}"
            );
        }
    }
}

// The raw text of one address mise.lock hands an install, checked before anything compares it. A
// control or whitespace character, a percent escape, a backslash and a dot segment are each text one
// parser rewrites and another reads as written. A check and an install could then disagree on where
// the bytes come from. An https URL parser reads a backslash as a path separator, and the dot segment
// check splits on '/' alone. mise writes none of them, and it writes both addresses into every entry
// it locks, so an empty one is refused as well.
void RequireAddressText(string tool, string platform, string field, string address)
{
    if (address.Length == 0)
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} with no {field}, and mise writes one into every entry it locks. Write it again with: {relock}"
        );
    }

    for (int index = 0; index < address.Length; index++)
    {
        if (char.IsControl(address[index]) || char.IsWhiteSpace(address[index]))
        {
            throw new CakeException(
                $"mise.lock records {tool} {platform} {field} with U+{(int)address[index]:X4} at index {index}, and an address mise writes carries no control or whitespace character. Write it again with: {relock}"
            );
        }
    }

    if (address.Contains('%', StringComparison.Ordinal))
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as {Quoted(address)}, and an address mise writes carries no percent escape. Write it again with: {relock}"
        );
    }

    if (address.Contains('\\', StringComparison.Ordinal))
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as {Quoted(address)}, and an address mise writes carries no backslash. Write it again with: {relock}"
        );
    }

    if (address.Split('/').Any(segment => segment is "." or ".."))
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as {Quoted(address)}, and an address mise writes carries no . or .. segment. Write it again with: {relock}"
        );
    }
}

// The version mise.toml pins for a tool the gate runs by name.
string Pinned(MiseConfig config, string tool) =>
    config.Versions.TryGetValue(tool, out string? version)
        ? version
        : throw new CakeException($"The gate runs {tool}, and mise.toml declares no such tool.");

// Hands back the file mise resolved for the tool, so a caller runs that path rather than resolving
// the name a second time and trusting the two answers to match. mise which honors
// MISE_<TOOL>_VERSION from the environment in locked mode too, and answers with any install of that
// version already on disk. On Windows, mise puts each tool the gate runs directly in
// <tool>/<version>. The path has to end in the tool, the pinned version and the file. The pair is
// read at the end because the data directory above it is whatever the environment names.
FilePath Installed(FilePath mise, string tool, string version)
{
    List<string> located = [];
    int lookup = RunMise(mise, ["which", tool], located);
    string resolved = string.Join('\n', located).Trim();
    if (lookup != 0 || resolved.Length == 0)
    {
        throw new CakeException($"mise which {tool} found nothing. Install it with: mise install");
    }

    string[] segments = resolved.Split(['/', '\\']);
    if (segments.Length < 3 || segments[^3] != tool || segments[^2] != version)
    {
        throw new CakeException(
            $"mise which {tool} resolved {Quoted(resolved)}, and mise.toml pins {tool} {version}, so the gate refuses to run it. "
                + $"The gate runs a path ending in {tool}, {version} and the file alone. "
                + $"Something in the environment, such as MISE_{tool.ToUpperInvariant()}_VERSION, chose another install."
        );
    }

    return new FilePath(resolved);
}

FilePath Verified(Dictionary<string, FilePath> resolved, string tool) =>
    resolved.TryGetValue(tool, out FilePath? executable)
        ? executable
        : throw new CakeException($"The workflows task runs {tool}, and mise.toml declares no such tool.");

// GitHub Actions sets CI to true in every step. Any value but empty, false or 0 counts, so a runner
// that spells it another way still keeps the gate from starting gh.
bool OnContinuousIntegration() =>
    EnvironmentVariable("CI") is { Length: > 0 } value
    && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
    && value != "0";

// The token gh holds for api.github.com, or null when gh is absent or holds none, with why in words
// that never carry the token. Only a local run asks. gh answers from GH_TOKEN before its keyring,
// so a token the shell exports comes back here as well. The output is redirected and the process
// runs silent, so the token reaches zizmor's environment and no log.
string? GitHubToken(out string why)
{
    FilePath? gh = OnPath("gh");
    if (gh is null)
    {
        why = "gh is not on PATH";
        return null;
    }

    int exit = StartProcess(
        gh,
        new ProcessSettings
        {
            Arguments = "auth token",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Silent = true,
        },
        out IEnumerable<string> printed
    );
    string token = string.Join("", printed).Trim();
    if (exit != 0 || token.Length == 0)
    {
        why = $"gh auth token exited {exit} without a token";
        return null;
    }

    why = "gh auth token answered, and the token goes to zizmor's process alone";
    return token;
}

// The arguments actionlint lints .github with, returned once actionlint has reported a ShellCheck
// finding with them. -shellcheck names the file the version check resolved. The pinned binary and
// the binary actionlint starts are therefore one path, not two lookups. -pyflakes= because no
// Windows package manager ships pyflakes, and actionlint skips that pass without a word when it is
// missing. A ShellCheck actionlint cannot start leaves the shellcheck rule off and the exit code 0.
// The canary is what gives a clean actionlint run any weight.
string ProvenAnalyzers(FilePath actionlint, FilePath shellcheck)
{
    string analyzers = $"-pyflakes= -shellcheck=\"{shellcheck.FullPath}\"";
    FilePath canary = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"actionlint-shellcheck-canary-{Guid.NewGuid():N}.yaml"
    );

    try
    {
        System.IO.File.WriteAllText(canary.FullPath, shellCheckCanary);
        int exit = StartProcess(
            actionlint,
            new ProcessSettings
            {
                Arguments = $"{analyzers} \"{canary.FullPath}\"",
                RedirectStandardOutput = true,
                Silent = true,
            },
            out IEnumerable<string> reported
        );

        string said = string.Join('\n', reported).Trim();
        if (!said.Contains(shellCheckFinding, StringComparison.Ordinal))
        {
            throw new CakeException(
                $"actionlint found no {shellCheckFinding} in a script that carries one, so ShellCheck never ran. "
                    + "No run: block under .github/workflows was checked. "
                    + $"actionlint exited {exit} saying: {(said.Length == 0 ? "nothing" : said)}. "
                    + $"Check that {shellcheck.FullPath} starts."
            );
        }
    }
    finally
    {
        System.IO.File.Delete(canary.FullPath);
    }

    return analyzers;
}

/// <summary>What mise.lock records for one tool on one platform.</summary>
/// <param name="Version">The version the tool entry carries.</param>
/// <param name="Backend">The backend the tool entry resolves through, empty when it carries none.</param>
/// <param name="Checksum">The artifact hash, empty when the entry carries none.</param>
/// <param name="Provenance">The method that verified the artifact, empty when none did.</param>
/// <param name="Url">The address an install downloads from, empty when the entry carries none.</param>
/// <param name="UrlApi">The release asset address, empty when the entry carries none.</param>
sealed record MiseArtifact(
    string Version,
    string[] Specifiers,
    string Backend,
    string Checksum,
    string Provenance,
    string Url,
    string UrlApi
);

/// <summary>What cake.cs knows about one pinned tool, beside the version mise.toml carries.</summary>
/// <param name="Repository">The GitHub repository aqua resolves the artifact from, as owner/name.</param>
/// <param name="TagPrefix">What the release tag carries ahead of the version.</param>
/// <param name="Assets">The release asset mise.lock fetches on each platform, with {version} for the pin.</param>
/// <param name="Attested">Whether aqua declares a signer workflow, so mise records provenance.</param>
sealed record MisePin(string Repository, string TagPrefix, Dictionary<string, string> Assets, bool Attested);

/// <summary>What mise.toml declares: the pinned version of each tool, and the platforms the lockfile carries.</summary>
/// <param name="Versions">Tool name to the version mise.toml pins.</param>
/// <param name="Platforms">The lockfile_platforms list every mise.lock entry has to carry.</param>
sealed record MiseConfig(Dictionary<string, string> Versions, string[] Platforms);
