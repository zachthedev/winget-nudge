# Contributing

[docs/dev.md](docs/dev.md) takes a fresh clone to a running app and a green gate. This file holds
the rules that apply to every change.

## Setup

[docs/dev.md](docs/dev.md#prerequisites) names the toolchain and the pin file each tool's version
lives in. Install it before the first commit: `bun install` runs `lefthook install`, which writes
the git hooks, and `mise trust` then `mise install` put the workflow linters and taplo on disk. A
clone where `bun install` never ran has no hooks, so git commits and pushes with no local check. A
hook that cannot find lefthook prints `Can't find lefthook in PATH` and exits 0. The control is
continuous integration: the `commits` job lints every commit message, and the `gate` job runs the
whole gate.

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

## The gate

One command, and the only one:

```powershell
dotnet cake.cs
```

[Cake](https://cakebuild.net) runs every task in `cake.cs`, each after the tasks it depends on. It
stops at the first failure and prints a summary table. `dotnet cake.cs --description` lists every
task and what it checks, and `dotnet cake.cs --tree` prints the order they run in.

`--target=<task>` runs one task and the tasks it depends on. `--target=code` runs everything but
`workflows`. `lockfile` reads two data files and starts no process, so it needs no mise on the
machine, and it is the first task the whole gate runs. `--target=tools` runs `lockfile` and then
`mise install`; it is the install continuous integration runs, and `check` does not reach it.

`workflows` hands actionlint the ShellCheck binary it resolved, then asks actionlint for a finding
only ShellCheck reports. actionlint leaves its shell checks off when that binary cannot start, and
still exits 0. A clean actionlint run counts for nothing until that finding comes back.

The pre-push hook runs the whole gate. A local run asks `gh auth token` for a token and hands the
answer to zizmor alone, which then runs its online audits. With no answer, zizmor runs with
`--offline`. On continuous integration, where GitHub Actions sets `CI`, the gate never starts gh and
runs zizmor with `--offline`, and the shared `workflows` job runs the online audits. gh reads
`GH_TOKEN` before its keyring, so a fine-grained read-only token there is the least a local run can
hand zizmor. A token the shell exports reaches every process the gate starts but mise, because
the gate clears nothing else from the environment it inherits.

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

Each version heading in `CHANGELOG.md` after the first links GitHub's compare view from the previous
tag. That view lists every commit in the release, hidden types included.
`git log --oneline <previous tag>..<tag>` lists the same commits locally.

The ruleset on `main` requires one approving review from a code owner, and `CODEOWNERS` names the
owner alone. GitHub does not count an author's approval of their own pull request, so `gh pr merge`
on the owner's pull request is refused with `the base branch policy prohibits the merge`.
`gh pr merge --admin` is the way through: it merges on the owner's bypass of the ruleset rather than
on a review. Wait for green checks before running it, because a bypass enforces nothing.

## Where code goes

- `src/WingetNudge.Core`: everything that is not UI. Winget access, tracking and the cooldown gate,
  preferences, manual tools, release notes, the upgrade engine, registration.
- `src/WingetNudge`: the WinUI 3 app, its windows, the notification and the command-line verbs.
- `tests/WingetNudge.Core.Tests`: the xUnit v3 suite over Core, and `RequirementsTests`, which binds
  `docs/install.md` to `Directory.Packages.props`.
- `installer`: the WiX project for the per-user MSI.
- `tools`: build-time scripts. `Update-WingetErrorCodes.ps1` regenerates
  `src/WingetNudge.Core/Packages/WingetErrorCodes.g.cs` from `winget error --output`.

## Tests

A test states what the code is supposed to do. Derive the assertion from the requirement, then run
it; never paste in whatever the code returned.

Winget, the Restart Manager, Task Scheduler and notifications sit behind seams, and a test passes a
substitute for each one it could reach. Nothing in the suite may upgrade a package, close an app,
show a notification, or touch a scheduled task this machine relies on. Files go in a temporary
directory the test owns.

## Code

- CSharpier formats C# and prettier formats everything else it understands, from the settings in
  `.csharpierrc` and `.prettierrc`.
- Analyzer warnings fail the build. `.editorconfig` sets the style rules, including explicit types
  over `var`.
- An icon glyph is a `\uXXXX` escape in the source, never the raw character, so a reviewer can read
  which glyph it is.

## Dependencies

- NuGet versions live in `Directory.Packages.props`, and every project has a `packages.lock.json`.
  The gate restores in locked mode, so a changed package graph fails until the lock files are
  regenerated and committed with it.
- `global.json` pins the .NET SDK with `rollForward` set to `patch`. The app publishes
  self-contained, so the MSI carries the runtime of the SDK that builds it. Continuous integration
  installs the pinned SDK and builds with it. A contributor on a later patch in the same feature
  band still runs the gate. The pin is always the SDK carrying the newest runtime past the
  cooldown. Without a matching SDK, `dotnet` prints the install command `errorMessage` names.
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
  of `winrtact.dll`. Renovate holds the projection for that reason.
- `docs/install.md` restates the winget and Windows App Runtime versions a user needs, because a user
  has no clone to read `Directory.Packages.props` from. `RequirementsTests` binds each to its pin:
  `WinGetVersion` at major.minor, and the `Microsoft.WindowsAppSDK` version whole, because the app's
  bootstrapper refuses an older runtime. A pull request that moves either stays red until the
  Requirements section names the new version.
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
- The gate also asserts the `backend`, `url` and `url_api` of every entry against what `cake.cs`
  names for that tool. Those three are what an install fetches, so a provenance line beside an
  address somewhere else would be a claim about bytes nobody downloads. `url` has to equal, byte
  for byte, the address `cake.cs` builds from the tool's repository, its tag prefix, the pinned
  version and the asset it names for that platform. `url_api` has to be an asset id under the same
  repository. An address carrying a control or whitespace character, a percent escape, a backslash,
  or a `.` or `..` segment is refused before any comparison. The expected owner and assets live in
  `cake.cs` rather than in `mise.lock`, so moving an install takes an edit to both.
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
  `mise` or `.mise` other than `mise.toml` and `mise.lock`, any entry under `.config` whose name
  starts with `mise`, and `.tool-versions`. That covers `.miserc.toml` and `.config/miserc.toml`,
  the `mise.<env>.toml` and `.mise.<env>.toml` env files, the `mise.windows.toml` platform files,
  the `.local` variants, and the `mise`, `.mise` and `.config/mise` directories. It reads the file
  system rather than git, because mise reads an untracked file too, so keep local mise settings in
  mise's global config. Every mise call the gate makes also runs with
  `MISE_OVERRIDE_CONFIG_FILENAMES=mise.toml`, `MISE_OVERRIDE_TOOL_VERSIONS_FILENAMES=none`,
  `MISE_ENV` empty and `MISE_AUTO_ENV=false`, which leave mise reading `mise.toml` alone even when a
  refused file is present.
- `mise.toml` itself holds `[tools]`, `[settings]` and `[tool_config]` alone, because mise evaluates
  `[env]` and `[vars]` templates as it loads the file and runs `[hooks]` during an install.
  `[tools]` carries version strings, and `[settings]` and `[tool_config]` have to equal, whole, the
  tables `cake.cs` holds in `ExpectedSettings` and `ExpectedToolConfig`. `mise.lock` holds the keys
  `mise lock` writes and no others, for the tools `mise.toml` pins. A symbolic link or junction at
  the root, or under `.config`, `.mise` or `mise`, is refused.
- The gate starts mise with an environment it builds from nothing: `SYSTEMROOT`, `LOCALAPPDATA`,
  `TEMP`, `TMP`, the proxy variables when set, and its own mise settings. No other variable, from
  the shell or anywhere else, reaches mise, so the gate's mise uses mise's default directories
  whatever `MISE_DATA_DIR` or `MISE_GLOBAL_CONFIG_FILE` says, and trusts the checkout itself.
- `mise.toml` sets `locked_verify_provenance`, so an install re-verifies each attestation rather
  than trusting the lockfile's recorded one, and `[tool_config] locked = true`, which mise enforces
  whatever `locked_scopes` says. `lockfile_platforms` there names the platforms every `mise.lock`
  entry carries, so a bare `mise lock` writes both legs and the gate refuses an entry for a
  platform the list does not name. mise also reads a nested `[tools.<tool>.platforms.<name>]`
  table for any platform, and `mise lock` writes the quoted `platforms.<name>` form alone, so the
  gate refuses an entry carrying the nested one. One gap no setting reports:
  `MISE_BACKENDS_<TOOL>` overrides a tool's backend from the environment.
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
  `Directory.Packages.props`. After changing the Cake.Sdk version in `global.json`, the Tomlyn
  version, or any `PackageVersion` naming a package Cake.Sdk depends on, regenerate it with
  `dotnet restore cake.cs --force-evaluate`.

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
certificate; [docs/dev.md](docs/dev.md) shows how.

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
