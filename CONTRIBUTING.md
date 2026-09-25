# Contributing

This file takes a fresh clone to a running app and a green gate, and holds the rules that apply to
every change. Read it top to bottom the first time.

## Setup

Windows 11 on x64, with the winget release [docs/install.md](docs/install.md#requirements) names.
The app is WinUI 3 and the installer is an MSI, so neither builds anywhere else. The toolchain
installs with winget; open a new terminal afterwards, so `PATH` includes what it added.

```powershell
winget install --id Oven-sh.Bun --exact
winget install --id Microsoft.PowerShell --exact
winget install --id jdx.mise --exact
```

- The .NET SDK, at the version `global.json` names. [First run](#first-run) installs it, because
  that version comes from the clone. `global.json` also pins Cake.Sdk, which runs the gate.
- [Bun](https://bun.sh), at the version `package.json` names in `packageManager`. It runs
  commitlint, prettier and lefthook.
- PowerShell 7, for the scripts under `tools` and the commands in this document.
- [mise](https://mise.jdx.dev), at any current version. mise installs actionlint, zizmor,
  ShellCheck and taplo: `mise.toml` pins the version of each one, `mise.lock` records the artifact
  that version resolved to, and continuous integration installs from the same two files. The gate
  names the `winget install` command when mise is missing.

The app needs the Windows App Runtime at run time.
[docs/install.md](docs/install.md#requirements) names the version and where it comes from.

### First run

```powershell
git clone https://github.com/zachthedev/winget-nudge.git
Set-Location winget-nudge
$version = (Get-Content -Path global.json -Raw | ConvertFrom-Json).sdk.version
winget install --id Microsoft.DotNet.SDK.10 --version $version --exact
```

The SDK install reads the exact version `global.json` pins. Open a new terminal in the clone
afterwards, so `PATH` includes the SDK. [Troubleshooting](#troubleshooting) says what `dotnet`
prints without it. Then run the rest, before the first commit:

```powershell
bun install
dotnet tool restore
mise trust
mise install
dotnet cake.cs
```

`bun install` runs `lefthook install`, which writes the git hooks. `dotnet tool restore` installs
CSharpier at the version `dotnet-tools.json` pins. `mise trust` marks this repository's `mise.toml`
as one mise may read, and `mise install` puts the linters and taplo on disk from the artifacts
`mise.lock` records. `dotnet cake.cs` runs the whole gate, which a pre-push hook runs again before
anything leaves the machine.

The gate resolves each mise tool with `mise which` and runs the path it gets back. `mise install` is
the step that needs the network; the gate itself reaches it only for zizmor's online audits, when
`gh auth token` answers.

The first build downloads the `Microsoft.WinGet.Client` package from the PowerShell Gallery and
checks its hash. It is the only source of `winrtact.dll`, the winget hook that lets an unpackaged
process marshal winget's COM objects.

[Safety](#safety) says what to read before running any of this on a branch you did not write, which
variables to keep unset in the shell you run it from, and why the hooks are no control.

### Worktrees

A git worktree has no `node_modules` of its own, and `.worktreeinclude` copies none in. So in a
worktree under the main checkout, the prettier row fails and names the install to run, and the
commit-msg hook runs the main checkout's copy. A new clone takes the plain `bun install` above,
which writes the hooks. Run `bun install --frozen-lockfile --ignore-scripts` in each new worktree,
and again after `bun.lock` changes. [Safety](#safety) says what to read before that install. Keep
the scripts off there: `prepare` and lefthook's postinstall each run `lefthook install`, which
rewrites the shared hooks to name that worktree's lefthook.

## Safety

Read a pull request's diff before running anything on its branch, a commit included. The gate is
the branch's own `cake.cs`, and the hooks run the branch's `lefthook.yml` and `commitlint.config.js`,
so no check runs ahead of the branch's code.

No row stops the first Bun process on a branch nobody has read. lefthook's commit-msg hook runs
`bunx --bun commitlint` before any gate row, and `bun install` runs lefthook's postinstall, which
starts under Bun when node is not on `PATH`. Read a pull request's `bunfig.toml`, and any root `.env`
or `.env.*`, before running anything on its branch.

The hooks are not a control. A clone where `bun install` never ran has no hooks, so git commits and
pushes with no local check. A hook that cannot find lefthook prints `Can't find lefthook in PATH` and
exits 0. The control is continuous integration: the `commits` job lints every commit message, and the
`gate` job runs the whole gate.

The committed `.claude/settings.json` pre-approves read-only git commands and nothing else, and
denies the `--output` form of `git diff`, `git log` and `git show`, which writes a file. A branch
supplies `cake.cs`, the project files and the tests, so approving a build or a test run for every
clone would run a stranger's code without a prompt. Approve those for yourself in
`.claude/settings.local.json`, which `.gitignore` covers:

```json
{
  "permissions": {
    "allow": ["Bash(dotnet cake.cs:*)", "Bash(dotnet build:*)", "Bash(dotnet test:*)"]
  }
}
```

Two things reach the JS tools from your own environment:

- A root `.env`. The gate refuses a tracked one, but your own untracked `.env` passes the
  tracked-path check. Bun loads it into prettier and commitlint, because `bunfig.toml` holds the
  cooldown alone. `bunx` accepts `--no-env-file` in every position and ignores it. No variable
  either tool reads from it loads code. `bun install` loads the root one too, whatever flag it gets.
  There it moves the registry, and it swaps a package under a frozen lockfile with no `bun.lock`
  change, through `BUN_INSTALL_CACHE_DIR`, or `BUN_CONFIG_SKIP_LOAD_LOCKFILE` with a registry. It
  hands its values to every script it runs. `prepare` passes `--no-env-file`, so `bun run prepare`
  hands lefthook's `install` none of them, but under `bun install` lefthook still sees them.
- The Bun variables. `BUN_OPTIONS` reaches every direct Bun start, such as the `prepare` script's
  lefthook install. A `--preload` in it runs a module first in each. The gate withholds it from the
  processes it starts, and a tool started through `bunx --bun --no-install` does not read it. Leave
  it unset. Bun preloads the module `BUN_INSPECT_PRELOAD` names, and reads `BUN_INSPECT` and
  `BUN_INSPECT_CONNECT_TO` for its inspector. The gate and the hooks leave those three in place, so
  keep them unset in the shell you commit from and run the gate from.

`prepare` starts `bun` by name, and a `package.json` script finds it in every `node_modules/.bin`
before `PATH`. A dependency declaring its own `bun` bin would win there, and it would arrive as a
change to `bun.lock` that review reads.

A token the shell exports reaches every process the gate starts but mise, because mise is the one
process whose environment the gate builds from nothing. A `mise install` run by hand reads the
shell's environment, where `MISE_BACKENDS_<TOOL>` overrides a tool's backend and no setting reports
it. Keep it unset in the shell you run `mise install` from.

## Running it

```powershell
dotnet build src/WingetNudge
$app = 'src/WingetNudge/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WingetNudge.exe'
& $app
& $app check
```

The first opens the picker, and the second runs a verb from the table in
[docs/usage.md](docs/usage.md#command-line). The executable is a GUI process, so a verb's output
reaches the terminal only when the terminal launched it directly. `dotnet run` starts it through
another process, and the output never arrives.

State lives in `%LOCALAPPDATA%\WingetNudge` for a source build and an installed copy alike. A crash
the app survives is written to `crash.log` there. Deleting a file there resets what it holds.

Running `register` from a build output points both scheduled tasks and the notification registration
at that build. Run `unregister` from the same build before deleting it, or install the MSI again,
which registers the installed copy.

### Generated files

`src/WingetNudge.Core/Packages/WingetErrorCodes.cs` comes from the installed winget's own error
table, and the failure text on a package card comes from it. The script formats the file with the
CSharpier `dotnet-tools.json` pins, and the analyzers check it like any other source, so it carries
no generated-code marker. Rerun the script when a winget upgrade adds codes:

```powershell
./tools/Update-WingetErrorCodes.ps1
```

### Screenshots

The README's screenshots come from `src/WingetNudge/Services/DemoInventory.cs`, a fixed inventory
that only a Debug build carries:

```powershell
$env:WINGETNUDGE_DEMO = '1'
& $app
```

It lists well-known packages and tools that are not this machine's, keeps its state in
`%TEMP%\WingetNudge-demo` rather than the real data directory, answers every web request with a 404,
and shows the default Windows blue rather than this machine's accent. Update selected, the run
button and the log links start nothing while it runs, and saving Settings schedules nothing. Every
verb is refused with exit code 2, because each one acts on this machine rather than on the
inventory: it opens the picker and nothing else.

`docs/images/picker-light.png` and `docs/images/picker-dark.png` are captures of it, one per theme.

### Building the MSI

```powershell
dotnet cake.cs --target=installer
```

The package lands at `installer/bin/Release/WingetNudge.msi`: a per-user install under
`%LOCALAPPDATA%\Programs\Winget Nudge` with no administrator prompt. It runs `register` at the end
of setup and `unregister` at the start of an uninstall. A failed `register` fails setup and rolls it
back, and a failed `unregister` never stops an uninstall. When a first install rolls back, setup runs
`unregister` before it removes the files, so no registration outlives them.

Before a first install changes anything, the custom action in `installer/CustomActions` lists the
Windows App Runtime framework packages registered for the installing user. Setup refuses when none of
them reaches the version the app's bootstrapper requires. Repair, uninstall and an upgrade's removal
of the previous version skip the check. The installer project asks the app project for
that package name and version, which come from the `Microsoft.WindowsAppSDK.Runtime` package it
resolved.

Installing the MSI on this machine points its scheduled checks and notification at the build. Windows
Sandbox starts from a clean copy of Windows, so try setup there instead: map `installer/bin/Release`
into it read-only and run `msiexec /i <folder>\WingetNudge.msi /l*v <log>`. Installing a runtime
there from the downloads page tries the other side of the check.

The gate builds it unsigned on every machine. To sign a local build, put a code-signing
certificate's thumbprint from the current user's store in `Directory.Signing.props` at the
repository root, which `.gitignore` keeps out of commits:

```xml
<Project>
    <PropertyGroup>
        <SigningCertificateThumbprint>...</SigningCertificateThumbprint>
    </PropertyGroup>
</Project>
```

Then build the installer directly:

```powershell
dotnet build installer/WingetNudge.Installer.wixproj --configuration Release
```

That signs `WingetNudge.exe`, the app assemblies and the MSI with the Windows SDK's signtool and a
DigiCert timestamp. A self-signed certificate verifies only on a machine that trusts it.

## Where code goes

- `src/WingetNudge.Core`: everything that is not UI. Winget access, tracking and the cooldown gate,
  preferences, manual tools, release notes, the upgrade engine, registration.
- `src/WingetNudge`: the WinUI 3 app, its windows, the notification and the command-line verbs.
- `tests/WingetNudge.Core.Tests`: the xUnit v3 suite over Core, and `RequirementsTests`, which binds
  `docs/install.md` to `Directory.Packages.props`. `RuntimeRequirementTests` covers the installer's
  runtime check, whose decision logic the suite compiles in from `installer/CustomActions`.
- `installer`: the WiX project for the per-user MSI.
- `installer/CustomActions`: the custom action setup runs before it changes anything. It finds the
  Windows App Runtime the app needs among the packages registered for the installing user. It
  targets .NET Framework 4.7.2, because WiX's DTF host runs a managed custom action in the .NET
  Framework.
- `tools`: build-time scripts. `Update-WingetErrorCodes.ps1` regenerates
  `src/WingetNudge.Core/Packages/WingetErrorCodes.cs` from `winget error --output`
  ([Generated files](#generated-files)).

## Code

- CSharpier formats C# and prettier formats everything else it understands, from the settings in
  `.csharpierrc` and `.prettierrc`.
- Analyzer warnings fail the build. `.editorconfig` sets the style rules, including explicit types
  over `var`.
- A finding is fixed, or waived with `[SuppressMessage]` naming its rule and a `Justification`.
  StyleCop.Analyzers' SA1404 fails the build on a waiver whose `Justification` is missing, empty,
  blank or `<Pending>`. `.editorconfig` turns every other StyleCop rule off by category, and SA0001
  by its own key, because it reports with no source location and no category key reaches it. A
  compiler warning (`CSxxxx`) has no inline waiver: `[SuppressMessage]` cannot suppress one.
- No analyzer checks any other inline waiver for a rule or a reason, so the gate refuses each one,
  in any case: `#pragma warning` in any form, `#nullable disable` in any form, `#line` in any form,
  which hides the findings below it or moves them to another file, `[GeneratedCode]`,
  `[UnconditionalSuppressMessage]`, and any C# naming SA1404, since a waiver of that rule switches
  it off for its scope. Roslyn reads a file as generated, and runs no analyzer over it, from a
  `<auto-generated>` or `<autogenerated>` comment or from its name, so the gate refuses the comment
  in a `.cs` file and a `.cs` file named `TemporaryGeneratedFile_*`, `*.designer.cs`,
  `*.generated.cs`, `*.g.cs` or `*.g.i.cs`. It refuses CSharpier's ignore comments in every file the
  `format` row checks.
- The directive match runs over the whole file, because C# ends a line at U+0085, U+2028 and U+2029
  as well as at a carriage return or line feed, and reads U+FEFF and U+001A as blanks. So it also
  refuses a directive-shaped line inside a comment, a string or an inactive `#if` region. The name
  match runs with every `\u` and `\U` escape decoded, and with every format character (Unicode
  category Cf) and every default-ignorable code point removed. An identifier takes escapes, and the
  compiler drops every format character from it, so `Generated­Code` binds to `[GeneratedCode]`.
- The gate reads a `.cs` file as the compiler does, and refuses one it cannot: bytes that are not
  UTF-8, nor UTF-16 after a byte-order mark, which the compiler reads in the machine's code page
  instead. It refuses a `%` in a `.cs` path, which a SARIF log reads as an escape.
- `.editorconfig` is lint configuration, and CODEOWNERS holds it to review. A `generated_code` key,
  and any `dotnet_diagnostic.*.severity` below `warning`, are waivers a reviewer refuses: each
  silences findings for the files it names, with no reason given. The gate sees SA1404 lowered for
  any file, and a waiver that silences a finding, but not a `generated_code` key.
- An icon glyph is a `\uXXXX` escape in the source, never the raw character, so a reviewer can read
  which glyph it is.

## Tests

A test states what the code is supposed to do. Derive the assertion from the requirement, then run
it; never paste in whatever the code returned.

Winget, the Restart Manager, Task Scheduler and notifications sit behind seams, and a test passes a
substitute for each one it could reach. No test needs a real one. Nothing in the suite may upgrade a
package, close an app, show a notification, or touch a scheduled task this machine relies on. Files
go in a temporary directory the test owns.

## The gate

One command, and the only one:

```powershell
dotnet cake.cs
```

[Cake](https://cakebuild.net) runs every task in `cake.cs`, each after the tasks it depends on. It
stops at the first failure and prints a summary table. `dotnet cake.cs --description` lists every
task and what it checks, `dotnet cake.cs --tree` prints what each depends on, and
`dotnet cake.cs --dryrun` prints the order they run in and runs none. The rows that build nothing
run first: `lockfile`, `workflows`, `format`, `prettier` and `toml`. `build`, `tests` and
`installer` build and run the repository's code, so they run last. When a local run fails or
disagrees with continuous integration, [Troubleshooting](#troubleshooting) says why.

`--target=<task>` runs one task and the tasks it depends on. `--target=code` runs everything but
`workflows`. `lockfile` reads `mise.toml`, `mise.lock` and `bunfig.toml`, walks the tree for any
config file a tool would read past the ones the gate names and for any inline waiver no analyzer
checks, and asks git which paths are tracked. git is the one process it starts, so it needs no mise
on the machine, and it is the first task the whole gate runs. Every row that starts dotnet, bunx or
a mise tool runs the same config checks first, so `--exclusive` skips none of them. `--target=tools`
runs `lockfile` and then `mise install`; it is the install continuous integration runs, and `check`
does not reach it.

`workflows` hands actionlint a ShellCheck stand-in in front of the ShellCheck binary it resolved,
then asks actionlint for a finding only ShellCheck reports and for a refusal only the stand-in
reports. actionlint leaves its shell checks off when that command cannot start, and still exits 0.
A clean actionlint run counts for nothing until both come back.

Every row that checks files from the tree prints the files it checked and fails when there are
none, because taplo, prettier and CSharpier each exit 0 having checked nothing. The `format` row
names each C# and XML file to CSharpier and fails unless CSharpier reports checking as many. The
`toml` row names each `.toml` file to taplo and fails unless taplo's own log lists the same files.
The `prettier` row lists its files with a `--debug-check` pass before `--check`. `workflows` names
each workflow file to actionlint, and zizmor logs each file it completes and fails when it collects
none. The `tests` row reads the test run summary and fails unless the total is above zero, every
test succeeded and none was skipped, or when it cannot read the summary: a filter the test
application reads reports each test it leaves out as skipped, and `dotnet test` still exits 0. It
sets `DOTNET_CLI_UI_LANGUAGE=en`, since `dotnet test` localizes the summary's labels, and passes
`--no-ansi`. Every row that reads a tool's output runs the tool with `NO_COLOR=1` and strips escape
sequences before it reads, since `dotnet test` colors its summary on GitHub's runner. The
`format`, `toml` and `workflows` rows take their lists from one walk of the tree. It skips `.git`
at any depth, `node_modules`, `.claude/worktrees` and `.vs` at the root, and the `bin` and `obj`
beside a project file, where the SDK writes, so no row names a file under one of those. It enters a
`bin` or `obj` anywhere else, since the SDK compiles a file there.

The pre-push hook runs the whole gate. A local run asks `gh auth token` for a token and hands the
answer to zizmor alone, which then runs its online audits. With no answer, zizmor runs with
`--offline`. On continuous integration, where GitHub Actions sets `CI`, the gate never starts gh and
runs zizmor with `--offline`, and the shared `workflows` job runs the online audits. The row prints
which mode zizmor runs in and why, and never the token. gh reads `GH_TOKEN` before its keyring, so a
fine-grained read-only token there is the least a local run can hand zizmor. [Safety](#safety) says
what else a token the shell exports reaches.

`tools` depends on `lockfile` and then runs `mise install`, so the lockfile is asserted before
anything installs from it. An address in `mise.lock` is what an install fetches, and an entry naming
a repository other than the one `cake.cs` records is refused before anything downloads from it. The
order is a dependency in `cake.cs`, so on the `gate` job no arrangement of steps can install first.
The Linux linter leg installs through mise alone, with the attestations verified. `check` does
not reach `tools`: a local gate resolves tools an earlier `mise install` put on disk, and the
one network request it makes is zizmor's online audit when `gh` holds a token. `tools` installs
with the attestations re-verified on a cold cache, so every pull request checks the artifacts
`mise.lock` records rather than trusting the run that wrote them.

Continuous integration is the workflow files under `.github/workflows`. The shared jobs call the
reusable workflows in `zachthedev/.github`, pinned by commit with the version beside it:

- `ci.yml`, on every pull request and push to `main`: `gate` runs `dotnet cake.cs --target=tools`
  and then the whole gate on Windows; `commits` lints every commit and the title with commitlint;
  `workflows` runs actionlint and zizmor on Linux from the same `mise.lock`; `sbom` and `snapshot`
  submit the NuGet graph of the restored tree, so `dependency-review` compares real versions
  against the base and, on the release pull request, against the last release tag.
- `cd.yml`, on every push to `main`: `release-pr` keeps the release pull request open and tags the
  merge that releases; `build` builds the MSI and checks its version against the tag; `publish`
  records a build provenance attestation for the MSI and its `SHA256SUMS`, attaches both, and flips
  the draft public once the `release` environment's reviewer approves.
- `codeql.yml`, on the same events plus a Thursday schedule: CodeQL code scanning as advanced
  setup, a committed workflow rather than the default setup a repository setting turns on and leaves
  nothing in the tree for. The checks report as `codeql / Analyze (<language>)`, the names the
  branch ruleset requires. `Analyze (csharp)` runs on Windows with `build-mode: none`, which
  extracts every C# source without building the solution, so code a build generates, here the XAML
  compiler's partial classes, is outside the database. `Analyze (actions)` reads the workflows under
  `.github`. It overlaps the `workflows` job without replacing it: actionlint and zizmor read a
  workflow's own configuration, such as an unpinned action or a permission wider than a job asks
  for, and CodeQL's Actions queries follow attacker-controlled data from an event payload into a
  `run:` block, an action input or an artifact. Neither reports the other's findings.
- `deps.yml`, daily: Renovate, under the updater app's credentials in the `deps` environment.
- `audit.yml`, weekly: the NuGet advisory report over the locked graph, and zizmor's online audits
  over the pinned actions. A red run there is a report, never a check.

### What the rows check

- For each `mise.lock` entry, the gate asserts the `backend`, `url` and `url_api` against what
  `cake.cs` names for that tool. Those three are what an install fetches, so a provenance line
  beside an address somewhere else would be a claim about bytes nobody downloads. `url` has to
  equal, byte for byte, the address `cake.cs` builds from the tool's repository, its tag prefix, the
  pinned version and the asset it names for that platform. `url_api` has to be an asset id under the
  same repository. An address carrying a control or whitespace character, a percent escape, a
  backslash, or a `.` or `..` segment is refused before any comparison. The expected owner and
  assets live in `cake.cs` rather than in `mise.lock`, so moving an install takes an edit to both.
- mise fetches an asset through its `url_api` address in place of `url` when a HEAD on `url` fails,
  and nothing offline ties that asset id to a release. The one `url_replacements` entry in
  `mise.toml` sends that fetch to `url-api-fallback-refused.invalid`, so such an install fails with
  a DNS error rather than installing whatever the id names. `lockfile` refuses a `mise.toml`
  without that entry or with any other, because another entry would move a download away from the
  url `mise.lock` records. `tools` hands `mise install` the same entry through
  `MISE_URL_REPLACEMENTS`, which outranks every config file. A `mise install` run by hand reads the
  files alone.
- mise reads more config files than `mise.toml`, and merges the lockfile beside each one ahead of
  `mise.lock`, so a `mise.local.toml` with a `mise.local.lock` would install from a url the gate
  never read. `lockfile` refuses, by name, any file or directory at the root whose name starts with
  `mise` or `.mise` other than `mise.toml` and `mise.lock`, and `.tool-versions`. That covers
  `.miserc.toml`, the `mise.<env>.toml` and `.mise.<env>.toml` env files, the `mise.windows.toml`
  platform files, the `.local` variants, and the `mise` and `.mise` directories. The `.config`
  directory, where mise reads `.config/miserc.toml` and `.config/mise`, is refused whole. Both
  checks read the file system rather than git, because mise reads an untracked file too. Every mise
  call the gate makes also runs with
  `MISE_OVERRIDE_CONFIG_FILENAMES=mise.toml`, `MISE_OVERRIDE_TOOL_VERSIONS_FILENAMES=none`,
  `MISE_ENV` empty and `MISE_AUTO_ENV=false`, which leave mise reading `mise.toml` alone even when a
  refused file is present.
- `mise.toml` itself holds `[tools]`, `[settings]` and `[tool_config]` alone, because mise evaluates
  `[env]` and `[vars]` templates as it loads the file and runs `[hooks]` during an install.
  `[tools]` carries version strings, and `[settings]` and `[tool_config]` have to equal, whole, the
  tables `cake.cs` holds in `ExpectedSettings` and `ExpectedToolConfig`. `mise.lock` holds the keys
  `mise lock` writes and no others, for the tools `mise.toml` pins. A symbolic link or junction at
  the root, or under `.mise` or `mise`, is refused.
- The gate starts mise with an environment it builds from nothing: `SYSTEMROOT`, `LOCALAPPDATA`,
  `TEMP`, `TMP`, the proxy variables when set, `NO_COLOR=1`, and its own mise settings. No other
  variable, from the shell or anywhere else, reaches mise, so the gate's mise uses mise's default
  directories whatever `MISE_DATA_DIR` or `MISE_GLOBAL_CONFIG_FILE` says, and trusts the checkout
  itself.
- The gate starts mise, gh, bunx, dotnet and git from the absolute path `PATH` names for each,
  skipping empty and relative entries and any entry inside the checkout. Cake's own lookup reads
  `tools` before `PATH`, and Windows reads the current directory for a bare name. So the gate also
  refuses a file at the root or under `tools` named `mise`, `gh`, `bunx`, `bun`, `dotnet`, `node`,
  `git`, `csharpier` or `sbom-tool`, bare or with `.exe`, `.bat`, `.cmd` or `.com`.
- The prettier row runs `bunx --bun --no-install`, which starts `node_modules/.bin/prettier` ahead
  of anything else. With none in the checkout, bunx runs a parent directory's copy, one on `PATH` or
  one in its cache, and says nothing, so the row first checks that `node_modules/.bin/prettier.exe`
  is there, and that a link there resolves to an existing file, and fails naming the install to run
  when either does not hold. `--bun` runs it under the Bun the gate resolved, never whichever node
  `PATH` names, and lefthook's commit-msg hook passes it to commitlint too. The hook checks for no
  install. `bun install` keeps a package it finds already at the version `bun.lock` records, so a
  committed `node_modules/prettier` still runs after an install. `lockfile` refuses every tracked
  path with a `node_modules` segment, in any case, and the prettier row refuses them again before it
  starts bunx. They refuse a tracked path with a `bin` or `obj` segment too, since MSBuild imports
  files from `obj` by wildcard, and a committed one reaches every checkout. They also refuse a
  tracked `.env` or `.env.<name>` at any depth. Bun loads the one at the root into prettier and
  commitlint, and no bunx flag stops it, and one anywhere else holds values meant to stay out of
  git. `git ls-files` answers what is tracked, so the `node_modules` an install writes, and a
  contributor's own `.env`, pass. An extraction from `git archive` has no `.git` at the root and
  tracks nothing, so the check starts no git there and passes. Beside a `.git`,
  `git rev-parse --show-cdup` has to print an empty line, because git searches the directories above
  a `.git` it cannot open and would list another repository's paths. It compares no paths, so a
  checkout reached through a junction passes. The `gate` job's `bun install` takes
  `--ignore-scripts`, because a frozen lockfile still runs the lifecycle scripts `package.json`
  names.
- `bunfig.toml` holds `[install]` with `minimumReleaseAge` alone. Bun reads the file on every
  start, and no flag stops it. A top-level `preload` there runs a module before the first line of
  whatever Bun starts, prettier and commitlint included. Every other key reaches Bun too: an
  `[install]` registry moves where even a frozen install downloads from. So `lockfile` and the
  prettier row refuse any other key or table before the gate starts bunx. The file's lines, less
  comments, have to read `[install]` or `minimumReleaseAge = ` and digits, in printable ASCII, so
  Bun and the gate cannot read it two ways. The gate reads no value there and passes a checkout
  with no `bunfig.toml`, so review holds the cooldown.
- The prettier row runs prettier with `--config .prettierrc`. On prettier 3.9.8 that stops the read
  of every other config file, a nested `.prettierrc` and a `package.json` `prettier` key included,
  so the gate refuses none of them. An editor's prettier still reads one. The row also passes
  `--no-editorconfig`, so no `.editorconfig` sets the indent, line ending or width prettier formats
  with.
- The config walk behind `lockfile` is the rows' walk of the tree described above, and it also reads
  the `bin` and `obj` beside each project, which the rows skip, because MSBuild imports files from
  `obj`. It reads the file system, because a tool reads an untracked file too. It refuses any
  directory link, and any directory it cannot list, by name.
- No config file is held to fixed text. CODEOWNERS review holds `.prettierrc`, `.prettierignore`,
  `.taplo.toml`, `.github/zizmor.yml`, `.csharpierrc`, `.csharpierignore`, `lefthook.yml` and the
  three `.editorconfig` files, and a reviewer refuses a line that takes a file out of a row or
  turns a finding off. The `format` and `toml` rows fail when their tool drops a file they named,
  and the prettier row does not: a line in `.prettierignore` takes files out of it without a word.
  The prettier row passes `--ignore-path .prettierignore`, which replaces prettier's default pair,
  so `.gitignore` takes nothing out of it, and `.prettierignore` lists the local paths `.gitignore`
  covers that prettier would read. A `.github/actionlint.yaml` is refused, because its `paths` block
  ignores actionlint's errors by pattern.
- The `format` row takes each file of the walk with an extension CSharpier 1.3.0 formats, and names
  them in batches that fit a Windows command line, each held to the count above. It passes
  `--config-path` with the absolute path of `.csharpierrc`, `--ignore-path .csharpierignore` and
  `--include-generated`. CSharpier then reads no other config or ignore file, and checks a file
  whose header calls it generated. The path is absolute because CSharpier anchors a config's
  `overrides` to its directory, as an editor's CSharpier does. A named file is checked whatever
  `.gitignore` says, so a local `Directory.Signing.props` is checked too.
- The `build`, `tests` and `installer` rows name the root `Directory.Build.props`,
  `Directory.Build.targets` and `Directory.Packages.props` to MSBuild, so it searches above no
  project for them. The root holds no `Directory.Build.targets`, and MSBuild imports a named file
  only when it exists. They pass `RestoreForce=true`, so each build's restore writes a project's
  `obj` imports from NuGet again, where a restore with nothing to do keeps a changed one. The same
  rows pass `ImportDirectorySolutionProps=false` and
  `ImportDirectorySolutionTargets=false`, so a solution build imports no `Directory.Solution.props`
  or `.targets` from the root or above it. They pass `DiscoverGlobalAnalyzerConfigFiles=false`,
  which stops the compiler finding a file named `.globalconfig` above a source file. An
  `.editorconfig` that sets `is_global` is global under any name, and MSBuild still finds one in any
  directory above a source file, up to the drive root, past the root file's `root = true`. The gate
  refuses one in the tree, and no check reaches one above the checkout, which a pull request cannot
  write. The `build` and `installer` rows pass `-noAutoResponse`, so no `Directory.Build.rsp` adds
  switches. `dotnet test` reads none, and hands that switch to the test application, which refuses
  it. NuGet still reads a contributor's own settings, since the gate names no `RestoreConfigFile`.
- `ErrorLog` in `Directory.Build.props` has every compile write a SARIF log to
  `obj/<configuration>/waivers.sarif` beside its project. After each configuration the `build` row
  reads the log of every project `WingetNudge.slnx` names. It refuses an in-source suppression whose
  justification holds no letter or digit once invisible characters are removed, such as a pragma's,
  which records none. It refuses a justification that does not decode as text, such as a lone
  surrogate, and any suppression of SA1404, however it was spelled.
  A suppression in the project's `obj`, such as the XAML compiler's output or a source generator's,
  is that tool's own. One anywhere else outside the tree is refused, since only a `#line` directive
  or a file the tree walk skips puts it there. The same log records each severity a rule takes
  across the compile's files. `WarningsAsErrors` in `Directory.Build.props` names SA1404, which
  gives a source generator's output the rule as an error too, so the row fails unless SA1404 is an
  error for every file the compile analyzes: a project with its analyzers off, without StyleCop,
  or with SA1404 lowered for any of its files, turns it red. A file Roslyn reads as generated is
  not analyzed, so the log says nothing about it. A missing or unreadable log fails the row, and so
  does one older than its project's intermediate assembly, which an earlier compile wrote. The XAML
  compiler's first pass runs the compiler on every build with no analyzers, so it writes
  `obj/<configuration>/xaml-first-pass.sarif` instead. The row prints the compiles and the number
  of suppressions it read. The `installer` row compiles the app and the custom action again for the
  MSI, so it reads the Release logs again after the package builds. The `tests` row compiles
  nothing.
- The SARIF log records neither `NoWarn` nor `WarningsNotAsErrors`, so before each configuration
  the `build` row asks MSBuild to evaluate each project under the build row's properties. It
  refuses `TreatWarningsAsErrors` other than true, SA1404 missing from `WarningsAsErrors` or named
  in either of the others, a `WarningLevel` below 1, any `CodeAnalysisRuleSet`, and `RunAnalyzers`
  or `RunAnalyzersDuringBuild` false. It splits each warning list at a semicolon, a comma or any
  whitespace, as the compiler does, and reads each value however a project file spells it. It reads
  those properties and nothing else. CODEOWNERS review of the project files is the control for what
  it does not see, as it is for `.editorconfig`, and a reviewer refuses each of these:
  - a `Compile` item that is not a `.cs` file the tree walk names, such as a file of another type,
    one outside the tree, or one in a project's `bin` or `obj`, since the text scan reads none of
    them;
  - a `Using` item, alias included, that names a refused attribute;
  - an `EditorConfigFiles` or `GlobalAnalyzerConfigFiles` item;
  - a compiler response file;
  - a condition on a property the evaluation does not pass, such as the `Platform=x64` the
    `installer` row builds the custom action with, or the absolute `PublishDir` it publishes the
    app to;
  - a target that changes a setting or adds an item while the build runs.
- The gate refuses, at any depth and in any case, a `Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props` or `nuget.config` below the root, and any
  `Directory.Build.rsp`, `Directory.Solution.props` or `Directory.Solution.targets`. `.gitignore`
  keeps the template's re-include of `Directory.Build.rsp`, so a local one shows in `git status`
  beside the refusal. It refuses any `*.csproj.user` or `*.wixproj.user`, in `bin` and `obj` too,
  which MSBuild imports after the project body, so a property there switches what the build rows
  check. A debug profile belongs in `Properties/launchSettings.json`, which MSBuild does not import.
  It refuses a file in `obj` named `<project file>.<name>.props` or `.targets`, which MSBuild
  imports by wildcard, unless `<name>` is NuGet's own `nuget.g`. It refuses a `testconfig.json` or
  `xunit.runner.json`, bare or behind an assembly name, in `bin` and `obj` too: the test application
  reads each as config, and the build copies a `testconfig.json` beside a test project into its
  output.
- The analyzers read every `.editorconfig` above each source file, up to the root file's
  `root = true`. So the gate refuses every `.editorconfig` below the root but
  `src/WingetNudge/.editorconfig` and `tests/.editorconfig`, and every `.globalconfig`, which an
  editor's build reads. It refuses an `.editorconfig` that sets `is_global` anywhere, those two and
  the root one included, since the analyzers apply a global config to every file of a project that
  finds it.
- A `.config` directory at the root is refused whole, in any case. mise, `dotnet tool run`,
  cosmiconfig and lefthook each read config from it. cosmiconfig runs a module there on every
  commitlint start, `--config` or not, and a tool manifest there outranks `dotnet-tools.json`. None
  of them reads a `.config` below the root. `dotnet-tools.json` keeps `"isRoot": true`, so
  `dotnet tool run`, which takes no manifest path, reads no manifest above the checkout. The gate
  does not check it, so review keeps it there.
- A config name stays refused only where no flag the gate passes stops the read. prettier,
  CSharpier, taplo and zizmor each read the one config the gate names and search for no other, so
  their other names pass. The gate refuses these, at the depths each tool searches, in any case:
  every commitlint search place at the root but `commitlint.config.js`, and a `package.yaml` at any
  depth, which cosmiconfig reads; `lefthook.yaml`, `.json`, `.jsonc` and `.toml`, and any
  `.lefthook.*`, at the root; and a root `cake.config`, which Cake reads before any task runs. It
  refuses a `tsconfig.json` or `jsconfig.json` at any depth, which Bun reads for the modules
  prettier and commitlint load, and the repository has no TypeScript.
- A `package.json` with a top-level `commitlint`, `cosmiconfig` or `patchedDependencies` key is
  refused. `bun install` applies a root `patchedDependencies` entry to the package it names, under
  a frozen lockfile too and with no `bun.lock` change, so a patch would change what prettier or
  commitlint runs. Bun reads the key in an escaped spelling too, and the refusal matches the
  decoded name. The refusal reads every copy of a key named twice, so a second copy hides none.
  Write each JSON key once all the same: Bun keeps the first of two copies, and most other readers
  keep the last.
- lefthook's commit-msg hook passes `--config commitlint.config.js`, so commitlint searches for no
  other config. The shared `commits` job runs commitlint without it, so a planted
  `.commitlintrc.json` passes that job, and the refusal above is where it lands. cosmiconfig runs a
  module from `.config` at the root before commitlint reads `--config`, and the hook runs before
  any gate row. So the hook's first job fails on a `.config`, and `piped: true` stops the hook
  there, before commitlint starts. That job stops an accidental `.config`, not a hostile branch:
  lefthook merges a `.config/lefthook-local.*` file over `lefthook.yml` before any job runs, so the
  same directory can replace the job, which is why [Safety](#safety) says to read a branch first.
  lefthook also merges a `lefthook-local.*` or `.lefthook-local.*` file at the root on every run,
  and no switch stops it. The tracked-path check refuses a tracked one, and `.gitignore` covers a
  contributor's own.
- prettier's CLI skips a directory named `.git`, `.sl`, `.svn`, `.hg` or `.jj` without a word, and
  the tree walk skips `.git` at any depth, so no row checks a file under one. Name no directory that
  way. Review reads what a row skips.
- The tracked-path check refuses a `zizmor: ignore[` comment in any tracked file under `.github`,
  because zizmor honors one with no config. A waiver goes in `.github/zizmor.yml` as a
  `rules.<audit>.ignore` entry. zizmor takes no config waiver for a composite action's finding, so
  such a finding cannot be waived here.
- The `workflows` row passes actionlint a `-shellcheck` command that starts `cake.cs` again as a
  ShellCheck stand-in, through a hidden argument it reads before Cake reads any. actionlint writes
  each script to the stand-in's stdin as ShellCheck would read it, with every YAML escape decoded
  and every fold joined. The stand-in refuses any line holding `#`, then `shellcheck` and a blank,
  in any case, as an error finding, because ShellCheck honors every such directive inside a `run:`
  script and nothing holds a waiver for one. No line check over the file sees through an escape or a
  fold. Otherwise the stand-in runs the pinned ShellCheck over the same bytes and passes its output
  and exit code through. It adds about a quarter of a second per script. The paths in the command go
  single-quoted with forward slashes, since actionlint drops an unquoted backslash and turns
  ShellCheck off without a word.
- The row also refuses a `shell:` on a step or under `defaults.run`, for the workflow or a job,
  other than `bash`, `sh` or `pwsh`. actionlint hands ShellCheck a script by the shell's first
  word, so `shell: /bin/bash` runs bash with no ShellCheck at all. It reads each workflow with
  YamlDotNet for that, so an escape or an alias resolves to the value GitHub reads, and it refuses
  a workflow YamlDotNet cannot read.
- The two `secrets-inherit` waivers name `cd.yml` and `deps.yml` whole. A waiver binds a file,
  never the workflow a job calls, so the `workflows` row runs zizmor again with no config and no
  ignores. Every job passing `secrets: inherit` has to call a workflow under
  `zachthedev/.github/.github/workflows/`, and zizmor's count of such jobs has to equal the
  `secrets: inherit` lines in the workflows. The row prints each callee.
- `mise.toml` sets `locked_verify_provenance`, so an install re-verifies each attestation rather
  than trusting the lockfile's recorded one, and `[tool_config] locked = true`, which mise enforces
  whatever `locked_scopes` says. `lockfile_platforms` there names the platforms every `mise.lock`
  entry carries, so a bare `mise lock` writes both legs and the gate refuses an entry for a
  platform the list does not name. mise also reads a nested `[tools.<tool>.platforms.<name>]`
  table for any platform, and `mise lock` writes the quoted `platforms.<name>` form alone, so the
  gate refuses an entry carrying the nested one. [Safety](#safety) names the environment variable
  that overrides a tool's backend, which no setting reports.
- The gate removes `SHELLCHECK_OPTS` from its own environment as it starts, in any letter case, so
  actionlint, its two canaries and the stand-in's ShellCheck never get it. ShellCheck reads that
  variable as extra arguments past actionlint's `--norc`, so an `-e` there drops a finding.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org), enforced by the commit-msg hook and by
the `commits` job. The header and body line limits are the ones `commitlint.config.js` sets.
`.github/commit-scopes.json` lists each scope and what it covers, and commitlint accepts no other.
Omit the scope rather than invent one. A new top-level area earns a scope in that file, in the
change that adds the area.

The commit message is where the history of a change goes: what was wrong, what the change does, and
why an approach was rejected. Code comments describe the code as it is now.

A pull request merges by squash alone, and the branch is deleted after. A one-commit pull request
lands under that commit's subject and a longer one under the pull request's title, each with the
pull request number appended, which is why the title is held to the same rules.

A pull request's title takes the type of its most user-facing commit, and `!` when any commit
breaks something users see. A squash of several commits lands the title's type alone. A `feat`
under a `chore` title never reaches the changelog. A `!` on one of those commits is lost too, with
its major bump.

A revert is written `revert(<scope>): <what it undoes, in fresh words>`, in a commit subject and a
pull request title alike. A `Refs: <sha>` footer names each commit it reverts. A reverted header
copied whole can overrun the header limit `commitlint.config.js` sets. commitlint skips the
`Revert "..."` subject that git and GitHub write. release-please cannot parse it, so that revert
never reaches the changelog.

commitlint also skips a commit whose header starts with a `commit-message` prefix
`.github/dependabot.yml` sets, then `: `, when a line after that header starts
`Signed-off-by: dependabot[bot] <`, the trailer Dependabot writes. Here that header is `fix(deps): `.
A Dependabot body carries lines past the width limit, and the body rule exempts only a line holding
a URL. A one-commit pull request lands under its commit's header, so a skipped commit lands a
header that starts with Dependabot's type and scope, and the rest of it goes unchecked. A one-line
pull request title has no line after its header, so the title lint checks it.

github.com shows a commit subject whole up to 72 characters and cuts it at 73. The 72 applies to
the header that lands. The `commits` job lints a pull request's title, or a one-commit pull
request's subject, with ` (#N)` appended. So a title fits in 64 to 67 characters, by the width of
the pull request number. A Dependabot pull request whose landed header runs past 72 fails that lint.
It is closed, and the bump is taken by hand. No Dependabot pull request here ran past it.

Each version heading in `CHANGELOG.md` after the first links GitHub's compare view from the previous
tag. That view lists every commit in the release, hidden types included.
`git log --oneline <previous tag>..<tag>` lists the same commits locally.

The ruleset on `main` requires one approving review from a code owner, and `CODEOWNERS` names the
owner alone.

## Dependencies

- NuGet versions live in `Directory.Packages.props`, and every project has a `packages.lock.json`.
  The gate restores in locked mode, so a changed package graph fails until the lock files are
  regenerated and committed with it.
- `global.json` pins the .NET SDK with `rollForward` set to `patch`. The app publishes
  self-contained, so the MSI carries the runtime of the SDK that builds it. Continuous integration
  installs the pinned SDK and builds with it. A contributor on a later patch in the same feature
  band still runs the gate. The pin is always the SDK carrying the newest runtime past the
  cooldown.
- Restore audits every package, transitive ones included, against nuget.org's advisory database,
  and every finding warns. The weekly `audit` workflow in `.github/workflows/audit.yml` lists every
  advisory against the locked graph, whatever its severity, in the run's summary.
- On a pull request, the `dependency-review` job in `.github/workflows/ci.yml` diffs GitHub's
  dependency graph between base and head. It fails on a high or critical advisory against a package
  the pull request adds or moves, and passes a package it leaves alone. It sees the direct npm
  packages in `package.json`, every action pin, and every NuGet package the restored graph holds,
  direct and transitive: the graph reads each `.csproj` alone, where Central Package Management
  leaves no version, so the `sbom` and `snapshot` jobs submit the restored graph for every pull
  request head this repository owns and every push to `main`, recorded under
  `Directory.Packages.props`.
- Every NuGet package in that snapshot reads as runtime scope, test packages included, so the
  release pull request's second check, runtime scope against the last release tag, also blocks on
  an advisory against a test-only package. The waiver is the shared workflow's `allow-ghsas`
  input, passed in `ci.yml` with a comment beside it naming the advisory.
- NuGet has no cooldown file, so the three-day wait on a NuGet bump is Renovate's alone.
- [Renovate](https://docs.renovatebot.com) proposes updates on Monday mornings, one grouped pull
  request per ecosystem, and never for a version younger than three days.
  `.github/workflows/deps.yml` calls the shared `deps` workflow, which runs it under the updater
  app's credentials from the `deps` environment. `.github/renovate.json` extends the
  `csharp-installer` preset in `zachthedev/.github`, which holds the schedule, the cooldown and
  the grouping, and adds what is true of this repository alone.
- A security fix skips both the schedule and the wait. `bunfig.toml` still holds the wait for Bun's
  own resolution, so a security bump can leave the pull request red with
  `blocked by minimum-release-age`. Audit the version, then run
  `bun install --minimum-release-age 0` once and commit the lock file.
- `WinGetVersion` and `WinGetModuleSha256` in `Directory.Packages.props` move together, by hand. The
  first is the winget release the COM projection comes from. The second is the SHA-256 of the
  `Microsoft.WinGet.Client` package of the same version on the PowerShell Gallery, the only source
  of `winrtact.dll`. Renovate holds the projection for that reason. Change both in one commit. Set
  `WinGetVersion` first. The commands below read it back from that file and print the hash
  `WinGetModuleSha256` takes:

  ```powershell
  $version = (Select-Xml -Path Directory.Packages.props -XPath '//WinGetVersion').Node.InnerText
  Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/package/Microsoft.WinGet.Client/$version" -OutFile "$env:TEMP\winget-client.nupkg"
  (Get-FileHash -Path "$env:TEMP\winget-client.nupkg" -Algorithm SHA256).Hash
  ```

  Then restore with `dotnet restore --force-evaluate` so the lock files pick up the new projection,
  and run the gate.

- `docs/install.md` restates the winget and Windows App Runtime versions a user needs, because a user
  has no clone to read `Directory.Packages.props` from. `RequirementsTests` binds each to its pin:
  `WinGetVersion` at major.minor, and the `Microsoft.WindowsAppSDK` version whole, because the app's
  bootstrapper refuses an older runtime. A pull request that moves either stays red until the
  Requirements section names the new version. The installer's runtime check follows the same bump
  with no edit, because the build reads the runtime package the app resolved.
- `WixToolset.Sdk` in `installer/WingetNudge.Installer.wixproj` and the two `WixToolset.Dtf` packages
  in `Directory.Packages.props` are one WiX release. `.github/renovate.json` groups them into one
  pull request. WiX 7 builds neither project until its Open Source Maintenance Fee EULA is
  accepted, which `AcceptEula` in `Directory.Build.props` does for both.
- The workflow linters and taplo are pinned in `mise.toml`, and `mise.lock` records the artifact
  each version resolved to. Both legs install from those two files, so no pin is asserted against a
  copy of itself. Renovate rewrites both in one pull request by running `mise lock`. The pins sit in
  a data file rather than in `cake.cs`, because a formatter moves source and a pin that moves is a
  pin no tool can read. A pin, and the version `mise.lock` records for it, is digit groups joined by
  single dots, such as `0.10.0`, and the gate refuses anything else before it builds a `url`. Each
  pin becomes part of the `url` the gate asserts and the path it runs.
- mise verifies a GitHub build attestation for actionlint and zizmor, because aqua's registry
  declares a signer workflow for each. It verifies none for ShellCheck. `koalaman/shellcheck`
  declares neither a signer workflow nor a checksums file at any version constraint, so ShellCheck's
  integrity here is the recorded hash alone. The gate asserts a checksum for every tool and
  provenance for each one that carries it.
- mise verifies no attestation for taplo either. Its release assets carry no GitHub digest and its
  aqua entry names no checksum file, so `mise lock` records no checksum for it. Its two checksum
  lines are the sha256 of the artifact at each recorded url, computed as `mise.toml` says, and a
  relock at the same version keeps them. A taplo bump drops them, so its pull request stays red at
  `lockfile` until the new hashes are computed and committed in the same change.
- StyleCop.Analyzers is one `GlobalPackageReference` in `Directory.Packages.props`, conditioned to
  `.csproj` projects, so it reaches every C# project and `cake.cs`, and no wixproj. Reviewers hold
  that line as the one place its version lives, since no check compares the version each project
  resolves. The `build` row's canary fails a compile that loads no SA1404, and a locked restore
  fails a lock file that disagrees with the references. The canary reads the solution's compiles
  alone, and `cake.cs` writes no log, so review of `Directory.Packages.props` and
  `cake.packages.lock.json` is the control for StyleCop reaching `cake.cs`.
- ShellCheck is a pinned dependency of this repository on both legs. The `rhysd/actionlint` image
  bundles a ShellCheck copied out of `koalaman/shellcheck-alpine:stable` when that image is built,
  so a run through the image has no pin on the ShellCheck it executes. One `mise.toml` entry drives
  the binary both legs run.
- The `gate` job pins mise itself on its `jdx/mise-action` line in `.github/workflows/ci.yml`. The
  shared `workflows` job pins its own on the same action's line in the reusable workflow `ci.yml`
  calls. The action verifies its download against the release's minisign-signed `SHASUMS256.txt`,
  which is the check on the tool that verifies the linters. A local run takes whichever mise is on
  `PATH`.
- `cake.cs` restores in locked mode against `cake.packages.lock.json`, and it imports
  `Directory.Packages.props`. After changing the Cake.Sdk version in `global.json`, the Tomlyn or
  YamlDotNet version, the StyleCop.Analyzers version, or any `PackageVersion` naming a package
  Cake.Sdk depends on, regenerate it with `dotnet restore cake.cs --force-evaluate`.

## Releases

[release-please](https://github.com/googleapis/release-please) keeps a release pull request open
against `main`, carrying the next version and the changelog it would ship. The commit types decide
the version: a breaking change bumps the major, a `feat` the minor and anything else the changelog
carries the patch.

`changelog-sections` in `release-please-config.json` decides which types cut a release, and `hidden`
there is the release switch rather than a display preference. release-please opens no pull request
when the changelog it rendered came out empty. The visible set is the one the `zachthedev/.github`
handbook names, and every other type stays hidden, which keeps a README edit, a Renovate tooling
bump or a formatting commit from shipping an MSI. A breaking change reaches the changelog whatever
its type says, and takes the major. `initial-version` in the same file names the first version the
tool cuts.

Merging that pull request tags the commit and creates the GitHub release as a draft, in the same
`cd.yml` run that then builds the MSI, checks that its version matches the tag, and waits for a
maintainer to approve the `release` environment. Only then does it record a build provenance
attestation, attach the MSI and its `SHA256SUMS`, and publish the draft, so a visitor never reaches
a release with nothing on it. `publish.yml` in `zachthedev/.github` signs the attestation, so
[docs/install.md](docs/install.md#check-the-download) names it as the signer workflow. Nothing
rebuilds an existing release: the publish refuses a tag that does not name the run's commit, so a
release that failed is recovered by cutting the next version.

A release publishes through an advisory. Nothing blocks after the merge: users hold the version
they have until the next one, and the fix ships as the next version. Renovate's `security` group
opens that bump without waiting for the schedule, and merging it cuts the release.

release-please owns the version in `Directory.Build.props` and the whole of `CHANGELOG.md`.

Release MSIs are unsigned for now. A local build signs when `Directory.Signing.props` names a
certificate; [Building the MSI](#building-the-msi) shows how.

## Troubleshooting

- Without a matching SDK, `dotnet` prints the install command the `errorMessage` in `global.json`
  names. [Dependencies](#dependencies) says which SDK matches.
- `lockfile` refuses a mise config file of your own at the root, such as a `mise.local.toml`,
  whether git tracks it or not ([What the rows check](#what-the-rows-check)). Keep local mise
  settings in mise's global config.
- A local run that disagrees with continuous integration may have run another copy of a tool. The
  prettier row and the commit-msg hook start theirs with `bunx --bun --no-install`, which runs
  `node_modules/.bin/<tool>` in the checkout and never downloads. With no install there, the
  prettier row fails and names the install to run, and the hook runs the first copy bunx finds in a
  parent directory's `node_modules/.bin`, then on `PATH`, then in Bun's cache, and says nothing
  about which. With an install older than `bun.lock`, both run that older copy, and the row's check
  passes it. The `gate` job's `bun install` is fresh, so neither happens there.
  [Worktrees](#worktrees) says how each worktree gets an install of its own.
- In a worktree under the main checkout with no `dotnet-tools.json` of its own, `dotnet tool run`
  uses the main checkout's manifest, so the `format` row can run another CSharpier than continuous
  integration does.
- A personal `.env` reaches a local run and never continuous integration. `PRETTIER_EXPERIMENTAL_CLI`
  set there turns the prettier row red, since that CLI refuses `--config`. A Bun variable in your
  shell changes every Bun start the same way, and [Safety](#safety) lists them.

## What never happens

- No hand edit of the version in `Directory.Build.props` or of `CHANGELOG.md`. release-please
  writes both from the commits in each release pull request, and a hand edit is overwritten by the
  next one or disagrees with the tag it cuts.
- No invented commit scope. commitlint accepts only the scopes `.github/commit-scopes.json` lists,
  so an invented one fails the hook and the `commits` job; omit the scope instead.
- No `mise.lock` line is written outside `mise lock`, except a checksum computed as `mise.toml` says.
  Its entries are the addresses an install fetches and the checksums it verifies against, so a
  hand-written line is an address nobody verified.
- No analyzer disabled, finding suppressed or assertion deleted to make the gate pass without saying
  why in the same change. A task that fails is reporting something.
