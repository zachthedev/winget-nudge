# Development

From a fresh clone to a running app and a green gate. [CONTRIBUTING.md](../CONTRIBUTING.md) holds
the rules every change follows.

## Prerequisites

Windows 11 on x64, with winget 1.29 or newer. Everything else installs with winget:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id Oven-sh.Bun --exact
winget install --id Microsoft.PowerShell --exact
```

The app also needs the Windows App Runtime 2.4 at run time. Recent App Installer and WinUI app
updates put it on most machines already; otherwise it comes from Microsoft's
[Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

The gate's workflow linters come from [mise](https://mise.jdx.dev) rather than from winget.
`mise.toml` pins the version of each one, `mise.lock` records the artifact that version resolved to,
and continuous integration installs from the same two files. `.github/mise-bootstrap.json` pins mise
itself, and the gate names the exact `winget install` command when mise is missing or at another
version.

```powershell
winget install --id jdx.mise --exact
```

Open a new terminal afterwards, so `PATH` includes what winget added.

## First run

```powershell
git clone https://github.com/zachthedev/winget-nudge.git
Set-Location winget-nudge
bun install
dotnet tool restore
mise trust
mise install
dotnet cake.cs
```

`bun install` also installs the git hooks. `dotnet tool restore` installs CSharpier at the version
`dotnet-tools.json` pins. `mise trust` marks this repository's `mise.toml` as one mise may read, and
`mise install` puts the linters on disk from the artifacts `mise.lock` records. `dotnet cake.cs`
runs the whole gate, which a pre-push hook runs again before anything leaves the machine.

The gate resolves each linter with `mise which` and runs the path it gets back, so a green run makes
no network request. `mise install` is the step that needs one.

The first build downloads the `Microsoft.WinGet.Client` package from the PowerShell Gallery and
checks its hash. It is the only source of `winrtact.dll`, the winget hook that lets an unpackaged
process marshal winget's COM objects.

## Running the app

```powershell
dotnet build src/WingetNudge
$app = 'src/WingetNudge/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WingetNudge.exe'
& $app
& $app check
```

The first opens the picker, and the second runs a verb from the table in the
[README](../README.md#command-line). The executable is a GUI process, so a verb's output reaches the
terminal only when the terminal launched it directly. `dotnet run` starts it through another
process, and the output never arrives.

State lives in `%LOCALAPPDATA%\WingetNudge` for a source build and an installed copy alike. A crash
the app survives is written to `crash.log` there. Deleting a file there resets what it holds.

Running `register` from a build output points both scheduled tasks and the notification registration
at that build. Run `unregister` from the same build before deleting it, or install the MSI again,
which registers the installed copy.

## Screenshots

The README's screenshots come from a fixed inventory that only a Debug build carries:

```powershell
$env:WINGETNUDGE_DEMO = '1'
& $app
```

It lists well-known packages and two tools that are not this machine's, keeps its state in
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
of setup and `unregister` at the start of an uninstall.

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

That signs `WingetNudge.exe`, both app assemblies and the MSI with the Windows SDK's signtool and a
DigiCert timestamp. A self-signed certificate verifies only on a machine that trusts it.

## Winget error codes

`tools/Update-WingetErrorCodes.ps1` regenerates
`src/WingetNudge.Core/Packages/WingetErrorCodes.g.cs` from the installed winget's own table. The
failure text on a package card comes from it. Rerun it when a winget upgrade adds codes:

```powershell
./tools/Update-WingetErrorCodes.ps1
```

## Moving to a new winget release

`WinGetVersion` names the winget release the COM projection comes from, and `WinGetModuleSha256`
pins the matching PowerShell module. Change both in `Directory.Packages.props`, in one commit:

```powershell
$version = '1.29.290'
Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/package/Microsoft.WinGet.Client/$version" -OutFile "$env:TEMP\winget-client.nupkg"
(Get-FileHash -Path "$env:TEMP\winget-client.nupkg" -Algorithm SHA256).Hash
```

Then restore with `dotnet restore --force-evaluate` so the lock files pick up the new projection,
and run the gate.
