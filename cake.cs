#:sdk Cake.Sdk
#:property NuGetLockFilePath=cake.packages.lock.json
#:property RestoreLockedMode=true

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// The gate: every check a change must pass before it leaves the machine. CONTRIBUTING.md lists
// what each task covers. The pre-push hook runs the check target, and continuous integration runs
// the code target in one job and the same mise-installed workflow linters in another.

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
// builds unsigned on every machine. Signing belongs to a release, and this task checks it links.
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

// ///// Policy /////

// What each commit type does to a release. release-please opens no release pull request when the
// changelog body it rendered is one line, and the conventionalcommits preset drops a commit whose
// type is hidden or missing from this table. Hidden is therefore the release switch rather than a
// display preference: a visible type cuts a patch on its own, because the default versioning
// strategy bumps the patch for anything that is neither a breaking change nor a feat. Every type
// commitlint accepts is named, so no type is dropped by omission. A breaking change renders
// whatever this says and takes the major.
string[] releaseTriggers =
[
    "build releases",
    "chore is silent",
    "ci is silent",
    "docs releases",
    "feat releases",
    "fix releases",
    "perf releases",
    "refactor releases",
    "revert releases",
    "style is silent",
    "test is silent",
];

// The installer project and the command that regenerates its lock file. Renovate regenerates a
// packages.lock.json only beside a cs, vb or fs project, so a package this file declares restores
// in locked mode against a lock file nothing refreshes, and the next Directory.Packages.props bump
// fails NU1004 there. A WiX extension is the ordinary reason to declare one.
const string installerProject = "installer/WingetNudge.Installer.wixproj";
const string installerRelock = "dotnet restore installer/WingetNudge.Installer.wixproj --force-evaluate";

// The two property names that turn the NuGet advisory gate in Directory.Build.props into a
// warning, neither of which may come from the environment. MSBuild takes an environment variable
// as a property, so an exported AuditPipeline=false reaches every project this gate builds with
// nothing on a command line to see, and an exported WarningsNotAsErrors seeds the list a project
// file appends to. Directory.Build.props assigns that list outright rather than appending to what
// it inherits, so the second name changes nothing there today; it is asserted because an exported
// value must never be what an advisory gate reads. AuditPipeline=false is a documented escape for
// one invocation, and a command line is where it belongs.
string[] auditProperties = ["AuditPipeline", "WarningsNotAsErrors"];

// The repository config Renovate applies, and the line its validator prints once it has read the
// file as that rather than as global, self-hosted configuration.
const string renovateConfig = ".github/renovate.json";
const string renovateValidated = $"Validating {renovateConfig} as repo config";

Task("policy")
    .Description("Release types, renovate.json as repository config, and a Renovate note per wixproj package")
    .Does(() =>
    {
        RequireReleaseTriggers();
        RequireInstallerPackagesNoted();
        RequireRenovateConfigValid();
    });

Task("code")
    .Description("Everything continuous integration runs in its gate job")
    .IsDependentOn("format")
    .IsDependentOn("prettier")
    .IsDependentOn("policy")
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

// What the gate knows about each linter beside the version mise.toml pins. The version argument
// is here because a version mise reports is mise's own record rather than the binary's. The aqua
// repository is here because mise.lock's backend and url are the address an install fetches from,
// and the file a bump rewrites wholesale is not where the expected owner can live.
//
// Attested names the two aqua declares a signer workflow for, so mise verifies a GitHub attestation
// and records it. koalaman/shellcheck declares neither a signer workflow nor a checksums file at
// any version constraint, so its entry carries a checksum and no provenance, and asserting one
// would fail a lockfile that is correct.
Dictionary<string, MisePin> misePins = new(StringComparer.Ordinal)
{
    ["actionlint"] = new("-version", "rhysd/actionlint", Attested: true),
    ["shellcheck"] = new("--version", "koalaman/shellcheck", Attested: false),
    ["zizmor"] = new("--version", "zizmorcore/zizmor", Attested: true),
};

// The platforms mise.lock has to carry: a bump made on one machine has to leave the other leg an
// artifact to install from.
string[] lockedPlatforms = ["linux-x64", "windows-x64"];

