# Usage

What the picker and `--help` do not carry: the verbs, where the data lives, and what the app
contacts.

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

Each state file gets a lock file beside it, such as `preferences.json.lock`, the first time the app
writes that file or finds it corrupt. A process holds it only while it reads, changes and writes that
file, or sets a corrupt one aside, so two processes never overwrite each other's change. It stays empty,
and a crash frees it the same way.

`preferences.json` and `settings.json` hold choices worth repairing by hand, so one that fails to parse
moves aside as `<file>.<time>-<id>.corrupt`. The newest three copies of each file stay, and older ones
go. Any other state file that fails to parse is deleted, and the app rebuilds it.

Deleting `version-tracking.json` by hand does not start a fresh first run. Its lock file stays, so
versions on offer still wait out the cooldown, while installed versions are recorded as before.

`diagnostics.log` gets one timestamped line for each bookkeeping write a check, the picker or an upgrade
carried on without, such as a stale skip it could not clear. `check` and `list` print the same lines, the
picker shows them in its message bar, and the upgrade window adds them to the package's row. Each new line
drops the lines older than the update log's retention window, then the oldest lines past 1 MiB.

`crash.log` gets each failed picker scan and each error the app caught nowhere else, with its stack trace. It is
bounded the same way: each new entry drops the entries past the retention window, then the oldest past 1 MiB.

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
