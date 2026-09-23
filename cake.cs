#:sdk Cake.Sdk
#:property NuGetLockFilePath=cake.packages.lock.json
#:property RestoreLockedMode=true
// With an apphost, the build writes an unsigned Cake.Sdk.exe beside the assembly under the temp
// directory, and dotnet run starts that. Without one, dotnet run starts the assembly through
// dotnet exec, so the gate's own code runs in the signed dotnet host.
#:property UseAppHost=false
#:package Tomlyn

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
    .Description("Every project in Release and Debug, analyzer warnings as errors, lock files honored")
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

// What the gate knows about each linter beside the version mise.toml pins. The aqua repository is
// here because mise.lock's backend and url are the address an install fetches from, and the file a
// bump rewrites wholesale is not where the expected owner can live.
//
// Attested names the two aqua declares a signer workflow for, so mise verifies a GitHub attestation
// and records it. koalaman/shellcheck declares neither a signer workflow nor a checksums file at
// any version constraint, so its entry carries a checksum and no provenance, and asserting one
// would fail a lockfile that is correct.
Dictionary<string, MisePin> misePins = new(StringComparer.Ordinal)
{
    ["actionlint"] = new("rhysd/actionlint", Attested: true),
    ["shellcheck"] = new("koalaman/shellcheck", Attested: false),
    ["zizmor"] = new("zizmorcore/zizmor", Attested: true),
};

// The host each address in mise.lock has to name, and the path under it. A url elsewhere is an
// install fetching bytes from elsewhere, whatever the rest of the entry says.
const string releaseHost = "github.com";
const string releaseApiHost = "api.github.com";

// The command that rewrites mise.lock from mise.toml. lockfile_platforms in mise.toml is what makes
// a bare lock write every platform the gate reads, so no flag repeats the list here.
const string relock = "mise lock";

// The two data files alone, before any process starts and before anything installs from them. A
// lockfile that disagrees with its pin is the likeliest fault after a bump, and an address in it
// is what an install fetches, so both are read before the install rather than after. Nothing here
// resolves mise, so the task needs none on the machine, and check runs it ahead of every other
// task.
Task("lockfile")
    .Description("Every mise.toml pin recorded in mise.lock at the address cake.cs names")
    .Does(() =>
    {
        MiseConfig config = ReadMiseConfig("mise.toml");
        Dictionary<string, MiseArtifact> artifacts = MiseArtifacts("mise.lock", config.Platforms);
        foreach (string tool in config.Versions.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            RequireRecorded(tool, config.Versions[tool], config.Platforms, artifacts);
        }
    });

// The tool install, behind the lockfile task. Continuous integration's gate job runs this target
// ahead of the whole gate. The order is a dependency in this file, so this task installs nothing
// before the assertions pass. Outside check, because a local gate runs the tools an earlier install
// put on disk. The three settings in the environment are the ones the install leans on: locked
// mode, the lockfile read, and re-verifying each attestation against the artifact mise.lock records
// rather than trusting the run that wrote it. mise.toml sets all three, and this repeats them so
// the install does not depend on the file being read or on the environment leaving them alone.
Task("tools")
    .Description("The linters mise.lock records, installed once the lockfile task has passed them")
    .IsDependentOn("lockfile")
    .Does(() =>
    {
        FilePath mise = RequireMise();
        int exit = StartProcess(
            mise,
            new ProcessSettings
            {
                Arguments = "install",
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MISE_LOCKED"] = "1",
                    ["MISE_LOCKFILE"] = "1",
                    ["MISE_LOCKED_VERIFY_PROVENANCE"] = "1",
                },
            }
        );
        if (exit != 0)
        {
            throw new CakeException($"mise install exited {exit}.");
        }
    });