// The host each address in mise.lock has to name, and the path under it. A url elsewhere is an
// install fetching bytes from elsewhere, whatever the rest of the entry says.
const string releaseHost = "github.com";
const string releaseApiHost = "api.github.com";

// Every mise setting the gate leans on. mise defaults locked, lockfile and locked_verify_provenance
// to off, so those three are what a missing mise.toml or MISE_SAFE=1 takes away, and any of the six
// can be switched off by its MISE_ environment variable.
//
// These six are not the whole of what can be switched off. MISE_LOCKED_SCOPES leaves every one of
// them reading true while dropping this repository's tools out of locked mode, so lockedScope is
// read separately below and mise.toml's [tool_config] locked is what holds when the list narrows.
string[] requiredSettings =
[
    "locked",
    "lockfile",
    "locked_verify_provenance",
    "github_attestations",
    "provenance_api_failures_fatal",
    "aqua.github_attestations",
];

// The config scope this repository's tools come from. locked_scopes lists the scopes
// invocation-wide locked mode covers, and a list without this one lifts it off every tool
// mise.toml declares.
const string lockedScope = "project";

Task("workflows")
    .Description("actionlint with ShellCheck, then zizmor, over .github, at the versions mise.lock records")
    .Does(() =>
    {
        // The two data files first, before any process starts: a lockfile that disagrees with
        // its pin is the likeliest fault after a bump, and reporting it needs no mise on the
        // machine.
        Dictionary<string, string> versions = MiseVersions("mise.toml");
        Dictionary<string, MiseArtifact> artifacts = MiseArtifacts("mise.lock");
        string[] tools = [.. versions.Keys.OrderBy(name => name, StringComparer.Ordinal)];
        foreach (string tool in tools)
        {
            RequireRecorded(tool, versions[tool], artifacts);
        }

        FilePath mise = RequireMise();
        RequireSettings(mise);

        Dictionary<string, FilePath> resolved = new(StringComparer.Ordinal);
        foreach (string tool in tools)
        {
            resolved[tool] = RequireInstalled(mise, tool, versions[tool]);
        }

        FilePath actionlint = Verified(resolved, "actionlint");

        Command(
            ["actionlint", "actionlint.exe"],
            ProvenAnalyzers(actionlint, Verified(resolved, "shellcheck")),
            settingsCustomization: settings => settings.WithToolPath(actionlint)
        );

        // --strict-collection fails on a file zizmor cannot parse. Without it the file is dropped
        // with a warning and the run reports no findings for a workflow it never read. --offline
        // keeps a local run's findings independent of a token; CI runs the online audits. --config
        // names the committed file so ZIZMOR_CONFIG in the environment cannot swap it.
        Command(
            ["zizmor", "zizmor.exe"],
            "--no-progress --offline --strict-collection --config .github/zizmor.yml " + ".github/workflows",
            settingsCustomization: settings => settings.WithToolPath(Verified(resolved, "zizmor"))
        );
    });

Task("check").Description("The whole gate").IsDependentOn("code").IsDependentOn("workflows");

// Before any target rather than inside one: installer is what the release workflow builds, and it
// depends on build alone, so a check living in a task would leave the one build an advisory escape
// costs the most.
Setup(context => RequireAuditPolicyUnset());

RunTarget(target);

// ///// Policy checks /////

void RequireAuditPolicyUnset()
{
    string[] exported =
    [
        .. auditProperties.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))),
    ];

    if (exported.Length > 0)
    {
        throw new CakeException(
            $"The environment sets [{string.Join(", ", exported)}], and MSBuild reads an environment variable as a property. "
                + "Directory.Build.props fails a build on NU1903 and NU1904, and either name turns that into a warning with nothing on the command line. "
                + $"Clear {string.Join(" and ", exported)} from the environment. AuditPipeline=false belongs on one dotnet invocation, beside the record CONTRIBUTING.md describes under Releases."
        );
    }
}

