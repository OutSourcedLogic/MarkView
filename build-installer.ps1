# Builds the MarkView installer: dist\MarkView-Setup-<version>.exe
#
#   .\build-installer.ps1
#
# Requires the .NET 9 SDK and Inno Setup 6.7+ (winget install JRSoftware.InnoSetup).
# Extra arguments are passed to the Inno Setup compiler. For example, /DPerUserTest builds an
# installer that also accepts /CURRENTUSER, for testing without administrator rights.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 1. Publish the app self-contained, so end users don't need .NET installed.
#    It goes to %TEMP%\MarkView.Build\publish, like the rest of the build output (see Directory.Build.props).
$publishDir = "$env:TEMP\MarkView.Build\publish"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish MarkView.csproj -c Release -r win-x64 --self-contained -p:DebugType=None -o $publishDir -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. WebView2 Evergreen Bootstrapper (Setup runs it only on PCs without the runtime).
$bootstrapper = 'installer\redist\MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $bootstrapper)) {
    New-Item -ItemType Directory -Force (Split-Path $bootstrapper) | Out-Null
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper -UseBasicParsing
}
$signature = Get-AuthenticodeSignature $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item $bootstrapper
    throw "WebView2 bootstrapper signature check failed: $($signature.Status) $($signature.SignerCertificate.Subject)"
}

# 3. Wizard images (only if missing; rerun installer\make-wizard-images.ps1 after an icon change).
if (-not (Test-Path 'installer\assets\wizard-large-100.png')) { & installer\make-wizard-images.ps1 | Out-Null }

# 4. Compile the installer.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DPublishDir=$publishDir" @args installer\MarkView.iss
if ($LASTEXITCODE) { throw "Inno Setup compiler failed ($LASTEXITCODE)" }

Get-ChildItem dist\*.exe | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { "Built $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
