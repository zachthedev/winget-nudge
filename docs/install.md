# Install

## Requirements

Windows 11 on x64, winget 1.29 or newer, and the Windows App Runtime 2.5.1 or a newer 2.x. Recent App
Installer and WinUI app updates put the runtime on most machines; otherwise it comes from Microsoft's
[Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Setup checks the Windows build and the runtime registered for your account before it changes
anything. Below Windows 11, setup stops with a message naming the build it needs. Without the
runtime, setup stops with a message naming the version and the downloads page. A silent install,
such as `msiexec /quiet` or winget, shows no message and ends with exit code 1603. Its log carries
the message when it writes one (`msiexec /l*v <file>`). If setup refuses while the runtime is
present, `msiexec /i WingetNudge-<version>-x64.msi WINDOWSAPPRUNTIMEFOUND=1` skips the runtime
check. Nothing skips the Windows check.

## Install

Download `WingetNudge-<version>-x64.msi` from the
[latest release](https://github.com/zachthedev/winget-nudge/releases/latest) and run it. It installs
for your account only, with no administrator prompt, into `%LOCALAPPDATA%\Programs\Winget Nudge`. It
adds a Start menu shortcut and an entry in Apps & features, and registers the scheduled checks and
the notification before setup finishes. If either registration fails, setup undoes everything it
did, the other registration included.

Release MSIs are not code-signed yet
([issue 1](https://github.com/zachthedev/winget-nudge/issues/1)), so SmartScreen may stop the first
run: choose **More info**, then **Run anyway**. Check the download first.

## Check the download

Every release MSI has a build provenance attestation. The shared `publish.yml` workflow in
[zachthedev/.github](https://github.com/zachthedev/.github) signs it, so the command names that
workflow as the signer:

```powershell
gh attestation verify WingetNudge-<version>-x64.msi --repo zachthedev/winget-nudge --signer-workflow zachthedev/.github/.github/workflows/publish.yml
```

A pass proves the source repository and the signer workflow. This command does not prove the tag.
The next one does. The release run starts from the push to `main`, so the attestation names
`refs/heads/main` and no tag, and `--source-ref refs/tags/v<version>` fails on a genuine MSI.

The attestation also records the commit the run built. Look up the commit the tag names, then
require it with `--source-digest`:

```powershell
$commit = gh api repos/zachthedev/winget-nudge/commits/v<version> --jq .sha
gh attestation verify WingetNudge-<version>-x64.msi --repo zachthedev/winget-nudge --signer-workflow zachthedev/.github/.github/workflows/publish.yml --source-digest $commit
```

A pass also proves the MSI was built from the commit the tag names. `publish.yml` refuses a tag that
does not name the run's commit, so a genuine release attests the tagged commit.

Each release also carries `SHA256SUMS`. Download it from the MSI's own tag, because an older MSI
fails against a newer release's list:

```powershell
Invoke-WebRequest https://github.com/zachthedev/winget-nudge/releases/download/v<version>/SHA256SUMS -OutFile SHA256SUMS
(Get-FileHash WingetNudge-<version>-x64.msi).Hash -eq (Get-Content SHA256SUMS).Split(' ')[0]
```

`True` proves integrity: the MSI matches the list its release carries. The list proves nothing about
origin, because whoever can replace the MSI on a release can replace the list beside it. The
attestation proves origin.

## Upgrade

A newer MSI replaces the installed one. The MSI and the executable carry the numeric version alone,
so a prerelease is no upgrade path over the release it follows.

An upgrade that needs a newer Windows App Runtime stops the same way before it changes anything,
and the installed version keeps working. An upgrade whose registration fails undoes itself and puts
the installed version back.

## Uninstall

Uninstall from Apps & features. It removes the scheduled checks, the notification registration and
everything setup installed. Settings and history stay in `%LOCALAPPDATA%\WingetNudge`; delete that
folder to remove them too.