Task("workflows")
    .Description(
        "actionlint with ShellCheck over .github/workflows, then zizmor over the repository, from the paths mise resolves in locked mode"
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
            resolved[tool] = Installed(mise, tool);
        }

        FilePath actionlint = Verified(resolved, "actionlint");

        Command(
            ["actionlint", "actionlint.exe"],
            ProvenAnalyzers(actionlint, Verified(resolved, "shellcheck")),
            settingsCustomization: settings => settings.WithToolPath(actionlint)
        );

        // --strict-collection fails on a file zizmor cannot parse. Without it the file is dropped
        // with a warning and the run reports no findings for a workflow it never read. --config
        // names the committed file so ZIZMOR_CONFIG in the environment cannot swap it. The online
        // audits read the GitHub API, so they run whenever gh holds a token and --offline keeps a
        // run without one green rather than failing on the missing token. The input is the
        // repository root, so zizmor collects .github/dependabot.yml beside the workflows. It
        // skips what .gitignore names, which keeps the workflows under node_modules out.
        string? token = GitHubToken();
        Command(
            ["zizmor", "zizmor.exe"],
            $"--no-progress {(token is null ? "--offline " : "")}--strict-collection --config .github/zizmor.yml .",
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

// mise.toml, read as TOML: the [tools] table is where the linter versions live rather than here,
// because a formatter moves source and a pin that moves is a pin no tool can read. Both legs
// install from this file, so no version has to be asserted against another file's copy of itself.
// lockfile_platforms is the list every mise.lock entry has to carry, so a bump made on one machine
// leaves the other leg an artifact to install from.
MiseConfig ReadMiseConfig(string path)
{
    TomlTable table = ReadToml(path);
    Dictionary<string, string> versions = new(StringComparer.Ordinal);
    if (table.TryGetValue("tools", out object? tools) && tools is TomlTable declared)
    {
        foreach (KeyValuePair<string, object> pin in declared)
        {
            // mise takes a table form too, which pins a version the gate would then never assert.
            versions[pin.Key] =
                pin.Value as string
                ?? throw new CakeException(
                    $"{path} pins {pin.Key} as something other than a version string, and the gate asserts a string pin alone."
                );
        }
    }

    if (versions.Count == 0)
    {
        throw new CakeException($"{path} declares no tool under [tools].");
    }

    string[] platforms =
        table.TryGetValue("settings", out object? settings)
        && settings is TomlTable declaredSettings
        && declaredSettings.TryGetValue("lockfile_platforms", out object? listed)
        && listed is TomlArray names
            ? [.. names.OfType<string>()]
            : [];
    if (platforms.Length == 0)
    {
        throw new CakeException(
            $"{path} sets no lockfile_platforms under [settings], so a bare {relock} would record this machine's platform alone."
        );
    }

    return new MiseConfig(versions, platforms);
}

// mise.lock as mise writes it, read as TOML: [[tools.<name>]] carries the version, the specifiers
// and the backend, and each platform sits under it in a quoted dotted key. mise picks the element
// whose specifiers name the requested version, so a tool with more than one element is refused
// outright: mise.toml pins one version per tool and mise lock writes one element per pin, and the
// gate would otherwise assert one element while mise installs from another. A field the entry lacks
// reads as empty, and every assertion below reads an empty field as a refusal, so an unfamiliar
// shape stops the gate rather than passing it. A platform table lockfile_platforms does not name is
// refused the same way: an install never reads it, so nothing verifies what it records.
Dictionary<string, MiseArtifact> MiseArtifacts(string path, string[] platforms)
{
    if (!System.IO.File.Exists(path))
    {
        throw new CakeException(
            $"{path} is missing, so nothing records which artifact a version resolved to. Write it with: mise trust; {relock}"
        );
    }

    TomlTable table = ReadToml(path);
    Dictionary<string, MiseArtifact> artifacts = new(StringComparer.Ordinal);
    if (!table.TryGetValue("tools", out object? tools) || tools is not TomlTable locked)
    {
        return artifacts;
    }

    foreach (KeyValuePair<string, object> tool in locked)
    {
        if (tool.Value is not TomlTableArray entries || entries.Count == 0)
        {
            continue;
        }

        if (entries.Count != 1)
        {
            throw new CakeException(
                $"{path} records {entries.Count} entries for {tool.Key}, and mise.toml pins one version, so {relock} writes one. Write it again with: {relock}"
            );
        }

        TomlTable entry = entries[0];
        string version = Text(entry, "version");
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
                    $"{path} records {tool.Key} for {name}, and mise.toml's lockfile_platforms names [{string.Join(", ", platforms)}]. Write it again with: {relock}"
                );
            }

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
        throw new CakeException($"{path}: {error.Message}");
    }
}

// One string field of a TOML table, or empty when the table lacks it or holds another type.
static string Text(TomlTable table, string key) =>
    table.TryGetValue(key, out object? value) && value is string text ? text : "";

// The mise that reads those two files. Continuous integration pins its version on the action line
// and verifies the download against the release's signed checksums; a local run takes whichever
// mise is on PATH, and mise itself refuses a lockfile entry it cannot verify.
FilePath RequireMise() =>
    Context.Tools.Resolve(["mise", "mise.exe"])
    ?? throw new CakeException("mise is not installed. Install it with: winget install --id jdx.mise --exact");

