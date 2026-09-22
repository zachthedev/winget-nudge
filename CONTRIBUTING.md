# Contributing

[docs/dev.md](docs/dev.md) takes a fresh clone to a running app and a green gate. This file holds
the rules that apply to every change.

## Toolchain

- Windows 11 on x64. The app is WinUI 3 and the installer is an MSI, so neither builds anywhere
  else.
- The .NET SDK that `global.json` names. It also pins Cake.Sdk, which runs the gate.
- [Bun](https://bun.sh), at the version `package.json` names in `packageManager`. It runs
  commitlint, prettier and lefthook.
- [mise](https://mise.jdx.dev), at the version `.github/mise-bootstrap.json` pins. It installs
  actionlint, zizmor and ShellCheck from `mise.toml` and `mise.lock`. Run `mise trust` then
  `mise install` once per clone. The gate names the `winget` command when mise itself is missing or
  at another version.

Run `bun install` once per clone. It installs the Node tooling and runs `lefthook install`, which
writes the git hooks. The commit-msg hook fails with no message on a clone where `bun install` never
ran, because `bunx --no-install` refuses to fetch commitlint.

The committed `.claude/settings.json` pre-approves read-only git commands and nothing else. A branch
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
stops at the first failure and prints a summary table. The table below lists the checks, not the
order they run in.

| Task        | What it checks                                                                               |
| ----------- | -------------------------------------------------------------------------------------------- |
| `format`    | C# formatting, through CSharpier                                                             |
| `prettier`  | Markdown, YAML and JSON formatting                                                           |
| `build`     | Every project in Release and Debug, analyzer warnings as errors, lock files honored          |
| `tests`     | The Core suite                                                                               |
| `installer` | The MSI links, built unsigned whatever `Directory.Signing.props` says                        |
| `policy`    | Release types, `renovate.json` as repository config, and a Renovate note per wixproj package |
| `lockfile`  | Every `mise.toml` pin recorded in `mise.lock` at the address `cake.cs` names                 |
| `workflows` | actionlint with ShellCheck, then zizmor, over `.github`, at the versions `mise.lock` records |

`--target=<task>` runs one task and the tasks it depends on. `--target=code` runs everything but
`workflows`. `lockfile` reads two data files and starts no process, so it needs no mise on the
machine, and it is the first task the whole gate runs. `--target=tools` runs `lockfile` and then
`mise install`; it is the install continuous integration runs, and `check` does not reach it.

`workflows` hands actionlint the ShellCheck binary it resolved, then asks actionlint for a finding
only ShellCheck reports. actionlint leaves its shell checks off when that binary cannot start, and
still exits 0. A clean actionlint run counts for nothing until that finding comes back.

The pre-push hook runs the whole gate. Continuous integration runs the same tasks across the jobs
`.github/workflows/ci.yml` defines:

- `gate` runs `dotnet cake.cs --target=tools`, then `dotnet cake.cs --target=code`, on Windows.
  The linters install on this leg even though `code` reaches none, so a lockfile whose windows-x64
  entries cannot install fails here rather than on a contributor's machine.
- `workflows` runs the same `tools` target on Linux and runs actionlint and zizmor from what it
  installed. Here zizmor also runs its online audits, which need a token a local run does not have.

`tools` depends on `lockfile` and then runs `mise install`, so on both legs the lockfile is asserted
before anything installs from it. An address in `mise.lock` is what an install fetches, and an entry
naming a repository other than the one `cake.cs` records is refused before anything downloads from
it. The order is a dependency in `cake.cs`, so no arrangement of workflow steps can install first.
`check` does not reach `tools`: a local gate resolves linters an earlier `mise install` put on disk
and makes no network request.

`tools` installs with `MISE_LOCKED_VERIFY_PROVENANCE=1` on a cold cache, so every pull request
re-verifies the attestations against the artifacts `mise.lock` records on both platforms rather than
trusting the run that wrote them. `mise.toml` sets the same value, so a local `mise install`
re-verifies too. Neither leg caches: `jdx/mise-action` saves a cache only when it installs, and both
jobs install with `mise` itself afterwards.

- `commits` checks every commit in a pull request, and its title, with commitlint.

`.github/workflows/codeql.yml` runs CodeQL code scanning on the same events, plus a weekly schedule.
It is advanced setup, a committed workflow, rather than the default setup a repository setting turns
on and leaves nothing in the tree for. Two jobs report, and the branch ruleset on `main` requires
them by the check names below, beside the checks the `ci` workflow reports:

- `Analyze (csharp)` runs on Windows with `build-mode: none`, which extracts every C# source without
  building the solution. Code a build generates is outside the database, which here is the XAML
  compiler's partial classes.
- `Analyze (actions)` runs on Linux over the workflows in `.github`. It overlaps the `workflows` job
  without replacing it. actionlint and zizmor read a workflow's own configuration, such as an
  unpinned action or a permission wider than a job asks for. CodeQL's Actions queries follow
  attacker-controlled data from an event payload into a `run:` block, an action input or an
  artifact. Neither reports the other's findings, so dropping one leaves a gap.

A task that fails is reporting something. Never disable an analyzer, suppress a finding or delete an
assertion to make it pass without saying why in the same change.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org), enforced by the commit-msg hook and by
the `commits` job. The header and every body line stay within 72 characters.
`.github/commit-scopes.json` lists each scope and what it covers, and commitlint accepts no other.
Omit the scope rather than invent one. A new top-level area earns a scope in that file, in the
change that adds the area.

The commit message is where the history of a change goes: what was wrong, what the change does, and
why an approach was rejected. Code comments describe the code as it is now.

A pull request merges by squash, merge or rebase. A squash of several commits takes the pull
request's title as its subject, which is why the title is held to the same rules.

The ruleset on `main` requires one approving review from a code owner, and `CODEOWNERS` names the
owner alone. GitHub does not count an author's approval of their own pull request, so `gh pr merge`
on the owner's pull request is refused with `the base branch policy prohibits the merge`.
`gh pr merge --admin` is the way through: it merges on the owner's bypass of the ruleset rather than
on a review. Wait for green checks before running it, because a bypass enforces nothing.

## Where code goes

- `src/WingetNudge.Core`: everything that is not UI. Winget access, tracking and the cooldown gate,
  preferences, manual tools, release notes, the upgrade engine, registration.
- `src/WingetNudge`: the WinUI 3 app, its windows, the notification and the command-line verbs.
- `tests/WingetNudge.Core.Tests`: the xUnit v3 suite over Core.
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
- Restore audits every package, transitive ones included, against nuget.org's advisory database. A
  high or critical advisory, `NU1903` or `NU1904`, fails the build. A low or moderate one, `NU1901`
  or `NU1902`, warns. A database restore could not reach, `NU1900` or `NU1905`, warns too, so an
  outage on nuget.org builds. `Directory.Build.props` records why the two severities stay errors and
  the condition that reverses it. Exempt is not ignored: the `advisories` job in
  `.github/workflows/ci.yml` runs on the weekly schedule and lists every advisory against the locked
  graph, whatever its severity, in the run's summary.
- On a pull request, the `dependency-review` job in `.github/workflows/ci.yml` diffs GitHub's
  dependency graph between base and head. It fails on a high or critical advisory against a package
  the pull request adds or moves, and passes a package it leaves alone. The graph reads
  `package.json` and the workflows. For NuGet it reads each `.csproj`, where Central Package
  Management leaves no version, so a bump in `Directory.Packages.props` is invisible to it. The
  restore audit is the NuGet gate.
- [Renovate](https://docs.renovatebot.com) proposes updates on Monday mornings, one grouped pull
  request per ecosystem, and never for a version younger than three days.
  `.github/workflows/dependency-updates.yml` runs it under a GitHub App, and `.github/renovate.json`
  decides what it proposes.
- That workflow runs the Renovate image from ghcr.io, and `.github/renovate.json` resolves its
  version and digest on Docker Hub, which carries the release timestamp the cooldown needs. A step
  before Renovate asks both registries for the digest of the pinned tag and fails the run when they
  disagree, so the pin Renovate writes from one registry is the image the other serves. A registry
  that will not answer after three attempts is a warning instead: the step runs ahead of Renovate,
  and Renovate is what raises a security fix, so an anonymous pull token's rate limit must not be
  what stops one shipping.
- A security fix skips both the schedule and the wait. `bunfig.toml` still holds the wait for Bun's
  own resolution, so a security bump can leave the pull request red with
  `blocked by minimum-release-age`. Audit the version, then run
  `bun install --minimum-release-age 0` once and commit the lock file.
- `WinGetVersion` and `WinGetModuleSha256` in `Directory.Packages.props` move together, by hand. The
  first is the winget release the COM projection comes from. The second is the SHA-256 of the
  `Microsoft.WinGet.Client` package of the same version on the PowerShell Gallery, the only source
  of `winrtact.dll`. Renovate holds the projection for that reason.
- The workflow linters are pinned in `mise.toml`, and `mise.lock` records the artifact each version
  resolved to. Both legs install from those two files, so no pin is asserted against a copy of
  itself. Renovate rewrites both in one pull request by running `mise lock`. The pins sit in a data
  file rather than in `cake.cs`, because a formatter moves source and a pin that moves is a pin no
  tool can read. Never hand-edit `mise.lock`; write it with
  `mise lock --platform linux-x64,windows-x64`.
- mise verifies a GitHub build attestation for actionlint and zizmor, because aqua's registry
  declares a signer workflow for each. It verifies none for ShellCheck. `koalaman/shellcheck`
  declares neither a signer workflow nor a checksums file at any version constraint, so ShellCheck's
  integrity here is the recorded hash alone. The gate asserts a checksum for all three and
  provenance for the two that carry one.
- The gate also asserts the `backend`, `url` and `url_api` of every entry against the aqua
  repository `cake.cs` names for that tool. Those three are what an install fetches, so a provenance
  line beside an address somewhere else would be a claim about bytes nobody downloads. The expected
  owner lives in `cake.cs` rather than in `mise.lock`, so moving an install takes an edit to both.
  `url` also has to carry the pinned version, which keeps an entry from naming an older release of
  the right repository.
- `mise.toml` sets `locked_verify_provenance`, so an install re-verifies each attestation rather
  than trusting the lockfile's recorded one, and `[tool_config] locked = true`, which mise enforces
  whatever `locked_scopes` says. The gate reads both, plus `locked_scopes` itself, so a
  `MISE_LOCKED_SCOPES` that drops `project` is reported rather than left to outrank the file in
  silence.
- ShellCheck is a pinned dependency of this repository on both legs. The `rhysd/actionlint` image
  bundles a ShellCheck copied out of `koalaman/shellcheck-alpine:stable` when that image is built,
  so a run through the image has no pin on the ShellCheck it executes. One `mise.toml` entry drives
  the binary both legs run.
- mise itself is pinned in `.github/mise-bootstrap.json`, with the SHA-256 of its binary on each
  platform. `jdx/mise-action` checks its download against the release's minisign-signed
  `SHASUMS256.txt`, and a step after it checks the installed binary against the recorded hash, so
  two independent checks cover the tool that verifies the linters. The gate hashes the `mise` it
  resolved against the same entry, so a local run identifies mise by its bytes rather than by the
  version mise prints about itself. Renovate moves the version and cannot compute those hashes, so
  its pull request carries a note and stays red until they move with it.
- `cake.cs` restores in locked mode against `cake.packages.lock.json`. After changing the Cake.Sdk
  version in `global.json`, regenerate it with `dotnet restore cake.cs --force-evaluate`.

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

Merging that pull request tags the commit and creates the GitHub release as a draft. The `release`
workflow then builds the MSI, checks that its version matches the tag, and waits for a maintainer to
approve the `release` environment. Only then does it attach the MSI, its `SHA256SUMS` and a build
provenance attestation, and publish the draft, so a visitor never reaches a release with nothing on
it. Running that workflow by hand with a tag rebuilds and reattaches the assets for an existing
release.

A high or critical NuGet advisory fails the release build as it fails every other, so a merged
release pull request can leave a draft with no assets. The way out is a version without the
advisory. Renovate's `security` group opens that bump without waiting for the schedule, and merging
it cuts the next release. Running the `release` workflow by hand with the tag rebuilds the draft
once the advisory is withdrawn.

Shipping through an advisory is a decision, and its record lives beside the exception.
`Directory.Build.props` takes a `NuGetAuditSuppress` item whose `Include` is the advisory URL. The
comment on that item carries what the advisory blocks, why shipping is safer than waiting, and the
condition that removes the item. Commit it as `build`, so release-please cuts the release, and
delete it in the change that meets the condition. The weekly `advisories` job lists a suppressed
advisory all the same, so the exception stays visible for as long as it lasts.

`AuditPipeline=false` on a `dotnet restore` or `dotnet build` turns `NU1903` and `NU1904` back into
warnings for that one invocation. The weekly report restores with it, and a maintainer can build
with it to read what a blocked build would produce. No workflow passes it to a release build, and it
is legitimate only beside the record above.

A command line is the only place it belongs. MSBuild reads an environment variable as a property, so
an exported `AuditPipeline` reaches every build with nothing on a command line to see, and an
exported `WarningsNotAsErrors` names an advisory to exempt without mentioning `AuditPipeline` at
all. `cake.cs` refuses to run any target while either name is set in the environment, and
`Directory.Build.props` assigns `WarningsNotAsErrors` outright rather than appending to what it
inherits, so nothing it inherits reaches the list.

Never edit the version in `Directory.Build.props` or `CHANGELOG.md` by hand. release-please owns
both.

Release MSIs are unsigned for now. A local build signs when `Directory.Signing.props` names a
certificate; [docs/dev.md](docs/dev.md) shows how.
