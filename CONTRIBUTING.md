# Contributing

[docs/dev.md](docs/dev.md) takes a fresh clone to a running app and a green gate. This file holds
the rules that apply to every change.

## Toolchain

- Windows 11 on x64. The app is WinUI 3 and the installer is an MSI, so neither builds anywhere
  else.
- The .NET SDK that `global.json` names. It also pins Cake.Sdk, which runs the gate.
- [Bun](https://bun.sh), at the version `package.json` names in `packageManager`. It runs
  commitlint, prettier and lefthook.
- actionlint, zizmor and ShellCheck, at the versions `.github/gate-tools.json` pins. The gate names
  the `winget` command for any that is missing or at another version.

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

[Cake](https://cakebuild.net) runs the tasks in `cake.cs` in order, stops at the first failure, and
prints a summary table.

| Task        | What it checks                                                            |
| ----------- | ------------------------------------------------------------------------- |
| `format`    | C# formatting, through CSharpier                                          |
| `prettier`  | Markdown, YAML and JSON formatting                                        |
| `build`     | Every project in Release, analyzer warnings as errors, lock files honored |
| `tests`     | The Core suite                                                            |
| `installer` | The MSI links, built unsigned whatever `Directory.Signing.props` says     |
| `policy`    | The three-day cooldown, in `renovate.json` and `bunfig.toml`              |
| `workflows` | actionlint with ShellCheck, then zizmor, over `.github`                   |

`--target=<task>` runs one task and the tasks it depends on. `--target=code` runs everything but
`workflows`.

The pre-push hook runs the whole gate. Continuous integration runs the same tasks in three jobs:

- `gate` runs `dotnet cake.cs --target=code` on Windows.
- `workflows` runs actionlint and zizmor from their official images, pinned by digest in
  `.github/workflows/ci.yml`. Those are the versions `.github/gate-tools.json` pins, and the
  `workflows` task fails when the two disagree. Here zizmor also runs its online audits, which need
  a token a local run does not have.
- `commits` checks every commit in a pull request, and its title, with commitlint.

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
- [Renovate](https://docs.renovatebot.com) proposes updates on Monday mornings, one grouped pull
  request per ecosystem, and never for a version younger than three days.
  `.github/workflows/dependency-updates.yml` runs it under a GitHub App, and `.github/renovate.json`
  decides what it proposes. The `policy` task refuses a config that lowers that wait or exempts a
  dependency the gate does not already allow.
- A security fix skips both the schedule and the wait. `bunfig.toml` still holds the wait for Bun's
  own resolution, so a security bump can leave the pull request red with
  `blocked by minimum-release-age`. Audit the version, then run
  `bun install --minimum-release-age 0` once and commit the lock file.
- `WinGetVersion` and `WinGetModuleSha256` in `Directory.Packages.props` move together, by hand. The
  first is the winget release the COM projection comes from. The second is the SHA-256 of the
  `Microsoft.WinGet.Client` package of the same version on the PowerShell Gallery, the only source
  of `winrtact.dll`. Renovate holds the projection for that reason.
- The workflow linters are pinned in two places: `.github/gate-tools.json` for a local run, and by
  tag and digest in `ci.yml` for the images. The pins sit in a data file rather than in `cake.cs`,
  because a formatter moves source and a pin that moves is a pin no tool can read. Bump both in one
  change, which Renovate does in one pull request because both are the same dependency. The gate
  reads every image reference on a line `ci.yml` executes and refuses one that is not the pinned
  digest, so a pin left behind in a comment fails too.
- `cake.cs` restores in locked mode against `cake.packages.lock.json`. After changing the Cake.Sdk
  version in `global.json`, regenerate it with `dotnet restore cake.cs --force-evaluate`.

## Releases

[release-please](https://github.com/googleapis/release-please) keeps a release pull request open
against `main`, carrying the next version and the changelog it would ship. The commit types decide
the version: a breaking change bumps the major, a `feat` the minor and a `fix` the patch.

Merging that pull request tags the commit and creates the GitHub release as a draft. The `release`
workflow then builds the MSI, checks that its version matches the tag, and waits for a maintainer to
approve the `release` environment. Only then does it attach the MSI, its `SHA256SUMS` and a build
provenance attestation, and publish the draft, so a visitor never reaches a release with nothing on
it. Running that workflow by hand with a tag rebuilds and reattaches the assets for an existing
release.

Never edit the version in `Directory.Build.props` or `CHANGELOG.md` by hand. release-please owns
both.

Release MSIs are unsigned for now. A local build signs when `Directory.Signing.props` names a
certificate; [docs/dev.md](docs/dev.md) shows how.