// What mise.lock has to say about one pinned tool before anything installs from it. Every branch
// here reads the two data files alone, so a bump that left the lockfile behind is reported by
// name on a machine with no mise at all.
void RequireRecorded(string tool, string version, string[] platforms, Dictionary<string, MiseArtifact> artifacts)
{
    if (!misePins.TryGetValue(tool, out MisePin? pin))
    {
        throw new CakeException(
            $"mise.toml declares {tool}, and cake.cs records no pin for it. Add its aqua repository to misePins."
        );
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
        // both have to be the one pin exactly. A prefix such as 1.7 would pass the url check below
        // and fail only at install time, on the leg that has mise.
        if (artifact.Version != version || !artifact.Specifiers.SequenceEqual([version], StringComparer.Ordinal))
        {
            throw new CakeException(
                $"mise.toml pins {tool} {version}, and mise.lock records version \"{artifact.Version}\" with specifiers [{string.Join(", ", artifact.Specifiers)}]. Write it again with: {relock}"
            );
        }

        // The backend decides which registry entry the artifact comes from, and the two addresses
        // are what the bytes arrive over. Every one of them is asserted against misePins in this
        // file, so a rewrite confined to mise.lock cannot move an install to another owner or another
        // version.
        if (artifact.Backend != backend)
        {
            throw new CakeException(
                $"cake.cs resolves {tool} through {backend}, and mise.lock records backend \"{artifact.Backend}\". Write it again with: {relock}"
            );
        }

        RequireArtifactAddress(
            tool,
            platform,
            "url",
            artifact.Url,
            releaseHost,
            $"/{pin.Repository}/releases/download/",
            version,
            relock
        );

        // mise writes both addresses into every entry, so one with a single address is not one mise
        // wrote, and the gate refuses it rather than passing an address it cannot assert.
        RequireArtifactAddress(
            tool,
            platform,
            "url_api",
            artifact.UrlApi,
            releaseApiHost,
            $"/repos/{pin.Repository}/releases/",
            "",
            relock
        );

        // ShellCheck's aqua entry declares no signer workflow and no checksums file, so mise has
        // no attestation to verify for it and its entry carries a checksum alone.
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
                $"aqua declares a signer workflow for {tool}, and mise.lock records {platform} provenance \"{artifact.Provenance}\". Write it again with: {relock}"
            );
        }
    }
}

// One address mise.lock hands an install, read as a URI rather than as text. A host is where the
// bytes come from, and a substring match over the text passes an address whose host is somewhere
// else entirely. https, the default port, that exact host, and the path the tool's own releases sit
// under; the pinned version has to appear in the path of the address the artifact downloads from,
// so an entry cannot point at another release of the same repository either. An entry with no
// address at all is refused by name, since mise writes one into every entry.
void RequireArtifactAddress(
    string tool,
    string platform,
    string field,
    string address,
    string host,
    string prefix,
    string version,
    string relock
)
{
    if (address.Length == 0)
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} with no {field}, and mise writes one into every entry it locks. Write it again with: {relock}"
        );
    }

    bool sound =
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
        && (version.Length == 0 || uri.AbsolutePath.Contains(version, StringComparison.Ordinal));

    if (!sound)
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as \"{address}\", and an install from this lockfile fetches that address. "
                + $"The gate takes https://{host}{prefix}"
                + (version.Length == 0 ? "" : $" carrying {version}")
                + $" alone. Write it again with: {relock}"
        );
    }
}

// Hands back the file mise resolved for the tool, so a caller runs that path rather than resolving
// the name a second time and trusting the two answers to match. mise which answers from the
// install the lockfile task passed, in locked mode, so the path is the pinned version's.
FilePath Installed(FilePath mise, string tool)
{
    int lookup = StartProcess(
        mise,
        new ProcessSettings
        {
            Arguments = $"which {tool}",
            RedirectStandardOutput = true,
            Silent = true,
        },
        out IEnumerable<string> located
    );
    string resolved = string.Join('\n', located).Trim();
    if (lookup != 0 || resolved.Length == 0)
    {
        throw new CakeException($"mise which {tool} found nothing. Install it with: mise install");
    }

    return new FilePath(resolved);
}

FilePath Verified(Dictionary<string, FilePath> resolved, string tool) =>
    resolved.TryGetValue(tool, out FilePath? executable)
        ? executable
        : throw new CakeException($"The workflows task runs {tool}, and mise.toml declares no such tool.");

// The token gh holds for api.github.com, or null when gh is absent or logged out. gh answers from
// GH_TOKEN first, so an exported token reaches zizmor through the same path a login does. The output
// is redirected and the process runs silent, so the token reaches the tool's environment and no log.
string? GitHubToken()
{
    FilePath? gh = Context.Tools.Resolve(["gh", "gh.exe"]);
    if (gh is null)
    {
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
    return exit == 0 && token.Length > 0 ? token : null;
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

/// <summary>What cake.cs knows about one pinned linter, beside the version mise.toml carries.</summary>
/// <param name="Repository">The GitHub repository aqua resolves the artifact from, as owner/name.</param>
/// <param name="Attested">Whether aqua declares a signer workflow, so mise records provenance.</param>
sealed record MisePin(string Repository, bool Attested);

/// <summary>What mise.toml declares: the pinned version of each tool, and the platforms the lockfile carries.</summary>
/// <param name="Versions">Tool name to the version mise.toml pins.</param>
/// <param name="Platforms">The lockfile_platforms list every mise.lock entry has to carry.</param>
sealed record MiseConfig(Dictionary<string, string> Versions, string[] Platforms);
