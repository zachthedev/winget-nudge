# Winget Nudge

[![ci](https://github.com/zachthedev/winget-nudge/actions/workflows/ci.yml/badge.svg)](https://github.com/zachthedev/winget-nudge/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/zachthedev/winget-nudge)](https://github.com/zachthedev/winget-nudge/releases/latest)
[![license](https://img.shields.io/github/license/zachthedev/winget-nudge)](LICENSE)

A Windows 11 app that checks winget for package upgrades on a schedule, nudges you with a
notification, and upgrades what you pick in a native progress window.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/picker-dark.png">
  <img alt="The picker, listing upgrades ready to install, tools installed outside winget, and versions still too new" src="docs/images/picker-light.png">
</picture>

It drives winget through its COM API, so there is no PowerShell module, no terminal window, and no
host that breaks when PowerShell or Windows Terminal upgrade themselves.

## What it does

- A scheduled check runs weekly and after sign-in. When upgrades exist, a notification lists them
  with one button: **Review updates**.
- A quiet check runs every four hours between those. It refreshes release dates and the cooldown, so
  a version that matured overnight is ready when the picker opens. It stays silent unless **Notify
  me as soon as an update appears** is on, and then it announces each new version once.
- The picker lists every upgrade with its versions, how long the new version has been public, and a
  link to its release notes. Sections separate what is ready, tools installed outside winget, and
  what is too new, skipped, muted, or failed last time. Each section's header has a checkbox that
  selects or clears its rows.
- **Too new** counts from when the version landed in `microsoft/winget-pkgs`, not from when this
  machine first saw it. The default wait is 24 hours, so a version published earlier in the week is
  offered right away.
- Per package, you can mute it, or skip just this version until the next one appears.
- **Update selected** opens one elevated window that upgrades the packages in order. Each upgrade
  runs silently first. Only when winget reports a locked file does it find the apps holding it
  through the Restart Manager and ask before closing them. It then retries silently, interactively,
  with force, and without elevation for installers that refuse it, and restarts the apps it closed.
- Tools installed outside winget, such as bun or uv, can be registered with a version command and a
  JSON endpoint. The picker lists them beside the packages. **Update selected** runs each checked
  tool's upgrade command as you, in a PowerShell window of its own, and a row's play button runs
  that one tool straight away.

## Install

You need Windows 11 on x64, winget 1.29 or newer, and the Windows App Runtime 2.4. Recent App
Installer and WinUI app updates put the runtime on most machines; otherwise it comes from
Microsoft's
[Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Download `WingetNudge-<version>-x64.msi` from the
[latest release](https://github.com/zachthedev/winget-nudge/releases/latest) and run it. It installs
for your account only, with no administrator prompt, into `%LOCALAPPDATA%\Programs\Winget Nudge`. It
adds a Start menu shortcut and an entry in Apps & features, and registers the scheduled checks and
the notification before setup finishes. A newer MSI replaces the installed one.

Release MSIs are not code-signed yet
([issue 1](https://github.com/zachthedev/winget-nudge/issues/1)), so SmartScreen may stop the first
run: choose **More info**, then **Run anyway**. Check the download first. Each release carries a
build provenance attestation that proves the MSI came from this repository's release workflow:

```powershell
gh attestation verify WingetNudge-1.0.0-x64.msi --repo zachthedev/winget-nudge --source-ref refs/tags/v1.0.0
```

Or compare `(Get-FileHash WingetNudge-1.0.0-x64.msi).Hash` with the release's `SHA256SUMS`.

Uninstall from Apps & features. It removes the scheduled checks, the notification registration and
everything setup installed. Settings and history stay in `%LOCALAPPDATA%\WingetNudge`; delete that
folder to remove them too.

## Command line

| Verb                                    | Does                                                                                      |
| --------------------------------------- | ----------------------------------------------------------------------------------------- |
| _(none)_ or `picker`                    | Open the picker.                                                                          |
| `check`                                 | Query winget and show the notification when upgrades exist. The scheduled task runs this. |
| `check --background`                    | The quiet interval check. Notifies only for unannounced versions, and only when enabled.  |
| `update-all`                            | Upgrade every eligible package in the elevated window.                                    |
| `upgrade --id X [--name N] ...`         | Upgrade specific packages. Relaunches itself elevated.                                    |
| `register` / `unregister`               | Both scheduled tasks, the shortcut, and the notification registration.                    |
| `tool list` / `add` / `remove` / `test` | Manage tools tracked outside winget.                                                      |
| `tracking init`                         | Seed version tracking with every installed package. `check` does this on first run too.   |

Registering a tool:

```powershell
WingetNudge.exe tool add bun --name Bun --current bun --version --current-regex '^(\d+\.\d+\.\d+)$' `
  --latest-url https://api.github.com/repos/oven-sh/bun/releases/latest --latest-field tag_name `
  --latest-regex '^bun-v(.+)$' --upgrade 'bun upgrade'
```

`WingetNudge.exe` is a GUI process, so a verb's output reaches the terminal only when that terminal
launched it directly.

## Settings and data

Everything lives in `%LOCALAPPDATA%\WingetNudge`. **Settings** in the picker edits `settings.json`:
the hours to wait after a release, the weekly check's day and hour, whether to check after sign-in,
the quiet check's interval, and whether a quiet check may notify. Saving re-registers both scheduled
tasks.

Release notes are cached per version in `changelog-cache.json`, and `notification-state.json`
records what the last notification named. Startup deletes half-written state files a killed process
left behind.

`check.lock` is held while a scheduled or manual check runs, so a second check stands down.
`upgrade.lock` is held for the duration of an upgrade, so a second upgrade shows "already running"
and installs nothing. A crash frees both, because Windows closes a dead process's handles, so a
stale lock file needs no manual cleanup.

On first run, the app copies state from the older PowerShell version's
`%LOCALAPPDATA%\WingetUpdater` folder if it exists, so muted packages, tracking history and
registered tools carry over.

## Privacy

Winget Nudge has no telemetry and no account. Beyond what winget itself contacts, it makes these
requests:

- `api.github.com` and `raw.githubusercontent.com`, for release dates from `microsoft/winget-pkgs`
  and release notes from each package's GitHub releases.
- Each registered tool's latest-version URL.

GitHub allows 60 unauthenticated API requests an hour. A token in **Settings**, stored encrypted to
your account, or in the `GITHUB_TOKEN` environment variable lifts that limit. The app sends the
token to `api.github.com` and nowhere else.

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md) has the rules, and [docs/dev.md](docs/dev.md) takes a fresh clone
to a running app. Report security problems privately, as [SECURITY.md](SECURITY.md) describes.

Winget Nudge is not affiliated with or endorsed by Microsoft.

## License

[MIT](LICENSE)
