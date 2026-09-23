# Winget Nudge

A `csharp-installer` repository. [README.md](README.md) says what the app is.

## Read first

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/dev.md](docs/dev.md) before changing anything. They
bind an agent as they bind a person.

## Verify

- `dotnet cake.cs` runs the whole gate.
- `dotnet cake.cs --target=code` runs everything but the workflow linters.
- `dotnet cake.cs --description` lists every task and what it checks.
- `dotnet cake.cs --target=<task>` runs one task and the tasks it depends on.

[CONTRIBUTING.md#the-gate](CONTRIBUTING.md#the-gate) says what the rows cover.

## Never

- Never run `register`, `unregister`, `upgrade`, `update-all` or `check` from a build output unless
  the user asks. They change this machine's scheduled tasks, notification registration and installed
  packages, and the real scheduled tasks here run the installed app.
- Never edit the version in `Directory.Build.props` or `CHANGELOG.md` by hand
  ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never write a `mise.lock` line outside `mise lock`, except a checksum computed as `mise.toml`
  says ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never disable an analyzer, suppress a finding or delete an assertion to make the gate pass
  ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never invent a commit scope ([what never happens](CONTRIBUTING.md#what-never-happens)).

## Deviations

A comment beside a line that names the handbook records a deliberate deviation. It is a decision, not
a defect.

## Where the rest is

[README.md#documentation](README.md#documentation).