// changelog-sections in release-please-config.json, read as the release switch releaseTriggers
// describes.
void RequireReleaseTriggers()
{
    using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText("release-please-config.json"));

    string[] sections =
    [
        .. document
            .RootElement.GetProperty("changelog-sections")
            .EnumerateArray()
            .Select(section =>
            {
                bool silent =
                    section.TryGetProperty("hidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.True;
                string type = section.GetProperty("type").GetString() ?? "";
                return $"{type} {(silent ? "is silent" : "releases")}";
            })
            .OrderBy(entry => entry, StringComparer.Ordinal),
    ];

    if (!sections.SequenceEqual(releaseTriggers, StringComparer.Ordinal))
    {
        throw new CakeException(
            $"release-please-config.json says [{string.Join("; ", sections)}], and this repository releases on [{string.Join("; ", releaseTriggers)}]."
        );
    }
}

// Every PackageReference or GlobalPackageReference the wixproj declares has a rule in renovate.json
// that names it in matchDepNames and carries installerRelock in prBodyNotes, the way the Cake.Sdk
// rule does for cake.packages.lock.json. That note is what tells the person merging a
// Directory.Packages.props bump to regenerate the lock file Renovate left behind.
void RequireInstallerPackagesNoted()
{
    string[] packages =
    [
        .. XDocument
            .Load(installerProject)
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "GlobalPackageReference")
            .Select(element =>
                (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "(unnamed)"
            ),
    ];

    if (packages.Length == 0)
    {
        return;
    }

    // NuGet identifiers are case-insensitive, and so is Renovate's matchDepNames.
    HashSet<string> noted = new(StringComparer.OrdinalIgnoreCase);
    using JsonDocument renovate = JsonDocument.Parse(System.IO.File.ReadAllText(renovateConfig));
    if (renovate.RootElement.TryGetProperty("packageRules", out JsonElement rules))
    {
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            bool relocks =
                rule.TryGetProperty("prBodyNotes", out JsonElement notes)
                && notes
                    .EnumerateArray()
                    .Any(note => (note.GetString() ?? "").Contains(installerRelock, StringComparison.Ordinal));
            if (!relocks || !rule.TryGetProperty("matchDepNames", out JsonElement depNames))
            {
                continue;
            }

            foreach (JsonElement depName in depNames.EnumerateArray())
            {
                noted.Add(depName.GetString() ?? "");
            }
        }
    }

    string[] unnoted = [.. packages.Where(package => !noted.Contains(package))];
    if (unnoted.Length > 0)
    {
        throw new CakeException(
            $"{installerProject} declares [{string.Join(", ", unnoted)}], and {renovateConfig} carries no note for it. "
                + "Renovate regenerates a packages.lock.json only beside a csproj, so a package declared here goes stale on every Directory.Packages.props bump and the locked restore fails NU1004. "
                + $"Add a packageRules entry with matchManagers [\"nuget\"] and matchDepNames naming it, whose prBodyNotes says to run {installerRelock} and commit the result, the way the Cake.Sdk rule does."
        );
    }
}

// renovate-config-validator, on the file the repository config lives in. The validator treats a
// file named on its command line as global, self-hosted configuration unless --no-global says
// otherwise, and a global-only option such as autodiscover passes as global config and fails as
// repository config. The run has to print renovateValidated, which names the mode it used: the
// assertion below reads that line, and LOG_LEVEL is pinned so the environment cannot silence it.
// --strict fails a file that needs migration, so a renamed option is reported rather than
// translated. RENOVATE_X_IGNORE_RE2: package.json trusts no install script but lefthook's, so
// re2's native module never builds here, and without the variable Renovate warns with a stack
// trace on every run about the RegExp fallback it takes anyway.
void RequireRenovateConfigValid()
{
    FilePath bunx =
        Context.Tools.Resolve(["bunx", "bunx.exe"])
        ?? throw new CakeException(
            "bunx is not on PATH. Install Bun at the version package.json names in packageManager."
        );

    int exit = StartProcess(
        bunx,
        new ProcessSettings
        {
            Arguments = $"--no-install renovate-config-validator --strict --no-global {renovateConfig}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LOG_LEVEL"] = "info",
                ["RENOVATE_X_IGNORE_RE2"] = "true",
            },
        },
        out IEnumerable<string> output,
        out IEnumerable<string> errors
    );
    string said = string.Join('\n', output.Concat(errors)).Trim();
    string reported = said.Length == 0 ? "nothing" : said;

    if (!said.Contains(renovateValidated, StringComparison.Ordinal))
    {
        throw new CakeException(
            $"renovate-config-validator never said \"{renovateValidated}\", so {renovateConfig} was not validated as repository config. "
                + $"It exited {exit} saying:\n{reported}"
        );
    }

    if (exit != 0)
    {
        throw new CakeException($"renovate-config-validator exited {exit} over {renovateConfig} saying:\n{reported}");
    }
}

