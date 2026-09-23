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

// ///// Code /////

Task("format")
    .Description("C# formatting, through CSharpier")
    .Does(() => DotNetTool("WingetNudge.slnx", "csharpier", "check ."));

Task("prettier")
    .Description("Markdown, YAML and JSON formatting")
    .Does(() => Command(["bunx", "bunx.exe"], "--no-install prettier --check ."));

// taplo comes from mise like the workflow linters, so it runs from the path mise which resolves
// once the lockfile task has passed its entry. --config names the committed file so TAPLO_CONFIG
// in the environment cannot swap it.
Task("toml")
    .Description("TOML formatting, through taplo, over the files .taplo.toml names")
    .IsDependentOn("lockfile")
    .Does(() =>
    {
        MiseConfig config = ReadMiseConfig("mise.toml");
        FilePath taplo = Installed(RequireMise(), "taplo", Pinned(config, "taplo"));
        Command(
            ["taplo", "taplo.exe"],
            "fmt --check --config .taplo.toml",
            settingsCustomization: settings => settings.WithToolPath(taplo)
        );
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
                new DotNetBuildSettings { Configuration = configuration, MSBuildSettings = locked }
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
        RequireFallbackRefused(config.Replacements);
        Dictionary<string, MiseArtifact> artifacts = MiseArtifacts("mise.lock", config.Platforms);
        foreach (string tool in config.Versions.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            RequireRecorded(tool, config.Versions[tool], config.Platforms, artifacts);
        }
    });

// The tool install, behind the lockfile task. Continuous integration's gate job runs this target
// ahead of the whole gate. The order is a dependency in this file, so this task installs nothing
// before the assertions pass. Outside check, because a local gate runs the tools an earlier install
// put on disk. The four settings in the environment are the ones the install leans on: locked
// mode, the lockfile read, re-verifying each attestation against the artifact mise.lock records
// rather than trusting the run that wrote it, and the url_api fallback refused. mise.toml sets all
// four, and this repeats them so the install does not depend on the file being read or on the
// environment leaving them alone.
Task("tools")
    .Description("The tools mise.lock records, installed once the lockfile task has passed them")
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
                    ["MISE_URL_REPLACEMENTS"] = JsonSerializer.Serialize(
                        new Dictionary<string, string>(StringComparer.Ordinal) { [fallbackPattern] = fallbackRefused }
                    ),
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
            resolved[tool] = Installed(mise, tool, config.Versions[tool]);
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

// mise.toml, read as TOML: the [tools] table is where the tool versions live rather than here,
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
            string version =
                pin.Value as string
                ?? throw new CakeException(
                    $"{path} pins {pin.Key} as something other than a version string, and the gate asserts a string pin alone."
                );

            // The pin goes into the url the lockfile task builds and the path segment Installed compares.
            // It is held to characters that add no separator, escape or space to either.
            if (version.Length == 0)
            {
                throw new CakeException(
                    $"{path} pins {pin.Key} as an empty string, and the gate takes ASCII letters, digits, '.', '+' and '-' alone."
                );
            }

            for (int index = 0; index < version.Length; index++)
            {
                if (!char.IsAsciiLetterOrDigit(version[index]) && version[index] is not ('.' or '+' or '-'))
                {
                    throw new CakeException(
                        $"{path} pins {pin.Key} as \"{version}\" with U+{(int)version[index]:X4} at index {index}, and the gate takes ASCII letters, digits, '.', '+' and '-' alone."
                    );
                }
            }

            versions[pin.Key] = version;
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

    Dictionary<string, string> replacements = new(StringComparer.Ordinal);
    if (
        table.TryGetValue("settings", out object? settingsValue)
        && settingsValue is TomlTable settingsTable
        && settingsTable.TryGetValue("url_replacements", out object? rules)
    )
    {
        if (rules is not TomlTable declaredRules)
        {
            throw new CakeException($"{path} sets url_replacements to something other than a table.");
        }

        foreach (KeyValuePair<string, object> rule in declaredRules)
        {
            replacements[rule.Key] =
                rule.Value as string
                ?? throw new CakeException(
                    $"{path} maps url_replacements key {rule.Key} to something other than a string."
                );
        }
    }

    return new MiseConfig(versions, platforms, replacements);
}

