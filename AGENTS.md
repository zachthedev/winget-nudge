# Agents in this repository

Everything a contributor needs is in the human-facing files. This one only points at them.

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/dev.md](docs/dev.md) before changing anything.
They bind an agent as they bind a person, and nothing here repeats them.

One rule is for agents alone. **Never run `register`, `unregister`, `upgrade`, `update-all` or
`check` from a build output** unless the user asks. They change this machine's scheduled tasks,
notification registration and installed packages, and the real scheduled tasks here run the
installed app.

## The documentation

| File                               | Holds                                                                |
| ---------------------------------- | -------------------------------------------------------------------- |
| [CONTRIBUTING.md](CONTRIBUTING.md) | The toolchain, the gate, commits, tests, dependencies and releases   |
| [docs/dev.md](docs/dev.md)         | The first run, running the app, the MSI and signing, winget releases |
| [README.md](README.md)             | What the app does, installing it, its command line and its data      |
| [SECURITY.md](SECURITY.md)         | What counts as a vulnerability, and how to report one                |
