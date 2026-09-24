#:sdk Cake.Sdk
#:property NuGetLockFilePath=cake.packages.lock.json
#:property RestoreLockedMode=true
// With an apphost, the build writes an unsigned Cake.Sdk.exe beside the assembly under the temp
// directory, and dotnet run starts that. Without one, dotnet run starts the assembly through
// dotnet exec, so the gate's own code runs in the signed dotnet host.
#:property UseAppHost=false
#:package Tomlyn
#:package YamlDotNet

using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.RepresentationModel;

// The gate: every check a change must pass before it leaves the machine. Each Description says
// what its task covers, and --description lists them. The pre-push hook runs the check target.
// Continuous integration's gate job runs the tools target, which asserts the lockfile and then
// installs from it, and then the check target. The release build in cd.yml runs the installer
// target.

// actionlint starts this program again as its ShellCheck, through the -shellcheck value
// ShellCheckStandIn builds. The branch runs before any Cake alias, so Cake never reads the
// arguments actionlint hands ShellCheck.
const string shellCheckStandInArgument = "--shellcheck-stand-in";
if (args is [shellCheckStandInArgument, string standInShellCheck, .. string[] standInArguments])
{
    Environment.Exit(RunShellCheckStandIn(standInShellCheck, standInArguments));
}

string target = Argument("target", "check");

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

// The extensions CSharpier 1.3.0 formats, from PrinterOptions.GetFormatter, which matches them
// without regard to case.
string[] csharpierExtensions =
[
    ".cs",
    ".csx",
    ".config",
    ".csproj",
    ".props",
    ".slnx",
    ".targets",
    ".axaml",
    ".xaml",
    ".xml",
];

// The one pattern the held .csharpierignore carries, so the format row leaves out what CSharpier
// would drop from its count. CSharpier matches the pattern in exact case, and so does the row.
const string csharpierIgnoredSuffix = ".g.cs";

