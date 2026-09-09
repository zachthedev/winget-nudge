# Security

Winget Nudge runs elevated to upgrade packages, closes other people's apps through the Restart
Manager, registers scheduled tasks, and renders release notes it fetches from the internet. This
file says what counts as a vulnerability in that arrangement, and how to report one without
publishing it first.

## Reporting

Open a private advisory: <https://github.com/zachthedev/winget-nudge/security/advisories/new>

Never open a public issue for a vulnerability. Everything else belongs in the issue tracker.

A report is most useful with the Winget Nudge version, the winget version, the Windows build, and
the smallest reproduction you have. Keep a proof of concept inert: something that writes to standard
output proves the hole is reachable as well as something that acts on it.

## What is supported

The latest release. A fix ships as a new release, not as a patch to an older one.

## In scope

- The elevation boundary. Anything an unelevated process, file or argument can use to steer what the
  elevated upgrade window does.
- Files the app deletes, moves or rewrites. A link or junction that aims one of those operations
  somewhere other than `%LOCALAPPDATA%\WingetNudge`.
- Release notes. Markdown from GitHub and winget manifests renders in WebView2, and any script it
  runs or any navigation it causes is a finding.
- The Restart Manager. Closing an app the prompt did not name, or closing one without asking when an
  upgrade reported a locked file.
- Registration. A scheduled task or notification registration that runs anything other than the
  installed `WingetNudge.exe`.
- Registered tools. Anything other than the user registering a tool that changes the command a tool
  runs.
- The GitHub token, from Settings, where it is encrypted to the current user, or from
  `GITHUB_TOKEN`. The app sends it over HTTPS to `api.github.com` and nowhere else, so any other
  destination is a finding, and so is the token appearing in the clear on disk.
- The release pipeline. A way to publish an MSI built from a commit `main` does not contain, or an
  attestation that vouches for bytes the workflow did not build.

## Out of scope

- winget itself, a package's installer, or what a package contains. Report those to
  [microsoft/winget-cli](https://github.com/microsoft/winget-cli) or to the package's publisher.
- Anything that needs administrator access, or write access to
  `%LOCALAPPDATA%\Programs\Winget Nudge`. Whoever can write there can already replace the
  executable.
- A registered tool's upgrade command doing what its owner registered.
- SmartScreen warning about the MSI. Release MSIs are unsigned for now
  ([issue 1](https://github.com/zachthedev/winget-nudge/issues/1)), and every release carries a
  checksum and a build provenance attestation instead.

## After a report

There is no bounty. A report gets an acknowledgment, a fix, and a credit in the advisory unless you
ask to stay anonymous.
