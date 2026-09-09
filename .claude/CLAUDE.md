# Claude Code in this repository

Everything a contributor needs is in the human-facing files. This one only points at them.

Read [CONTRIBUTING.md](../CONTRIBUTING.md) and [docs/dev.md](../docs/dev.md) before changing
anything.

## The rules that do not bend

- **The gate is `dotnet cake.cs`.** Run it before calling a change done, and never run its tasks
  separately as a substitute. A tool it cannot run stops it and names the fix.
- **A test never reaches this machine.** Winget, the Restart Manager, Task Scheduler and
  notifications sit behind seams. Pass a substitute for each one a test could touch, whether or not
  the case triggers it today. The real scheduled tasks here run the installed app.
- **Never run `register`, `unregister`, `upgrade`, `update-all` or `check` from a build output**
  unless the user asks. They change this machine's scheduled tasks, notification registration and
  installed packages.
- **Commit scopes come from `.github/commit-scopes.json`.** Omit the scope rather than invent one.
- **release-please owns the version in `Directory.Build.props` and `CHANGELOG.md`.** Never edit
  either by hand.

## The documentation

| File                                  | Holds                                                                |
| ------------------------------------- | -------------------------------------------------------------------- |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | The toolchain, the gate, commits, tests, dependencies and releases   |
| [docs/dev.md](../docs/dev.md)         | The first run, running the app, the MSI and signing, winget releases |
| [README.md](../README.md)             | What the app does, installing it, its command line and its data      |
| [SECURITY.md](../SECURITY.md)         | What counts as a vulnerability, and how to report one                |