// CSharpier handed a directory reads every .gitignore and nested .csharpierignore above each file,
// and a root ignore line of * checks nothing and exits 0. So the row names each file TreeFiles finds
// with an extension CSharpier formats, less those the held .csharpierignore leaves out, and names
// the held config and ignore file too, so CSharpier reads no other. The config path is absolute,
// because CSharpier anchors a config's overrides to its directory, and a relative path leaves them
// matching nothing where an editor's CSharpier applies them. --include-generated checks a file whose
// header calls it generated, which CSharpier otherwise counts and skips. Windows caps a command line
// at 32,767 characters, and dotnet starts CSharpier with the same arguments again, so the row names
// the files in batches of at most csharpierBatchCharacters. Each batch fails unless CSharpier reports
// checking exactly as many files as the batch named, and a batch names at least one.
Task("format")
    .Description("C# and XML formatting, through CSharpier, over every such file in the tree, each named to CSharpier")
    .Does(() =>
    {
        const int csharpierBatchCharacters = 16_000;
        FilePath dotnet = Dotnet();
        string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
        string[] files =
        [
            .. TreeFiles(buildOutput: false)
                .Where(file =>
                    csharpierExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    && !file.EndsWith(csharpierIgnoredSuffix, StringComparison.Ordinal)
                )
                .Order(StringComparer.Ordinal),
        ];
        RequireChecked("format", files);

        List<List<string>> batches = [];
        int characters = 0;
        foreach (string file in files)
        {
            if (batches.Count == 0 || characters + file.Length + 3 > csharpierBatchCharacters)
            {
                batches.Add([]);
                characters = 0;
            }

            batches[^1].Add(file);
            characters += file.Length + 3;
        }

        foreach (List<string> batch in batches)
        {
            ProcessArgumentBuilder arguments = new ProcessArgumentBuilder()
                .Append("csharpier")
                .Append("check")
                .Append("--config-path")
                .AppendQuoted(System.IO.Path.Combine(root, ".csharpierrc"))
                .Append("--ignore-path")
                .Append(".csharpierignore")
                .Append("--include-generated");
            foreach (string file in batch)
            {
                arguments.AppendQuoted(file);
            }

            int exit = StartProcess(
                dotnet,
                new ProcessSettings
                {
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                out IEnumerable<string> output,
                out IEnumerable<string> errors
            );
            string[] logged = [.. output.Concat(errors)];
            foreach (string line in logged)
            {
                Information("{0}", line);
            }

            if (exit != 0)
            {
                throw new CakeException($"csharpier check exited {exit}.");
            }

            System.Text.RegularExpressions.Match reported = System.Text.RegularExpressions.Regex.Match(
                string.Join("\n", logged),
                @"^Checked (\d+) files in ",
                System.Text.RegularExpressions.RegexOptions.Multiline
            );
            if (!reported.Success)
            {
                throw new CakeException(
                    "CSharpier exited 0 without saying how many files it checked, so the row cannot tell it checked any."
                );
            }

            int count = int.Parse(reported.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (count == 0 || count != batch.Count)
            {
                throw new CakeException(
                    $"CSharpier checked {count} files, and the row named {batch.Count} in that batch, from {Quoted(batch[0])} to {Quoted(batch[^1])}. "
                        + "CSharpier drops a named file an ignore file matches, and one whose extension it does not format, and still exits 0."
                );
            }
        }

        Information(
            "The row named {0} files to CSharpier across {1} {2}.",
            files.Length,
            batches.Count,
            batches.Count == 1 ? "run" : "runs"
        );
    });

// --ignore-path names .prettierignore alone, which replaces prettier's default pair, so .gitignore
// takes nothing out of the row. --no-editorconfig stops prettier mapping an .editorconfig's indent,
// line ending and width onto the options .prettierrc leaves unset, from any directory above a file.
// prettier --check names no file it checked and passes on none, so a --debug-check pass first lists
// them, with the same options, and the row refuses an empty list.
Task("prettier")
    .Description("Markdown, YAML and JSON formatting, over the files the row lists first")
    .Does(() =>
    {
        const string options =
            "--bun --no-install prettier --config .prettierrc --ignore-path .prettierignore --no-editorconfig";
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
            .. TreeFiles(buildOutput: false)
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
// compiles only in Debug, so a Release-only gate would never analyze or even parse it. The tests and
// the installer run Release, so they reuse this build.
Task("build")
    .Description("Every project in Release and Debug, analyzer warnings as errors, lock files honored")
    .Does(() =>
    {
        FilePath dotnet = Dotnet();
        foreach (string configuration in (string[])["Release", "Debug"])
        {
            DotNetBuild(
                "WingetNudge.slnx",
                new DotNetBuildSettings
                {
                    Configuration = configuration,
                    MSBuildSettings = RootNamed(noAutoResponse: true),
                    ToolPath = dotnet,
                }
            );
        }
    });

// dotnet test evaluates every test project, so it reads the Directory files as a build does. It reads
// no Directory.Build.rsp, and hands -noAutoResponse to the test application, which refuses it. A
// filter the test application reads reports every test it leaves out as skipped, and dotnet test
// still exits 0, so the row reads the test run summary and fails unless every test ran and
// succeeded. A summary it cannot read fails the row too, whatever the exit code says. dotnet test
// localizes the summary's labels, so the row sets DOTNET_CLI_UI_LANGUAGE=en over the shell's value.
Task("tests")
    .Description(
        "The Core suite, and the versions docs/install.md restates from Directory.Packages.props, every test run and succeeded"
    )
    .IsDependentOn("build")
    .Does(() =>
    {
        List<string> said = [];
        DotNetTest(
            "WingetNudge.slnx",
            new DotNetTestSettings
            {
                ToolPath = Dotnet(),
                PathType = DotNetTestPathType.Solution,
                Configuration = "Release",
                NoBuild = true,
                MSBuildSettings = RootNamed(noAutoResponse: false),
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                },
                SetupProcessSettings = process =>
                {
                    process.RedirectStandardOutput = true;
                    process.RedirectStandardError = true;
                },
                PostAction = process =>
                {
                    said.AddRange(process.GetStandardOutput().Concat(process.GetStandardError()));
                    foreach (string line in said)
                    {
                        Information("{0}", line);
                    }
                },
            }
        );

        Dictionary<string, int[]> counts = new(StringComparer.Ordinal);
        foreach (string name in (string[])["total", "failed", "succeeded", "skipped"])
        {
            counts[name] =
            [
                .. said.Select(line => System.Text.RegularExpressions.Regex.Match(line, $@"^\s*{name}:\s*(\d+)\s*$"))
                    .Where(match => match.Success)
                    .Select(match =>
                        int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    ),
            ];
        }

        int summaries = said.Count(line => line.StartsWith("Test run summary:", StringComparison.Ordinal));
        if (summaries != 1 || counts.Values.Any(found => found.Length != 1))
        {
            throw new CakeException(
                $"The row could not read one test run summary, with one total, failed, succeeded and skipped count, from dotnet test's output, which held {summaries} summary lines. "
                    + "Without it the row cannot tell how many tests ran, and it never takes the exit code alone."
            );
        }

        int total = counts["total"][0];
        int succeeded = counts["succeeded"][0];
        int skipped = counts["skipped"][0];
        if (total == 0 || succeeded != total || skipped != 0)
        {
            throw new CakeException(
                $"dotnet test ran {total} tests: {succeeded} succeeded, {counts["failed"][0]} failed and {skipped} skipped, and exited 0. "
                    + "The row takes a run where every test succeeds and none is skipped. "
                    + "A filter the test application reads, such as xUnit's explicit setting in a testconfig.json, reports every test it leaves out as skipped."
            );
        }
    });

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
                MSBuildSettings = RootNamed(noAutoResponse: true).WithProperty("SigningCertificateThumbprint", ""),
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

// A workflow whose one script carries a ShellCheck directive, which the stand-in refuses.
const string shellCheckDirectiveCanary = """
    name: canary
    on: push
    jobs:
      canary:
        runs-on: ubuntu-latest
        steps:
          - run: |
              # shellcheck disable=SC2086
              echo $GITHUB_REF
    """;

const string shellCheckDirectiveRefusal = "The gate refuses a ShellCheck directive in a workflow script";

// The shells a workflow may name: bash and sh, whose scripts actionlint hands ShellCheck, and pwsh,
// a Windows runner's default, which ShellCheck cannot read. actionlint matches a shell by its first
// word, so shell: /bin/bash runs bash with no ShellCheck, and every other value is refused.
string[] shellCheckedShells = ["bash", "sh", "pwsh"];

// ShellCheck 0.11.0 reads this variable as extra arguments on every run, past the --norc actionlint
// passes, so an -e there drops a finding. It is the one variable ShellCheck reads that changes one,
// and actionlint and its canaries run with it empty. The stand-in removes it again.
const string shellCheckOptions = "SHELLCHECK_OPTS";

// The one place whose reusable workflows a job may call with secrets: inherit, which hands the called
// workflow every secret its caller can read.
const string inheritCallee = "zachthedev/.github/.github/workflows/";

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
      # cd.yml's release-pr job and deps.yml's deps job each call a job that names an environment, and
      # run in none themselves, so secrets: inherit is the one form that passes that environment's
      # secret. The gate refuses an inline ignore comment, so both waivers sit here, one per file. A
      # waiver binds a file, never the workflow a job calls, so the workflows row also runs zizmor with
      # no config and fails unless every job passing secrets: inherit calls a zachthedev/.github workflow.
      secrets-inherit:
        ignore:
          - cd.yml
          - deps.yml

    """;

// .csharpierrc, byte for byte. The format row names it with --config-path, and the gate refuses every
// other name CSharpier searches for.
const string csharpierConfig = """
    {
      "printWidth": 120,
      "indentSize": 4,
      "useTabs": false,
      "endOfLine": "lf"
    }

    """;

// .csharpierignore, byte for byte. The format row names it with --ignore-path.
const string csharpierIgnore = "*" + csharpierIgnoredSuffix + "\n";

// lefthook.yml, byte for byte. lefthook runs each job's command as written, and an extends or remotes
// key there pulls in more config.
const string lefthookConfig = """
    # Git hooks. `bun install` runs `lefthook install`, which writes the hooks into .git/hooks.

    # cosmiconfig runs a module from .config at the root before commitlint reads --config, so the first
    # job refuses the directory, and piped stops the hook at the first job that fails.
    # --bun runs commitlint under the Bun that runs this hook, rather than whichever node PATH names.
    # --config names the one commitlint config, so commitlint searches for no other.
    commit-msg:
      piped: true
      jobs:
        - name: no .config
          run: test ! -e .config || { echo 'cosmiconfig runs modules from .config, so remove it' >&2; exit 1; }
        - name: commitlint
          run: bunx --bun --no-install commitlint --config commitlint.config.js --edit {1}

    # The gate, run before anything leaves this machine. `dotnet cake.cs --description` lists its steps.
    pre-push:
      jobs:
        - name: gate
          run: dotnet cake.cs

    """;

// The two .editorconfig files below the root, byte for byte. The analyzers read every .editorconfig
// above each source file, and a severity there can turn a finding off.
const string appEditorConfig = """
    [*.xaml.cs]
    # XAML wires event handlers to instance methods
    dotnet_diagnostic.CA1822.severity = none

    """;

const string testsEditorConfig = """
    [*.cs]
    # Test names use Method_Scenario_Expectation
    dotnet_diagnostic.CA1707.severity = none
    # Tests are the documentation
    dotnet_diagnostic.CS1591.severity = none

    """;

// The two mise data files, before anything installs from them. A lockfile that disagrees with its
// pin is the likeliest fault after a bump, and an address in it is what an install fetches, so both
// are read before the install rather than after. bunfig.toml, the config files the other rows read
// and the tree's other config files are read here as well, so a pull request that changes one fails
// the first row. The one process it starts is git, to list the tracked paths. Nothing here resolves
// mise, so the task needs none on the machine, and check runs it ahead of every other task.
Task("lockfile")
    .Description(
        "Every mise.toml pin recorded in mise.lock at the address cake.cs names, with no other mise config or lock file beside them, no refused tracked path, bunfig.toml and every config file a row reads as cake.cs holds them, and no other config file a tool the gate starts searches for"
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
        "actionlint with ShellCheck behind a stand-in that refuses its directives over .github/workflows, each shell: held to bash, sh or pwsh, then zizmor over .github, from the paths mise resolves in locked mode, and every job passing secrets: inherit held to a zachthedev/.github workflow"
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
        // file under .github/workflows itself, prints them, and refuses an empty list. The prefix
        // matches in any case, as NTFS names a checked-out folder by the first path git writes.
        string[] workflows =
        [
            .. TreeFiles(buildOutput: false)
                .Where(file =>
                    file.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                    && !file[".github/workflows/".Length..].Contains('/')
                    && (
                        file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Order(StringComparer.Ordinal),
        ];
        RequireChecked("actionlint", workflows);
        RequireShellCheckedShells(workflows);
        Command(
            ["actionlint", "actionlint.exe"],
            $"{ProvenAnalyzers(actionlint, Verified(resolved, "shellcheck"))} {string.Join(" ", workflows.Select(file => $"\"{file}\""))}",
            settingsCustomization: settings =>
                settings.WithToolPath(actionlint).WithEnvironmentVariable(shellCheckOptions, "")
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

        RequireInheritCallees(Verified(resolved, "zizmor"), workflows);
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
    RequireConfigFiles();
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

// dotnet for the format, build, tests and installer rows. The config checks run here as well as in
// the lockfile task, so no --target=build or --exclusive run reaches MSBuild or CSharpier past them.
FilePath Dotnet()
{
    RequireConfigFiles();
    return RequireOnPath("dotnet", "Install the .NET SDK global.json names.");
}

// The settings every build and test the gate runs hands MSBuild. RestoreLockedMode fails a restore
// whose package graph disagrees with a packages.lock.json rather than re-resolving it. The three
// Directory paths name the root files, so MSBuild searches above no project for them. MSBuild imports
// a named file only when it exists, and the root holds no Directory.Build.targets, so none is
// imported. The two ImportDirectorySolution switches stop a solution build importing a
// Directory.Solution.props or .targets from the root or any directory above it.
// DiscoverGlobalAnalyzerConfigFiles=false stops the compiler reading a file named .globalconfig from
// any directory above a source file. It leaves an .editorconfig that sets is_global, which is global
// under any name, and MSBuild still finds one in any directory above a source file, up to the drive
// root. RequireNoConfigElsewhere refuses one in the tree, and no check reaches one above the
// checkout. noAutoResponse passes -noAutoResponse, which stops MSBuild reading a Directory.Build.rsp.
// RestoreForce=true makes each build's restore write obj/<project file>.nuget.g.props and .targets
// again from what NuGet computes, where a restore with nothing to do leaves a changed one in place.
// dotnet test --no-build restores nothing, so it is inert on the tests row.
DotNetMSBuildSettings RootNamed(bool noAutoResponse)
{
    string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
    DotNetMSBuildSettings settings = new DotNetMSBuildSettings()
        .WithProperty("RestoreLockedMode", "true")
        .WithProperty("RestoreForce", "true")
        .WithProperty("DirectoryBuildPropsPath", System.IO.Path.Combine(root, "Directory.Build.props"))
        .WithProperty("DirectoryBuildTargetsPath", System.IO.Path.Combine(root, "Directory.Build.targets"))
        .WithProperty("DirectoryPackagesPropsPath", System.IO.Path.Combine(root, "Directory.Packages.props"))
        .WithProperty("ImportDirectorySolutionProps", "false")
        .WithProperty("ImportDirectorySolutionTargets", "false")
        .WithProperty("DiscoverGlobalAnalyzerConfigFiles", "false");
    settings.ExcludeAutoResponseFiles = noAutoResponse;
    return settings;
}

// bunx for the prettier row, the one bun process the gate starts. The row passes --bun, so prettier
// runs under this Bun rather than whichever node PATH names. The config checks run here as well as
// in the lockfile task, so no --target=prettier or --exclusive run reaches bunx past them.
FilePath Bunx()
{
    RequireConfigFiles();
    return RequireOnPath("bunx", "Install Bun at the version package.json names.");
}

// Every check on the tree's config files, run before each tool the gate starts: the tracked-path
// refusals, bunfig.toml, the held files, the tool manifest, and every other name a tool searches for.
void RequireConfigFiles()
{
    RequireNoRefusedTrackedPaths();
    RequireBunfig();
    RequireHeldFiles();
    RequireToolManifest();
    RequireNoConfigElsewhere();
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
// read. The match is on the mise and .mise prefixes every such name carries, so a name a later mise
// adds is refused as well. .tool-versions carries neither prefix, so it is refused by name.
// RequireNoConfigElsewhere refuses the .config directory whole. The walk is the file system, not
// git, because mise reads a file whether git tracks it or not. No other subdirectory is read by a
// mise run here, so none is walked, and the directories above the checkout are outside what a pull
// request can write. A name with a trailing dot, a trailing space or a stream suffix still starts
// with the prefix it carries, and mise opens the plain names alone, so no name is normalized first.
//
// A symbolic link or junction at the root, or under .mise or mise, is refused as well. mise and
// every check here follow one to wherever it points, so the file behind it is one this walk never
// names.
void RequireOnlyPinnedMiseFiles()
{
    static bool MiseNamed(string name) =>
        name.StartsWith("mise", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".mise", StringComparison.OrdinalIgnoreCase);

    string[] linkRoots = [".", ".mise", "mise"];
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
            $"The repository holds the link {string.Join(", ", linked.Select(Quoted))}, and the gate takes no symbolic link or junction at the root or under .mise or mise. "
                + "mise follows a link to wherever it points, so the file it reads is one the gate never named."
        );
    }

    string[] found =
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
            )
            .Order(StringComparer.Ordinal),
    ];
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
// node_modules/prettier still runs as prettier after the install. A tracked path with a bin or obj
// segment is refused the same way, since MSBuild imports files from obj by wildcard, and a
// committed one reaches every checkout. A tracked .env or .env.<name> at any depth is refused as
// well: Bun loads the one at the root into every process it starts, prettier and commitlint
// included, and no bunx flag stops it. So is a lefthook-local or .lefthook-local file at the root,
// which lefthook merges over lefthook.yml on every run. A path with a .git, .sl, .svn, .hg or .jj
// segment is refused, in any case, because prettier's CLI skips such a directory without a word. A
// zizmor: ignore[ comment in a tracked file under .github is refused, because zizmor honors it with
// no config. git answers what is tracked, so the node_modules an install writes, and a contributor's
// own untracked .env or lefthook-local file, pass. An extraction from git archive has no .git at the
// root and tracks nothing, so the check starts no git there and passes. No GIT_ variable reaches git,
// and git has to name the root as its top level, so the repository and index it reads are the
// checkout's own, never ones a shell or hook exported or a directory above the root holds.
void RequireNoRefusedTrackedPaths()
{
    string root = Context.Environment.WorkingDirectory.FullPath;
    string dotGit = System.IO.Path.Combine(root, ".git");
    if (!System.IO.Directory.Exists(dotGit) && !System.IO.File.Exists(dotGit))
    {
        Information("No .git at the root, so nothing is tracked and no tracked path is refused.");
        return;
    }

    FilePath git = RequireOnPath("git", "Install it with: winget install --id Git.Git --exact");
    (int listedExit, string listed) = Git("ls-files", "-z");
    if (listedExit != 0)
    {
        throw new CakeException(
            $"git ls-files exited {listedExit} beside a .git at the root, so the gate cannot tell which paths are tracked."
        );
    }

    // git reads a .git it cannot open as no repository at all, and searches the directories above for
    // one, so an empty .git leaves ls-files listing a parent repository's paths. --show-cdup prints
    // the path from the root up to the top level git found, an empty line when the root is the top
    // level, and it compares no paths, so a root reached through a junction passes.
    (int cdupExit, string cdup) = Git("rev-parse", "--show-cdup");
    if (cdupExit != 0 || cdup.Trim().Length != 0)
    {
        throw new CakeException(
            $"git rev-parse --show-cdup exited {cdupExit} printing {Quoted(cdup.Trim())} at the root {Quoted(root)}, and the gate takes an empty line. "
                + "git searched past the .git at the root, so the paths it lists are another repository's. "
                + "Restore the repository, or remove the .git and the check reads the tree as an extraction."
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

    string[] buildOutputSegments = ["bin", "obj"];
    string[] underBuildOutput =
    [
        .. tracked
            .Where(path =>
                path.Split('/').Any(segment => buildOutputSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            )
            .Order(StringComparer.Ordinal),
    ];
    if (underBuildOutput.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", underBuildOutput.Take(5).Select(Quoted))}{(underBuildOutput.Length > 5 ? $" and {underBuildOutput.Length - 5} more" : "")}, "
                + "and the gate takes no tracked path with a segment named bin or obj, in any case. "
                + "MSBuild imports obj/<project file>.*.props and .targets into every build of a project, so a committed one reaches the build in CI's checkout. Remove it from the commit."
        );
    }

    string[] envFiles =
    [
        .. tracked
            .Where(path =>
                path.Split('/')[^1] is string name
                && (
                    name.Equals(".env", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Order(StringComparer.Ordinal),
    ];
    if (envFiles.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", envFiles.Select(Quoted))}, and the gate takes no tracked .env file at any depth. "
                + "Bun loads one at the root into every process it starts, prettier and commitlint included, and no bunx flag stops it. "
                + "A tool started in any other directory loads the one there, and each holds values meant to stay out of git. Remove it from the commit."
        );
    }

    string[] lefthookLocal =
    [
        .. tracked
            .Where(path =>
                !path.Contains('/')
                && (
                    path.StartsWith("lefthook-local.", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(".lefthook-local.", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Order(StringComparer.Ordinal),
    ];
    if (lefthookLocal.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", lefthookLocal.Select(Quoted))} at the root, and the gate takes no tracked lefthook local file. "
                + "lefthook merges one over lefthook.yml on every run, so a job there replaces the hook's command. "
                + "Remove it from the commit, and keep your own copy untracked, as .gitignore does."
        );
    }

    string[] versionControlSegments = [".git", ".sl", ".svn", ".hg", ".jj"];
    string[] underVersionControl =
    [
        .. tracked
            .Where(path =>
                path.Split('/')
                    .Any(segment => versionControlSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            )
            .Order(StringComparer.Ordinal),
    ];
    if (underVersionControl.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks {string.Join(", ", underVersionControl.Take(5).Select(Quoted))}{(underVersionControl.Length > 5 ? $" and {underVersionControl.Length - 5} more" : "")}, "
                + $"and the gate takes no tracked path with a segment named {string.Join(", ", versionControlSegments)}, in any case. "
                + "prettier's CLI skips a directory of that name without a word, so no row would check what is under it. Rename the segment."
        );
    }

    System.Text.RegularExpressions.Regex waiver = new(
        @"zizmor\s*:\s*ignore\s*\[",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    );
    string[] waivers =
    [
        .. tracked
            .Where(path =>
                path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(System.IO.Path.Combine(root, path))
            )
            .SelectMany(path =>
                System
                    .IO.File.ReadAllLines(System.IO.Path.Combine(root, path))
                    .Select((line, index) => (path, line, number: index + 1))
            )
            .Where(entry => waiver.IsMatch(entry.line))
            .Select(entry => $"{Quoted(entry.path)} line {entry.number}")
            .Order(StringComparer.Ordinal),
    ];
    if (waivers.Length > 0)
    {
        throw new CakeException(
            $"The repository tracks a zizmor: ignore[ comment at {string.Join(", ", waivers)}, and the gate takes no inline zizmor waiver under .github. "
                + "zizmor honors one with no config, so nothing the gate holds names it. "
                + "Move the waiver to rules.<audit>.ignore in .github/zizmor.yml as the file name, and change zizmorConfig in cake.cs to match."
        );
    }

    // One git command at the root, with every GIT_ variable removed, and what it wrote to stdout.
    (int ExitCode, string Output) Git(params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo start = new(git.FullPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            WorkingDirectory = root,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

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
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
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

// The nine config files below, each held byte for byte against the text cake.cs holds for it.
// prettier runs the modules .prettierrc names, and a line in .prettierignore takes files out of the
// prettier row. .taplo.toml's exclude takes files out of the toml row, and a rule in
// .github/zizmor.yml can disable an audit or ignore a finding. An override in .csharpierrc and a line
// in .csharpierignore do the same to the format row. lefthook.yml holds the commands the hooks run,
// and a severity in either .editorconfig below the root can turn an analyzer finding off. So any
// change to one of these texts is refused rather than read, and the finding names the constant and
// the first line that differs. .github/actionlint.yaml, in either extension, is refused outright:
// its paths block ignores actionlint's errors by pattern, and the repository carries none.
void RequireHeldFiles()
{
    (string Path, string Constant, string Text, string Why)[] held =
    [
        (".prettierrc", nameof(prettierConfig), prettierConfig, "prettier runs the modules a config names"),
        (".prettierignore", nameof(prettierIgnore), prettierIgnore, "a line there takes files out of the prettier row"),
        (".taplo.toml", nameof(taploConfig), taploConfig, "its exclude takes files out of the toml row"),
        (
            ".github/zizmor.yml",
            nameof(zizmorConfig),
            zizmorConfig,
            "a rule there can disable an audit or ignore a finding"
        ),
        (
            ".csharpierrc",
            nameof(csharpierConfig),
            csharpierConfig,
            "an override there changes the format row's options"
        ),
        (
            ".csharpierignore",
            nameof(csharpierIgnore),
            csharpierIgnore,
            "a line there takes files out of the format row"
        ),
        (
            "lefthook.yml",
            nameof(lefthookConfig),
            lefthookConfig,
            "lefthook runs the commands there, and an extends or remotes key pulls in more config"
        ),
        (
            "src/WingetNudge/.editorconfig",
            nameof(appEditorConfig),
            appEditorConfig,
            "a severity there can turn an analyzer finding off"
        ),
        (
            "tests/.editorconfig",
            nameof(testsEditorConfig),
            testsEditorConfig,
            "a severity there can turn an analyzer finding off"
        ),
    ];
    foreach ((string path, string constant, string text, string why) in held)
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] actual = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : [];
        int offset = actual.AsSpan().CommonPrefixLength(expected);
        if (offset == actual.Length && offset == expected.Length)
        {
            continue;
        }

        string reason = $"{why[..1].ToUpperInvariant()}{why[1..]}, so the file has to match {constant} byte for byte.";
        if (!System.IO.File.Exists(path))
        {
            throw new CakeException(
                $"{path} is missing, and the gate takes the text {constant} in cake.cs holds for it. {reason} Restore the file."
            );
        }

        int line = expected.AsSpan(0, offset).Count((byte)'\n') + 1;
        throw new CakeException(
            $"{path} differs from {constant} in cake.cs at line {line}, byte {offset}: the file has {LineAt(actual, offset)}, and {constant} has {LineAt(expected, offset)}. "
                + $"{reason} Change both together, or restore the file."
        );
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

// Every other file a tool the gate starts, or an editor running that tool, would read as config,
// anywhere in the tree, each named with why. The gate hands each tool its one config by name, so
// the refusals keep an editor's run, and a run the gate does not make, reading what the gate reads.
// The walk is TreeFiles with the build output, because an editor reads an untracked file too, and
// MSBuild imports files from obj. Every name matches without regard to case, as NTFS does, and the
// one config the gate names for a tool passes in its exact case alone. An .editorconfig that sets
// is_global is refused wherever it sits, the root one and the two held ones included, since the
// analyzers apply a global config to every file of a project that finds it. A .config entry at the
// root is refused whole: mise, dotnet tool run, cosmiconfig and lefthook each read config from it,
// and cosmiconfig runs a module there on every commitlint start. No tool the gate starts reads a
// .config below the root, since each reads its working directory, the root, or the directories
// above it.
void RequireNoConfigElsewhere()
{
    string root = System.IO.Path.GetFullPath(Context.Environment.WorkingDirectory.FullPath);
    List<string> found = [];
    foreach (string relative in TreeFiles(buildOutput: true))
    {
        string name = System.IO.Path.GetFileName(relative);
        string? why =
            SearchedConfig(relative)
            ?? (
                name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                && SetsIsGlobal(System.IO.Path.Combine(root, relative))
                    ? "it sets is_global, and the analyzers apply a global config to every file of a project with a source file below it"
                    : null
            )
            ?? (
                name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    ? RefusedPackageJson(System.IO.Path.Combine(root, relative))
                    : null
            );
        if (why is not null)
        {
            found.Add($"{Quoted(relative)}, since {why}");
        }
    }

    found.AddRange(
        System
            .IO.Directory.EnumerateFileSystemEntries(".")
            .Select(entry => System.IO.Path.GetFileName(entry))
            .Where(name => name.Equals(".config", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
                $"{Quoted(name)}, since mise, dotnet tool run, cosmiconfig and lefthook each read config from it, and cosmiconfig runs a module there on every commitlint start"
            )
    );
    if (found.Count > 0)
    {
        throw new CakeException(
            $"The tree holds {string.Join("; ", found.Order(StringComparer.Ordinal))}. "
                + "The gate names each tool's one config itself and takes no other file the tool searches for. "
                + "Remove each one, and put any setting it carries in the file the gate names."
        );
    }
}

// Why the gate refuses a file of this name where it sits, or null when it takes it. The names are
// each tool's own search list at the version the gate pins: prettier 3.9.8's CONFIG_FILES, CSharpier
// 1.3.0's .csharpierrc family, MSBuild's Directory files, response file, project .user files and
// obj imports, NuGet's config, the analyzers' .editorconfig and .globalconfig, Bun's tsconfig.json
// and jsconfig.json, Cake's cake.config, taplo 0.10.0, zizmor 1.30.1, commitlint 21.2.2 over
// cosmiconfig 9.0.2, lefthook 2.1.14, and the test platform's testconfig.json and xUnit's
// xunit.runner.json. A name is refused at every depth the tool, or an editor running it, searches.
// package.yaml is refused whole, since the gate does not read its keys. In obj, MSBuild imports
// <project file>.*.props and .targets by wildcard, and NuGet writes the nuget.g pair there on every
// restore, so that pair alone passes.
static string? SearchedConfig(string relative)
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
    string[] commitlintFiles =
    [
        ".commitlintrc",
        ".commitlintrc.json",
        ".commitlintrc.yaml",
        ".commitlintrc.yml",
        ".commitlintrc.js",
        ".commitlintrc.cjs",
        ".commitlintrc.mjs",
        ".commitlintrc.ts",
        ".commitlintrc.cts",
        ".commitlintrc.mts",
        "commitlint.config.js",
        "commitlint.config.cjs",
        "commitlint.config.mjs",
        "commitlint.config.ts",
        "commitlint.config.cts",
        "commitlint.config.mts",
    ];
    string[] zizmorFiles =
    [
        ".github/zizmor.yaml",
        "zizmor.yml",
        "zizmor.yaml",
        ".github/.github/zizmor.yml",
        ".github/.github/zizmor.yaml",
    ];
    string[] lefthookFiles = ["lefthook.yaml", "lefthook.json", "lefthook.jsonc", "lefthook.toml"];
    string[] heldEditorConfigs = ["src/WingetNudge/.editorconfig", "tests/.editorconfig"];
    string[] segments = relative.Split('/');
    string name = segments[^1].ToLowerInvariant();
    bool atRoot = segments.Length == 1;
    System.Text.RegularExpressions.Match objImport = System.Text.RegularExpressions.Regex.Match(
        name,
        @"^.+\.(csproj|wixproj)\.(.*)\.(props|targets)$"
    );
    return relative switch
    {
        _ when prettierFiles.Contains(name) && relative != ".prettierrc" =>
            "prettier reads it as config and runs the modules it names",
        _ when name == "package.yaml" =>
            "prettier and cosmiconfig read its keys as config, and the gate does not read its keys",
        _ when name == ".npmrc" => "it moves where bun install downloads from",
        _ when name.StartsWith(".csharpierrc", StringComparison.Ordinal) && relative != ".csharpierrc" =>
            "CSharpier reads it as config for every file below it",
        _ when name == ".csharpierignore" && relative != ".csharpierignore" =>
            "CSharpier leaves out every file it matches below it",
        _ when (name is "directory.build.props" or "directory.build.targets" or "directory.packages.props")
                && !atRoot => "MSBuild imports it for every project below it in a build that names no root file",
        _ when name == "directory.build.rsp" => "MSBuild reads its switches on every command-line build",
        _ when name is "directory.solution.props" or "directory.solution.targets" =>
            "MSBuild imports it into a build of any solution in its directory or below, and an editor's build passes no switch that stops it",
        _ when name.EndsWith(".csproj.user", StringComparison.Ordinal)
                || name.EndsWith(".wixproj.user", StringComparison.Ordinal) =>
            "MSBuild imports it after the project body in every build of the project, so a property there switches what the gate's builds check, "
                + "and a debug profile belongs in Properties/launchSettings.json instead, which MSBuild does not import",
        _ when segments.Length > 1
                && segments[^2].Equals("obj", StringComparison.OrdinalIgnoreCase)
                && objImport.Success
                && objImport.Groups[2].Value != "nuget.g" =>
            "MSBuild imports it into every build of the project beside obj, and NuGet's own nuget.g files are the only ones the gate takes there",
        _ when name == "nuget.config" && !atRoot =>
            "NuGet adds its sources past the root nuget.config's <clear /> for every project below it",
        _ when name == ".editorconfig" && !atRoot && !heldEditorConfigs.Contains(relative) =>
            "the analyzers read its severities, and an editor's prettier its indent and line endings, for every file below it",
        _ when name == ".globalconfig" =>
            "the analyzers apply it to every file of a project with a source file below it, in an editor's build, which does not pass DiscoverGlobalAnalyzerConfigFiles=false as the gate's does",
        _ when name is "testconfig.json" or "xunit.runner.json"
                || name.EndsWith(".testconfig.json", StringComparison.Ordinal)
                || name.EndsWith(".xunit.runner.json", StringComparison.Ordinal) =>
            "the test application reads it as config, and the build copies a testconfig.json beside a test project into its output, where a filter can leave every test out",
        _ when name is "tsconfig.json" or "jsconfig.json" =>
            "Bun reads its paths and jsx settings for the modules prettier and commitlint load",
        _ when atRoot && name == "cake.config" => "Cake reads its settings before any task runs",
        _ when atRoot && name == "taplo.toml" =>
            "taplo reads it as config when run without --config, as an editor runs it",
        _ when zizmorFiles.Contains(relative.ToLowerInvariant()) =>
            "zizmor reads it as config when run without --config",
        _ when atRoot && commitlintFiles.Contains(name) && relative != "commitlint.config.js" =>
            "commitlint reads it as config when run without --config, as the shared commits job runs it",
        _ when atRoot && (lefthookFiles.Contains(name) || name.StartsWith(".lefthook.", StringComparison.Ordinal)) =>
            "lefthook reads it as its config",
        _ => null,
    };
}

// Why the gate refuses a package.json, or null when it takes it. prettier reads a top-level prettier
// key as config, commitlint a commitlint key, and cosmiconfig a cosmiconfig key as options for every
// search it makes. Bun's reader takes more than JSON does, so a file that does not read as a JSON
// object is refused. Bun keeps the first of two keys, and JSON.parse the last, so a key named twice
// at any depth is refused too. JsonDocument parses a key holding a lone surrogate escape, and throws
// InvalidOperationException only when the key is read, so that file is refused as not JSON as well.
static string? RefusedPackageJson(string path)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement top = document.RootElement;
        if (top.ValueKind != JsonValueKind.Object)
        {
            return "it is not a JSON object, and Bun's reader takes more than JSON does";
        }

        string[] duplicates = [.. DuplicateKeys(top, "$")];
        if (duplicates.Length > 0)
        {
            return $"it names {string.Join(", ", duplicates.Select(Quoted))} twice, and Bun keeps the first of two keys where JSON.parse keeps the last";
        }

        string[] keys =
        [
            .. top.EnumerateObject()
                .Select(property => property.Name)
                .Where(key => key is "prettier" or "commitlint" or "cosmiconfig")
                .Select(key =>
                    key switch
                    {
                        "prettier" => "a top-level \"prettier\" key, which prettier reads as config",
                        "commitlint" =>
                            "a top-level \"commitlint\" key, which commitlint reads as config when run without --config",
                        _ => "a top-level \"cosmiconfig\" key, which cosmiconfig reads as options for every search",
                    }
                ),
        ];
        return keys.Length > 0 ? $"it carries {string.Join(", and ", keys)}" : null;
    }
    catch (Exception error) when (error is JsonException or InvalidOperationException)
    {
        return "it does not read as JSON, and Bun's reader takes more than JSON does";
    }
}

// Whether an .editorconfig sets is_global, which makes it a global config under any name. The
// compiler reads a property line as optional blanks, a key, then = or :, and compares keys without
// regard to case. The match runs over every line, in or out of a section, so no layout hides one.
static bool SetsIsGlobal(string path) =>
    System.Text.RegularExpressions.Regex.IsMatch(
        System.IO.File.ReadAllText(path).ReplaceLineEndings("\n"),
        @"^\s*is_global\s*[=:]",
        System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

// Every key path in a JSON value, from $, whose key its object names a second time. Names compare
// as decoded text, so an escaped spelling of a key is the same key.
static IEnumerable<string> DuplicateKeys(JsonElement element, string path)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string child = $"{path}.{property.Name}";
            if (!seen.Add(property.Name))
            {
                yield return child;
            }

            foreach (string nested in DuplicateKeys(property.Value, child))
            {
                yield return nested;
            }
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            foreach (string nested in DuplicateKeys(item, $"{path}[{index}]"))
            {
                yield return nested;
            }

            index++;
        }
    }
}

// dotnet-tools.json, holding "isRoot": true. dotnet tool run takes no manifest path: it walks up from
// the working directory, reading .config/dotnet-tools.json and then dotnet-tools.json in each
// directory, until a manifest sets isRoot. Without it, a manifest above the checkout could name the
// csharpier the format row runs. RequireNoConfigElsewhere refuses the .config directory. A key named
// twice is refused, since the SDK and JsonDocument each read the last and another reader the first.
// A key holding a lone surrogate escape throws InvalidOperationException when read, and is refused
// as not JSON.
void RequireToolManifest()
{
    const string path = "dotnet-tools.json";
    if (!System.IO.File.Exists(path))
    {
        throw new CakeException($"{path} is missing, and it names the csharpier the format row runs. Restore it.");
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement top = document.RootElement;
        string[] duplicates = top.ValueKind == JsonValueKind.Object ? [.. DuplicateKeys(top, "$")] : [];
        if (duplicates.Length > 0)
        {
            throw new CakeException(
                $"{path} names {string.Join(", ", duplicates.Select(Quoted))} twice, and the gate takes each key once. "
                    + "The SDK reads the last of two keys, and another reader the first. Remove the duplicate."
            );
        }

        if (
            top.ValueKind != JsonValueKind.Object
            || !top.TryGetProperty("isRoot", out JsonElement isRoot)
            || isRoot.ValueKind != JsonValueKind.True
        )
        {
            throw new CakeException(
                $"{path} does not set \"isRoot\": true, and the gate takes a manifest that does. "
                    + "dotnet tool run walks up from the root until a manifest sets it, so a manifest above the checkout could name the csharpier the format row runs. Set it."
            );
        }
    }
    catch (Exception error) when (error is JsonException or InvalidOperationException)
    {
        throw new CakeException($"{path} does not read as JSON: {Quoted(error.Message)}. Restore it.");
    }
}

// Every file in the tree, relative to the root with forward slashes, for the refusals and rows that
// read the tree rather than handing a tool a directory. It skips .git wherever it sits, and
// node_modules, .claude/worktrees and .vs at the root, where installs, agents and IDEs write. bin
// and obj beside a .csproj or .wixproj hold what the SDK writes, so the rows skip them, and the
// refusals pass buildOutput to read them, since MSBuild imports files from obj. The SDK compiles a
// file in a bin or obj anywhere else, so every walk enters one. A directory link is refused, since
// the files behind it are ones no check here would name. A directory the gate cannot list, or one
// deleted while the walk reads it, is refused by name as well, so a row fails rather than checking
// a shorter list than the tree holds.
List<string> TreeFiles(bool buildOutput)
{
    string[] skippedAtRoot = ["node_modules", ".claude/worktrees", ".vs"];
    string[] projectOutput = ["bin", "obj"];
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

        bool projectRoot = entries.Any(entry =>
            entry is not System.IO.DirectoryInfo
            && (
                entry.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || entry.Name.EndsWith(".wixproj", StringComparison.OrdinalIgnoreCase)
            )
        );
        foreach (System.IO.FileSystemInfo entry in entries)
        {
            string relative = System.IO.Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (entry is not System.IO.DirectoryInfo)
            {
                files.Add(relative);
                continue;
            }

            if (
                entry.Name == ".git"
                || skippedAtRoot.Contains(relative, StringComparer.Ordinal)
                || (!buildOutput && projectRoot && projectOutput.Contains(entry.Name, StringComparer.Ordinal))
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

// The line of a text that holds a byte offset, quoted, or "nothing more" when the text ends before
// the offset.
static string LineAt(byte[] bytes, int offset)
{
    if (offset >= bytes.Length)
    {
        return "nothing more";
    }

    int start = offset == 0 ? 0 : Array.LastIndexOf(bytes, (byte)'\n', offset - 1) + 1;
    int end = Array.IndexOf(bytes, (byte)'\n', offset);
    return Quoted(System.Text.Encoding.UTF8.GetString(bytes, start, (end < 0 ? bytes.Length : end) - start));
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

// .github/zizmor.yml waives secrets-inherit by file, and a waiver binds a file, never the workflow a
// job calls. So a new job in cd.yml or deps.yml could hand every secret to another repository's
// workflow unseen. This runs zizmor again, offline, with no config and no ignores, and every
// secrets-inherit finding has to call a workflow under inheritCallee. zizmor 1.30.1's json-v1 output
// gives the callee as the concrete feature of the finding's primary location, the job's uses value.
// The findings have to number the secrets: inherit lines in the workflows, so a changed output
// shape, or a finding zizmor stops reporting, fails the row rather than passing it.
void RequireInheritCallees(FilePath zizmor, string[] workflows)
{
    int exit = StartProcess(
        zizmor,
        new ProcessSettings
        {
            Arguments =
                "--no-progress --offline --no-config --no-ignores --strict-collection --no-exit-codes --format json-v1 --collect=all .github",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Silent = true,
        },
        out IEnumerable<string> output,
        out IEnumerable<string> errors
    );
    if (exit != 0)
    {
        throw new CakeException(
            $"zizmor exited {exit} listing its findings with no config, saying {Quoted(string.Join(" ", errors))}."
        );
    }

    List<string> callees = [];
    try
    {
        using JsonDocument document = JsonDocument.Parse(string.Join("\n", output));
        foreach (JsonElement finding in document.RootElement.EnumerateArray())
        {
            if (finding.GetProperty("ident").GetString() != "secrets-inherit")
            {
                continue;
            }

            callees.Add(
                finding
                    .GetProperty("locations")
                    .EnumerateArray()
                    .Where(location => location.GetProperty("symbolic").GetProperty("kind").GetString() == "Primary")
                    .Select(location => location.GetProperty("concrete").GetProperty("feature").GetString())
                    .FirstOrDefault()
                    ?? throw new CakeException(
                        "zizmor reported a secrets-inherit finding with no primary location, so the gate cannot tell which workflow the job calls."
                    )
            );
        }
    }
    catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        throw new CakeException(
            $"zizmor's JSON did not read as json-v1 from zizmor 1.30.1, which the gate reads for secrets-inherit callees: {Quoted(error.Message)}."
        );
    }

    System.Text.RegularExpressions.Regex inherit = new(@"^\s*secrets\s*:\s*inherit\s*(#.*)?$");
    int inherits = workflows.Sum(file => System.IO.File.ReadAllLines(file).Count(line => inherit.IsMatch(line)));
    if (callees.Count != inherits)
    {
        throw new CakeException(
            $"zizmor reported {callees.Count} jobs passing secrets: inherit, and the workflows hold {inherits} secrets: inherit lines. "
                + "The gate holds each such job to its callee, so the two counts have to agree. Write each one as secrets: inherit on its own line."
        );
    }

    string[] outside =
    [
        .. callees.Where(callee => !callee.StartsWith(inheritCallee, StringComparison.Ordinal)).Select(Quoted),
    ];
    if (outside.Length > 0)
    {
        throw new CakeException(
            $"A job passing secrets: inherit calls {string.Join(", ", outside)}, and the gate takes a workflow under {inheritCallee} alone. "
                + "inherit hands the called workflow every secret the caller can read. Pass the secrets it needs by name, or call a workflow in zachthedev/.github."
        );
    }

    Information(
        "secrets: inherit reaches {0} called workflows, each under {1}: {2}",
        callees.Count,
        inheritCallee,
        string.Join(", ", callees.Order(StringComparer.Ordinal))
    );
}

// The arguments actionlint lints .github with, returned once actionlint has reported a ShellCheck
// finding and a stand-in refusal with them. -shellcheck names the stand-in in front of the file the
// version check resolved, so the pinned binary and the binary the stand-in starts are one path, not
// two lookups. -pyflakes= because no Windows package manager ships pyflakes, and actionlint skips
// that pass without a word when it is missing. A ShellCheck or stand-in actionlint cannot start
// leaves the shellcheck rule off and the exit code 0. The first canary proves ShellCheck runs behind
// the stand-in, and the second that the stand-in refuses a directive. Together they are what give a
// clean actionlint run any weight.
string ProvenAnalyzers(FilePath actionlint, FilePath shellcheck)
{
    string analyzers = $"-pyflakes= \"-shellcheck={ShellCheckStandIn(shellcheck)}\"";
    (string Text, string Expected, string Why)[] canaries =
    [
        (
            shellCheckCanary,
            shellCheckFinding,
            $"actionlint found no {shellCheckFinding} in a script that carries one, so ShellCheck never ran"
        ),
        (
            shellCheckDirectiveCanary,
            shellCheckDirectiveRefusal,
            "actionlint reported no refusal of a script carrying a ShellCheck directive, so the stand-in refuses none"
        ),
    ];
    foreach ((string text, string expected, string why) in canaries)
    {
        FilePath canary = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"actionlint-shellcheck-canary-{Guid.NewGuid():N}.yaml"
        );

        try
        {
            System.IO.File.WriteAllText(canary.FullPath, text);
            int exit = StartProcess(
                actionlint,
                new ProcessSettings
                {
                    Arguments = $"{analyzers} \"{canary.FullPath}\"",
                    RedirectStandardOutput = true,
                    Silent = true,
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [shellCheckOptions] = "",
                    },
                },
                out IEnumerable<string> reported
            );

            string said = string.Join('\n', reported).Trim();
            if (!said.Contains(expected, StringComparison.Ordinal))
            {
                throw new CakeException(
                    $"{why}. No run: block under .github/workflows was checked. "
                        + $"actionlint exited {exit} saying: {(said.Length == 0 ? "nothing" : said)}. "
                        + $"Check that {shellcheck.FullPath} starts."
                );
            }
        }
        finally
        {
            System.IO.File.Delete(canary.FullPath);
        }
    }

    return analyzers;
}

// Every shell: a workflow names, on a step or under defaults.run for the workflow or a job, held to
// shellCheckedShells. The stand-in reads only a script actionlint hands ShellCheck, so a shell no
// ShellCheck reads is refused here. Each workflow is read with YamlDotNet, so an escape or an alias
// resolves to the value GitHub reads, and a key matches in any case. A file YamlDotNet cannot read
// is refused, since the gate then cannot tell which shells it names.
void RequireShellCheckedShells(string[] workflows)
{
    List<string> found = [];
    foreach (string workflow in workflows)
    {
        YamlStream stream = new();
        try
        {
            stream.Load(new System.IO.StringReader(System.IO.File.ReadAllText(workflow)));
        }
        catch (YamlDotNet.Core.YamlException error)
        {
            throw new CakeException(
                $"{Quoted(workflow)} does not read as YAML at line {error.Start.Line}: {Quoted(error.Message)}. "
                    + "The gate reads each workflow for the shells it names. Fix the file."
            );
        }

        foreach (YamlDocument document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode top)
            {
                continue;
            }

            List<YamlNode> shells = [.. ShellsUnderDefaults(top)];
            foreach (
                YamlMappingNode job in Values(top, "jobs")
                    .OfType<YamlMappingNode>()
                    .SelectMany(jobs => jobs.Children.Values)
                    .OfType<YamlMappingNode>()
            )
            {
                shells.AddRange(ShellsUnderDefaults(job));
                shells.AddRange(
                    Values(job, "steps")
                        .OfType<YamlSequenceNode>()
                        .SelectMany(steps => steps.Children)
                        .OfType<YamlMappingNode>()
                        .SelectMany(step => Values(step, "shell"))
                );
            }

            found.AddRange(
                shells
                    .Where(shell =>
                        shell is not YamlScalarNode { Value: string name }
                        || !shellCheckedShells.Contains(name, StringComparer.Ordinal)
                    )
                    .Select(shell =>
                        $"{Quoted(workflow)} line {shell.Start.Line}, shell {(shell is YamlScalarNode { Value: string name } ? Quoted(name) : $"as a {shell.NodeType}")}"
                    )
            );
        }
    }

    if (found.Count > 0)
    {
        throw new CakeException(
            $"The workflows name {string.Join("; ", found)}, and the gate takes {string.Join(", ", shellCheckedShells)} alone. "
                + "actionlint hands ShellCheck a script by the shell's first word, so any other shell runs a script no check reads. Name one of those."
        );
    }

    static IEnumerable<YamlNode> Values(YamlMappingNode map, string key) =>
        map
            .Children.Where(child =>
                child.Key is YamlScalarNode { Value: string name }
                && name.Equals(key, StringComparison.OrdinalIgnoreCase)
            )
            .Select(child => child.Value);

    static IEnumerable<YamlNode> ShellsUnderDefaults(YamlMappingNode scope) =>
        Values(scope, "defaults")
            .OfType<YamlMappingNode>()
            .SelectMany(defaults => Values(defaults, "run"))
            .OfType<YamlMappingNode>()
            .SelectMany(run => Values(run, "shell"));
}

// The -shellcheck value that starts this program again as ShellCheck's stand-in, in front of the
// pinned ShellCheck. cake.cs runs under dotnet exec, so the command is dotnet, this assembly, the
// stand-in argument and the ShellCheck path. actionlint splits the value into words with
// go-shellwords, which drops an unquoted backslash and leaves the shellcheck rule off without a
// word. So each word goes single-quoted with forward slashes, and a path holding a quote is refused.
string ShellCheckStandIn(FilePath shellcheck)
{
    string host = Environment.ProcessPath ?? "";
    string assembly = Environment.GetCommandLineArgs()[0];
    if (
        !System.IO.Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || !System.IO.File.Exists(assembly)
    )
    {
        throw new CakeException(
            $"The gate runs as {Quoted(host)} with {Quoted(assembly)}, not as an assembly under dotnet exec, so it cannot start itself as ShellCheck's stand-in. "
                + "cake.cs sets UseAppHost=false so dotnet run starts it that way."
        );
    }

    string[] words =
    [
        .. ((string[])[host, assembly, shellCheckStandInArgument, shellcheck.FullPath]).Select(word =>
            word.Replace('\\', '/')
        ),
    ];
    string[] quoted = [.. words.Where(word => word.Contains('\'') || word.Contains('"'))];
    if (quoted.Length > 0)
    {
        throw new CakeException(
            $"{string.Join(", ", quoted.Select(Quoted))} holds a quote, and actionlint's -shellcheck value quotes each path in single quotes. Move it to a path without one."
        );
    }

    return string.Join(" ", words.Select(word => $"'{word}'"));
}

// The ShellCheck actionlint starts, through ShellCheckStandIn's value. actionlint writes the script
// ShellCheck reads to stdin, with every YAML escape decoded and every fold joined, so a directive no
// line of a workflow shows arrives here as a line. A line holding # then shellcheck and a blank, in
// any case, comes back as an error finding in ShellCheck's JSON form, and actionlint prints it and
// fails. ShellCheck honors every such directive, and no file the gate holds names one. Otherwise the
// pinned ShellCheck runs over the same bytes, with SHELLCHECK_OPTS removed, and its output and exit
// code pass through. An error here exits 2 with nothing on stdout, which actionlint fails on.
static int RunShellCheckStandIn(string shellCheck, string[] arguments)
{
    try
    {
        using System.IO.MemoryStream buffer = new();
        using (System.IO.Stream input = Console.OpenStandardInput())
        {
            input.CopyTo(buffer);
        }

        byte[] script = buffer.ToArray();
        System.Text.RegularExpressions.Regex directive = new(
            @"#\s*shellcheck\s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        );
        (string Line, int Number)[] refused =
        [
            .. new System.Text.UTF8Encoding(false)
                .GetString(script)
                .Split('\n')
                .Select((line, index) => (line, index + 1))
                .Where(entry => directive.IsMatch(entry.Item1)),
        ];
        if (refused.Length > 0)
        {
            using System.IO.Stream output = Console.OpenStandardOutput();
            using Utf8JsonWriter json = new(output);
            json.WriteStartArray();
            foreach ((string line, int number) in refused)
            {
                json.WriteStartObject();
                json.WriteString("file", "-");
                json.WriteNumber("line", number);
                json.WriteNumber("endLine", number);
                json.WriteNumber("column", 1);
                json.WriteNumber("endColumn", 1);
                json.WriteString("level", "error");
                json.WriteNumber("code", 0);
                json.WriteString(
                    "message",
                    $"{shellCheckDirectiveRefusal}, since ShellCheck honors it and no file the gate holds names it: {line.Trim()}"
                );
                json.WriteNull("fix");
                json.WriteEndObject();
            }

            json.WriteEndArray();
            return 1;
        }

        System.Diagnostics.ProcessStartInfo start = new(shellCheck)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(shellCheckOptions);
        using System.Diagnostics.Process process =
            System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException($"{shellCheck} did not start.");
        process.StandardInput.BaseStream.Write(script);
        process.StandardInput.Close();
        process.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"The ShellCheck stand-in failed: {error.Message}");
        return 2;
    }
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