// ///// Pins /////

// mise.toml's [tools] table, which is where the linter versions live rather than here: a formatter
// moves source, and a pin that moves is a pin no tool can read. Both legs install from this file,
// so no version has to be asserted against another file's copy of itself.
Dictionary<string, string> MiseVersions(string path)
{
    Dictionary<string, string> versions = new(StringComparer.Ordinal);
    bool tools = false;
    foreach (string line in System.IO.File.ReadAllLines(path))
    {
        string text = line.Trim();
        if (text.StartsWith('['))
        {
            tools = text == "[tools]";
            continue;
        }

        Match declaration = Regex.Match(text, """^([A-Za-z0-9_.-]+)\s*=\s*"([^"]+)"$""");
        if (tools && declaration.Success)
        {
            versions[declaration.Groups[1].Value] = declaration.Groups[2].Value;
        }
    }

    if (versions.Count == 0)
    {
        throw new CakeException($"{path} declares no tool under [tools].");
    }

    return versions;
}

// mise.lock as mise writes it: [[tools.<name>]] carries the version, and each platform sits under
// it in a quoted dotted key. The reader keeps the fields the gate asserts. A line it cannot take
// leaves the field empty, and every assertion below reads an empty field as a refusal, so an
// unfamiliar shape stops the gate rather than passing it.
Dictionary<string, MiseArtifact> MiseArtifacts(string path)
{
    if (!System.IO.File.Exists(path))
    {
        throw new CakeException(
            $"{path} is missing, so nothing records which artifact a version resolved to. "
                + "Write it with: mise trust; mise lock --platform linux-x64,windows-x64"
        );
    }

    Dictionary<string, string> versions = new(StringComparer.Ordinal);
    Dictionary<string, string> backends = new(StringComparer.Ordinal);
    Dictionary<string, Dictionary<string, string>> fields = new(StringComparer.Ordinal);
    string tool = "";
    string platform = "";

    foreach (string line in System.IO.File.ReadAllLines(path))
    {
        string text = line.Trim();
        Match entry = Regex.Match(text, """^\[\[tools\.([A-Za-z0-9_.-]+)\]\]$""");
        if (entry.Success)
        {
            tool = entry.Groups[1].Value;
            platform = "";
            continue;
        }

        Match locked = Regex.Match(text, """^\[tools\.([A-Za-z0-9_.-]+)\."platforms\.([A-Za-z0-9_.-]+)"\]$""");
        if (locked.Success)
        {
            tool = locked.Groups[1].Value;
            platform = locked.Groups[2].Value;
            fields[$"{tool} {platform}"] = new Dictionary<string, string>(StringComparer.Ordinal);
            continue;
        }

        if (text.StartsWith('['))
        {
            tool = "";
            platform = "";
            continue;
        }

        Match assignment = Regex.Match(text, """^([A-Za-z0-9_]+)\s*=\s*"?([^"]*)"?$""");
        if (tool.Length == 0 || !assignment.Success)
        {
            continue;
        }

        if (platform.Length == 0)
        {
            switch (assignment.Groups[1].Value)
            {
                case "version":
                    versions[tool] = assignment.Groups[2].Value;
                    break;
                case "backend":
                    backends[tool] = assignment.Groups[2].Value;
                    break;
            }

            continue;
        }

        fields[$"{tool} {platform}"][assignment.Groups[1].Value] = assignment.Groups[2].Value;
    }

    Dictionary<string, MiseArtifact> artifacts = new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, Dictionary<string, string>> entry in fields)
    {
        string name = entry.Key[..entry.Key.IndexOf(' ')];
        artifacts[entry.Key] = new MiseArtifact(
            versions.TryGetValue(name, out string? version) ? version : "",
            backends.TryGetValue(name, out string? backend) ? backend : "",
            entry.Value.TryGetValue("checksum", out string? checksum) ? checksum : "",
            entry.Value.TryGetValue("provenance", out string? provenance) ? provenance : "",
            entry.Value.TryGetValue("url", out string? url) ? url : "",
            entry.Value.TryGetValue("url_api", out string? urlApi) ? urlApi : ""
        );
    }

    return artifacts;
}

