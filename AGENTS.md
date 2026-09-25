# Winget Nudge

A `csharp-installer` repository. [README.md](README.md) says what the app is.

## Read first

Read these before changing anything, in order. They bind an agent as they bind a person.

1. [README.md](README.md)
2. [CONTRIBUTING.md](CONTRIBUTING.md), whole
3. [SECURITY.md](SECURITY.md)
4. [docs/install.md](docs/install.md)
5. [docs/usage.md](docs/usage.md)

## Verify

- `dotnet cake.cs` runs the whole gate.
- `dotnet cake.cs --target=code` runs everything but the workflow linters.
- `dotnet cake.cs --description` lists every task and what it checks.
- `dotnet cake.cs --target=<task>` runs one task and the tasks it depends on.

[CONTRIBUTING.md#the-gate](CONTRIBUTING.md#the-gate) says what the rows cover.

## Never

- Never run `register`, `unregister`, `upgrade`, `update-all` or `check` from a build output unless
  the user asks. They change the scheduled tasks, notification registration and installed packages of
  the computer they run on, and a development computer's own scheduled tasks may run an installed
  copy of the app.
- Never edit the version in `Directory.Build.props` or `CHANGELOG.md` by hand
  ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never write a `mise.lock` line outside `mise lock`, except a checksum computed as `mise.toml`
  says ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never disable an analyzer, suppress a finding or delete an assertion to make the gate pass
  ([what never happens](CONTRIBUTING.md#what-never-happens)).
- Never invent a commit scope ([what never happens](CONTRIBUTING.md#what-never-happens)).

## Deviations

A comment beside a deviating line records a deliberate deviation. It is a decision, not a defect.

## Where the rest is

[README.md#documentation](README.md#documentation).