// mise.toml's url_replacements has to be the fallback refusal and nothing else. Without it a url
// that fails lets mise fetch whatever asset url_api names. Any other entry moves a download away
// from the address mise.lock records, past every assertion the lockfile task makes.
void RequireFallbackRefused(Dictionary<string, string> replacements)
{
    if (
        replacements.Count != 1
        || !replacements.TryGetValue(fallbackPattern, out string? destination)
        || destination != fallbackRefused
    )
    {
        throw new CakeException(
            $"mise.toml's [settings.url_replacements] has to hold one entry, '{fallbackPattern}' = \"{fallbackRefused}\", "
                + $"and it holds [{string.Join(", ", replacements.Select(rule => $"'{rule.Key}' = \"{rule.Value}\""))}]."
        );
    }
}

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
// alone, so an entry carrying the nested one is refused whole.
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
        if (entry.ContainsKey("platforms"))
        {
            throw new CakeException(
                $"{path} records a nested platforms table for {tool.Key}, and {relock} writes the quoted \"platforms.<name>\" form alone. Write it again with: {relock}"
            );
        }

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
            $"mise.toml declares {tool}, and cake.cs records no pin for it. Add its aqua repository, tag prefix and assets to misePins."
        );
    }

    // Every lockfile platform needs the asset this file expects there. An asset for a platform the
    // list does not name is an expectation nothing reads, so it is refused too.
    foreach (string platform in platforms)
    {
        if (!pin.Assets.ContainsKey(platform))
        {
            throw new CakeException(
                $"mise.toml's lockfile_platforms names {platform}, and cake.cs names no {platform} asset for {tool}. Add it to misePins."
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

        // The url is compared whole against the address this file builds, so no URL parser stands
        // between the check and the text mise reads.
        string asset = pin.Assets[platform].Replace("{version}", version, StringComparison.Ordinal);
        string url = $"https://{releaseHost}/{pin.Repository}/releases/download/{pin.TagPrefix}{version}/{asset}";
        RequireAddressText(tool, platform, "url", artifact.Url);
        if (!string.Equals(artifact.Url, url, StringComparison.Ordinal))
        {
            throw new CakeException(
                $"mise.lock records {tool} {platform} url as \"{artifact.Url}\", and cake.cs builds {url} from the pin. "
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
                $"mise.lock records {tool} {platform} url_api as \"{artifact.UrlApi}\", and the gate takes {assets} followed by an asset id alone. "
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
                $"aqua declares a signer workflow for {tool}, and mise.lock records {platform} provenance \"{artifact.Provenance}\". Write it again with: {relock}"
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
            $"mise.lock records {tool} {platform} {field} as \"{address}\", and an address mise writes carries no percent escape. Write it again with: {relock}"
        );
    }

    if (address.Contains('\\', StringComparison.Ordinal))
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as \"{address}\", and an address mise writes carries no backslash. Write it again with: {relock}"
        );
    }

    if (address.Split('/').Any(segment => segment is "." or ".."))
    {
        throw new CakeException(
            $"mise.lock records {tool} {platform} {field} as \"{address}\", and an address mise writes carries no . or .. segment. Write it again with: {relock}"
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

    string[] segments = resolved.Split(['/', '\\']);
    if (segments.Length < 3 || segments[^3] != tool || segments[^2] != version)
    {
        throw new CakeException(
            $"mise which {tool} resolved {resolved}, and mise.toml pins {tool} {version}, so the gate refuses to run it. "
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

/// <summary>What cake.cs knows about one pinned tool, beside the version mise.toml carries.</summary>
/// <param name="Repository">The GitHub repository aqua resolves the artifact from, as owner/name.</param>
/// <param name="TagPrefix">What the release tag carries ahead of the version.</param>
/// <param name="Assets">The release asset mise.lock fetches on each platform, with {version} for the pin.</param>
/// <param name="Attested">Whether aqua declares a signer workflow, so mise records provenance.</param>
sealed record MisePin(string Repository, string TagPrefix, Dictionary<string, string> Assets, bool Attested);

/// <summary>What mise.toml declares: the pinned version of each tool, and the platforms the lockfile carries.</summary>
/// <param name="Versions">Tool name to the version mise.toml pins.</param>
/// <param name="Platforms">The lockfile_platforms list every mise.lock entry has to carry.</param>
/// <param name="Replacements">The url_replacements entries under [settings], pattern to destination.</param>
sealed record MiseConfig(
    Dictionary<string, string> Versions,
    string[] Platforms,
    Dictionary<string, string> Replacements
);