// The mise that reads those two files. .github/mise-bootstrap.json carries the version, and the
// hashes continuous integration checks its download against, so the tool that verifies the linters
// is pinned the way the linters are.
FilePath RequireMise()
{
    using JsonDocument bootstrap = JsonDocument.Parse(System.IO.File.ReadAllText(".github/mise-bootstrap.json"));
    string version =
        bootstrap.RootElement.GetProperty("version").GetString()
        ?? throw new CakeException(".github/mise-bootstrap.json names no mise version.");
    string install = $"winget install --id jdx.mise --version {version} --exact";

    FilePath? executable = Context.Tools.Resolve(["mise", "mise.exe"]);
    if (executable is null)
    {
        throw new CakeException($"mise is not installed. Install it with: {install}");
    }

    int exit = StartProcess(
        executable,
        new ProcessSettings { Arguments = "--version", RedirectStandardOutput = true },
        out IEnumerable<string> output
    );
    string found = Regex.Match(string.Join('\n', output), @"\d+\.\d+\.\d+").Value;
    if (exit != 0 || found != version)
    {
        throw new CakeException(
            $"mise {found} is installed, and .github/mise-bootstrap.json pins {version}. Install it with: {install}"
        );
    }

    // The bytes on disk, against the entry for this platform. The version above is what mise says
    // about itself, which is the binary's own claim. Continuous integration checks the same entry
    // after its own download, so both legs identify mise the same way.
    string platform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    if (!bootstrap.RootElement.GetProperty("sha256").TryGetProperty(platform, out JsonElement pinned))
    {
        throw new CakeException($".github/mise-bootstrap.json records no sha256 for {platform}.");
    }

    string want = pinned.GetString() ?? "";
    string hash = Convert
        .ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(executable.FullPath)))
        .ToLowerInvariant();
    if (!hash.Equals(want, StringComparison.OrdinalIgnoreCase))
    {
        throw new CakeException(
            $"{executable.FullPath} hashes {hash}, and .github/mise-bootstrap.json records {want} for {platform}. Install it with: {install}"
        );
    }

    return executable;
}

// What mise has in force, not what mise.toml says. mise applies a project [settings] table whether
// or not the file is trusted, so trust is not what this guards. An environment variable such as
// MISE_LOCKED=0 outranks the file, and MISE_SAFE=1 drops project settings altogether. mise settings
// get answers with the effective value. mise settings ls --all lays each config file's own values
// over the effective table, so it reports the file wherever the two differ, and a gate reading it
// would pass a setting an override had switched off.
void RequireSettings(FilePath mise)
{
    foreach (string setting in requiredSettings)
    {
        string reported = MiseSetting(mise, setting);
        if (reported != "true")
        {
            string variable = "MISE_" + setting.Replace('.', '_').ToUpperInvariant();
            throw new CakeException(
                $"mise reports {setting} as {reported}, and the gate needs it true. "
                    + $"mise.toml sets it, so clear {variable} or MISE_SAFE from the environment."
            );
        }
    }

    // Every setting above reads true with this list narrowed, so the loop is blind to it on its
    // own. locked_scopes is global-only, so mise.toml cannot set it and MISE_LOCKED_SCOPES outranks
    // whatever the user config says. mise.toml's [tool_config] locked is enforced whatever this
    // reports, and this reports the override rather than leaving it silent.
    string scopes = MiseSetting(mise, "locked_scopes");
    if (!scopes.Contains($"\"{lockedScope}\"", StringComparison.Ordinal))
    {
        throw new CakeException(
            $"mise reports locked_scopes as {scopes}, and the gate needs it to cover \"{lockedScope}\", the scope mise.toml's tools come from. "
                + "Clear MISE_LOCKED_SCOPES from the environment, or widen the list in the user config."
        );
    }
}

