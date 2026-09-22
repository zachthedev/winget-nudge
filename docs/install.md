# Install

## Requirements

Windows 11 on x64, winget 1.29 or newer, and the Windows App Runtime 2.4. Recent App Installer and
WinUI app updates put the runtime on most machines; otherwise it comes from Microsoft's
[Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

## Install

Download `WingetNudge-<version>-x64.msi` from the
[latest release](https://github.com/zachthedev/winget-nudge/releases/latest) and run it. It installs
for your account only, with no administrator prompt, into `%LOCALAPPDATA%\Programs\Winget Nudge`. It
adds a Start menu shortcut and an entry in Apps & features, and registers the scheduled checks and
the notification before setup finishes.

Release MSIs are not code-signed yet
([issue 1](https://github.com/zachthedev/winget-nudge/issues/1)), so SmartScreen may stop the first
run: choose **More info**, then **Run anyway**. Check the download first.

## Check the download

Each release carries a build provenance attestation that proves the MSI came from this repository's
release workflow:

```powershell
gh attestation verify WingetNudge-<version>-x64.msi --repo zachthedev/winget-nudge --source-ref refs/tags/v<version>
```

Or compare `(Get-FileHash WingetNudge-<version>-x64.msi).Hash` with the release's `SHA256SUMS`.

## Upgrade

A newer MSI replaces the installed one. The MSI and the executable carry the numeric version alone,
so a prerelease is no upgrade path over the release it follows.

## Uninstall

Uninstall from Apps & features. It removes the scheduled checks, the notification registration and
everything setup installed. Settings and history stay in `%LOCALAPPDATA%\WingetNudge`; delete that
folder to remove them too.
