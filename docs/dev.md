# Development

From a fresh clone to a running app and a green gate. [CONTRIBUTING.md](../CONTRIBUTING.md) holds
the rules every change follows.

## Prerequisites

Windows 11 on x64, with the winget release [install.md](install.md#requirements) names. The app is
WinUI 3 and the installer is an MSI, so neither builds anywhere else. Everything below installs with
winget; open a new terminal afterwards, so `PATH` includes what it added.

```powershell
winget install --id Oven-sh.Bun --exact
winget install --id Microsoft.PowerShell --exact
winget install --id jdx.mise --exact
```

- The .NET SDK, at the version `global.json` names. [First run](#first-run) installs it, because
  that version comes from the clone. `global.json` also pins Cake.Sdk, which runs the gate.
- [Bun](https://bun.sh), at the version `package.json` names in `packageManager`. It runs
  commitlint, prettier and lefthook.
- PowerShell 7, for the scripts under `tools` and the commands in this document.
- [mise](https://mise.jdx.dev), at any current version. The `gate` job pins the one it runs on its
  `jdx/mise-action` line in `.github/workflows/ci.yml`, and the shared `workflows` job pins its own
  in the reusable workflow it calls. mise installs actionlint, zizmor, ShellCheck and taplo:
  `mise.toml` pins the version of each one, `mise.lock` records the artifact that version resolved
  to, and continuous integration installs from the same two files. The gate names the
  `winget install` command when mise is missing.

The app needs the Windows App Runtime at run time. [install.md](install.md#requirements) names the
version and where it comes from.

## First run

```powershell
git clone https://github.com/zachthedev/winget-nudge.git
Set-Location winget-nudge
$version = (Get-Content -Path global.json -Raw | ConvertFrom-Json).sdk.version
winget install --id Microsoft.DotNet.SDK.10 --version $version --exact
```

The SDK install reads the exact version `global.json` pins. Open a new terminal in the clone
afterwards, so `PATH` includes the SDK. Then run the rest:

```powershell
bun install
dotnet tool restore
mise trust
mise install
dotnet cake.cs
```

`bun install` also installs the git hooks. `dotnet tool restore` installs CSharpier at the version
`dotnet-tools.json` pins. `mise trust` marks this repository's `mise.toml` as one mise may read, and
`mise install` puts the linters and taplo on disk from the artifacts `mise.lock` records.
`dotnet cake.cs` runs the whole gate, which a pre-push hook runs again before anything leaves the
machine.

The gate resolves each mise tool with `mise which` and runs the path it gets back. `mise install` is
the step that needs the network; the gate itself reaches it only for zizmor's online audits, when
`gh auth token` answers.

The first build downloads the `Microsoft.WinGet.Client` package from the PowerShell Gallery and
checks its hash. It is the only source of `winrtact.dll`, the winget hook that lets an unpackaged
process marshal winget's COM objects.

## Running it

```powershell
dotnet build src/WingetNudge
$app = 'src/WingetNudge/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WingetNudge.exe'
& $app
& $app check
```

The first opens the picker, and the second runs a verb from the table in
[usage.md](usage.md#command-line). The executable is a GUI process, so a verb's output reaches the
terminal only when the terminal launched it directly. `dotnet run` starts it through another
process, and the output never arrives.

State lives in `%LOCALAPPDATA%\WingetNudge` for a source build and an installed copy alike. A crash
the app survives is written to `crash.log` there. Deleting a file there resets what it holds.

Running `register` from a build output points both scheduled tasks and the notification registration
at that build. Run `unregister` from the same build before deleting it, or install the MSI again,
which registers the installed copy.

## Generated files

`src/WingetNudge.Core/Packages/WingetErrorCodes.g.cs` comes from the installed winget's own error
table, and the failure text on a package card comes from it. Rerun the script when a winget upgrade
adds codes:

```powershell
./tools/Update-WingetErrorCodes.ps1
```

## Tests that need a real thing

None. Winget, the Restart Manager, Task Scheduler and notifications sit behind seams, and every test
passes a substitute; [CONTRIBUTING.md](../CONTRIBUTING.md#tests) names them.

## Screenshots

The README's screenshots come from `src/WingetNudge/Services/DemoInventory.cs`, a fixed inventory
that only a Debug build carries:

```powershell
$env:WINGETNUDGE_DEMO = '1'
& $app
```

It lists well-known packages and tools that are not this machine's, keeps its state in
`%TEMP%\WingetNudge-demo` rather than the real data directory, answers every web request with a 404,
and shows the default Windows blue rather than this machine's accent. Update selected, the run
button and the log links start nothing while it runs, and saving Settings schedules nothing. Every
verb is refused with exit code 2, because each one acts on this machine rather than on the
inventory: it opens the picker and nothing else.

`docs/images/picker-light.png` and `docs/images/picker-dark.png` are captures of it, one per theme.

## Building the MSI

```powershell
dotnet cake.cs --target=installer
```

The package lands at `installer/bin/Release/WingetNudge.msi`: a per-user install under
`%LOCALAPPDATA%\Programs\Winget Nudge` with no administrator prompt. It runs `register` at the end
of setup and `unregister` at the start of an uninstall. A failed `register` fails setup and rolls it
back, and a failed `unregister` never stops an uninstall. When a first install rolls back, setup runs
`unregister` before it removes the files, so no registration outlives them.

Before a first install changes anything, the custom action in `installer/CustomActions` lists the
Windows App Runtime framework packages registered for the installing user. Setup refuses when none of
them reaches the version the app's bootstrapper requires. Repair, uninstall and an upgrade's removal
of the previous version skip the check. The installer project asks the app project for
that package name and version, which come from the `Microsoft.WindowsAppSDK.Runtime` package it
resolved. A `Microsoft.WindowsAppSDK` bump therefore moves the check with no edit.

Installing the MSI on this machine points its scheduled checks and notification at the build. Windows
Sandbox starts from a clean copy of Windows, so try setup there instead: map `installer/bin/Release`
into it read-only and run `msiexec /i <folder>\WingetNudge.msi /l*v <log>`. Installing a runtime
there from the downloads page tries the other side of the check.

The gate builds it unsigned on every machine. To sign a local build, put a code-signing
certificate's thumbprint from the current user's store in `Directory.Signing.props` at the
repository root, which `.gitignore` keeps out of commits:

```xml
<Project>
  <PropertyGroup>
    <SigningCertificateThumbprint>...</SigningCertificateThumbprint>
  </PropertyGroup>
</Project>
```

Then build the installer directly:

```powershell
dotnet build installer/WingetNudge.Installer.wixproj --configuration Release
```

That signs `WingetNudge.exe`, the app assemblies and the MSI with the Windows SDK's signtool and a
DigiCert timestamp. A self-signed certificate verifies only on a machine that trusts it.

## Moving to a new winget release

`WinGetVersion` names the winget release the COM projection comes from, and `WinGetModuleSha256`
pins the matching PowerShell module. Change both in `Directory.Packages.props`, in one commit. Set
`WinGetVersion` first. The commands below read it back from that file and print the hash
`WinGetModuleSha256` takes:

```powershell
$version = (Select-Xml -Path Directory.Packages.props -XPath '//WinGetVersion').Node.InnerText
Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/package/Microsoft.WinGet.Client/$version" -OutFile "$env:TEMP\winget-client.nupkg"
(Get-FileHash -Path "$env:TEMP\winget-client.nupkg" -Algorithm SHA256).Hash
```

Then restore with `dotnet restore --force-evaluate` so the lock files pick up the new projection,
and run the gate.