// One effective setting, as mise reports it rather than as mise.toml writes it.
string MiseSetting(FilePath mise, string setting)
{
    int exit = StartProcess(
        mise,
        new ProcessSettings
        {
            Arguments = $"settings get {setting}",
            RedirectStandardOutput = true,
            Silent = true,
        },
        out IEnumerable<string> output
    );
    string reported = string.Join('\n', output).Trim();
    if (exit != 0)
    {
        throw new CakeException(
            $"mise settings get {setting} exited {exit} saying: " + (reported.Length == 0 ? "nothing" : reported)
        );
    }

    return reported;
}

// What mise.lock has to say about one pinned tool before anything installs from it. Every branch
// here reads the two data files alone, so a bump that left the lockfile behind is reported by
// name on a machine with no mise at all.
void RequireRecorded(string tool, string version, Dictionary<string, MiseArtifact> artifacts)
{
    if (!misePins.TryGetValue(tool, out MisePin? pin))
    {
        throw new CakeException(
            $"mise.toml declares {tool}, and cake.cs records no pin for it. Add its version argument and its aqua repository to misePins."
        );
    }

    const string relock = "mise lock --platform linux-x64,windows-x64";
    string backend = $"aqua:{pin.Repository}";
    foreach (string platform in lockedPlatforms)
    {
        if (!artifacts.TryGetValue($"{tool} {platform}", out MiseArtifact? artifact))
        {
            throw new CakeException(
                $"mise.toml pins {tool} {version}, and mise.lock records no {platform} artifact for it. Write one with: {relock}"
            );
        }

        if (artifact.Version != version)
        {
            throw new CakeException(
                $"mise.toml pins {tool} {version}, and mise.lock records {artifact.Version} for {platform}. Write it again with: {relock}"
            );
        }

        if (artifact.Checksum.Length == 0)
        {
            throw new CakeException(
                $"mise.lock records {tool} {version} for {platform} with no checksum, so an install from it verifies nothing. Write it again with: {relock}"
            );
        }

        // The backend decides which registry entry the artifact comes from, and the two addresses
        // are what the bytes arrive over. Every one of them is asserted against misePins in this
        // file, so a rewrite confined to mise.lock cannot move an install to another owner.
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

        // An entry mise wrote carries both addresses. One it does not is not a reason to leave an
        // unasserted address in the file.
        if (artifact.UrlApi.Length > 0)
        {
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
        }

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
// so an entry cannot point at another release of the same repository either.
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

// Hands back the executable it verified, so a caller runs that file rather than resolving the
// name a second time and trusting the two answers to match. The binary is asked its version
// rather than mise, because mise's answer is mise's own record and not the file on disk.
FilePath RequireInstalled(FilePath mise, string tool, string version)
{
    string versionArgument = misePins[tool].VersionArgument;
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

    FilePath executable = new FilePath(resolved);
    int exit = StartProcess(
        executable,
        new ProcessSettings { Arguments = versionArgument, RedirectStandardOutput = true },
        out IEnumerable<string> printed
    );
    string found = Regex.Match(string.Join('\n', printed), @"\d+\.\d+\.\d+").Value;
    if (exit != 0 || found != version)
    {
        throw new CakeException(
            $"{resolved} reports {tool} {found}, and mise.toml pins {version}. Install it with: mise install"
        );
    }

    return executable;
}

FilePath Verified(Dictionary<string, FilePath> resolved, string tool) =>
    resolved.TryGetValue(tool, out FilePath? executable)
        ? executable
        : throw new CakeException($"The workflows task runs {tool}, and mise.toml declares no such tool.");

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
    string Backend,
    string Checksum,
    string Provenance,
    string Url,
    string UrlApi
);

/// <summary>What cake.cs knows about one pinned linter, beside the version mise.toml carries.</summary>
/// <param name="VersionArgument">The argument the binary answers its own version on.</param>
/// <param name="Repository">The GitHub repository aqua resolves the artifact from, as owner/name.</param>
/// <param name="Attested">Whether aqua declares a signer workflow, so mise records provenance.</param>
sealed record MisePin(string VersionArgument, string Repository, bool Attested);
