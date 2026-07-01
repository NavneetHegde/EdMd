<#
.SYNOPSIS
  Builds (and optionally signs) an MSIX package for EdMd (EdMd).

.DESCRIPTION
  Publishes the WPF app self-contained, stages it with the MSIX manifest + assets,
  and packs it into a .msix using the Windows SDK's MakeAppx.exe.

  The package Identity/Publisher MUST match the Subject of the signing certificate or
  Windows refuses to install it. This script therefore derives the Publisher from the
  cert and injects it into the staged manifest automatically.

.PARAMETER PfxPath
  Path to a real code-signing certificate (.pfx). Production path: the resulting .msix
  installs on any machine that trusts the cert's chain — no "trust the test cert" step.

.PARAMETER PfxPassword
  Password for the .pfx (SecureString-safe plain string).

.PARAMETER Publisher
  Override the package Publisher (e.g. "CN=Contoso, O=Contoso, C=US"). Normally derived
  from the signing cert; set this to match Partner Center for Store submissions.

.PARAMETER Sign
  Dev-only: sign with a throwaway self-signed cert (CN=EdMd Dev) and export its .cer so
  it can be trusted locally. Ignored when -PfxPath is supplied.

.EXAMPLE
  # Production build signed with a real cert:
  ./build.ps1 -PfxPath C:\certs\edmd.pfx -PfxPassword 'hunter2' -Version 1.0.0.0

.EXAMPLE
  # Local dev smoke test (self-signed):
  ./build.ps1 -Sign

.NOTES
  Requires: .NET SDK, and the Windows 10/11 SDK (MakeAppx.exe + SignTool.exe).
#>
[CmdletBinding()]
param(
  [string]$Version = "1.0.0.0",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$PfxPath,
  [string]$PfxPassword,
  [string]$Publisher,
  [string]$TimestampUrl = "http://timestamp.digicert.com",
  [switch]$Sign
)

$ErrorActionPreference = "Stop"
$here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj   = [System.IO.Path]::GetFullPath((Join-Path $here "..\EdMd\EdMd.csproj"))
$stageDir  = Join-Path $here "obj\stage"
$outDir    = Join-Path $here "Output"
$manifest  = Join-Path $here "Package.appxmanifest"
$assetsDir = Join-Path $here "Assets"
$msixPath  = Join-Path $outDir "EdMd-$Version.msix"
$devSubject = "CN=EdMd Dev"   # self-signed dev fallback identity

# Locate an SDK tool (makeappx/signtool). Prefer stable SDKs over Insider/preview
# builds, whose MakeAppx has a known bug rejecting valid Windows.FullTrustApplication
# manifests. Preview SDK build numbers (>= 27000) are de-prioritised.
function Find-SdkTool([string]$name) {
  $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
  $candidates = Get-ChildItem -Path $roots -Filter $name -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\x64\\" }
  if (-not $candidates) { throw "$name not found. Install the Windows 10/11 SDK." }
  $ranked = $candidates | ForEach-Object {
    $ver = if ($_.FullName -match "\\10\\bin\\(\d+\.\d+\.(\d+)\.\d+)\\") { [int]$Matches[2] } else { 0 }
    [pscustomobject]@{ Path = $_.FullName; Build = $ver; Stable = ($ver -lt 27000) }
  }
  ($ranked | Sort-Object Stable, Build -Descending | Select-Object -First 1).Path
}

# --- Determine the Publisher (must equal signing cert Subject) ------------------------
if (-not $Publisher) {
  if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $pfxCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
      $PfxPath, $PfxPassword)
    $Publisher = $pfxCert.Subject
    Write-Host "==> Publisher derived from PFX: $Publisher" -ForegroundColor Cyan
  } elseif ($Sign) {
    $Publisher = $devSubject
  }
}

# --- Publish --------------------------------------------------------------------------
Write-Host "==> Publishing $Configuration / $Runtime (self-contained)..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
dotnet publish $appProj -c $Configuration -r $Runtime --self-contained true `
  -p:PublishSingleFile=false -p:Version=$Version -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# --- Stage manifest (inject Version + Publisher) + assets -----------------------------
Write-Host "==> Staging manifest + assets..." -ForegroundColor Cyan
$xml = Get-Content $manifest -Raw
# Case-sensitive (-creplace) so we touch only the Identity's capital-V "Version",
# never the lowercase "version" in the <?xml ...?> declaration.
$xml = $xml -creplace 'Version="[0-9.]+"', ('Version="' + $Version + '"')
if ($Publisher) {
  # Escape $ in the replacement so cert subjects can't be misread as regex groups.
  $pubRepl = 'Publisher="' + ($Publisher -replace '\$', '$$$$') + '"'
  $xml = $xml -replace 'Publisher="[^"]*"', $pubRepl
}
Set-Content (Join-Path $stageDir "AppxManifest.xml") -Value $xml -Encoding UTF8
Copy-Item $assetsDir (Join-Path $stageDir "Assets") -Recurse -Force

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$makeappx = Find-SdkTool "makeappx.exe"

# --- Pack (retry once with /nv if semantic validation trips on full-trust) ------------
Write-Host "==> Packing $msixPath ..." -ForegroundColor Cyan
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
& $makeappx pack /o /d $stageDir /p $msixPath
if ($LASTEXITCODE -ne 0) {
  Write-Warning "MakeAppx validation failed; retrying with /nv (install-time validation still applies)."
  & $makeappx pack /nv /o /d $stageDir /p $msixPath
  if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed." }
}

# --- Sign -----------------------------------------------------------------------------
$signtool = $null
if ($PfxPath) {
  Write-Host "==> Signing with $PfxPath ..." -ForegroundColor Cyan
  $signtool = Find-SdkTool "signtool.exe"
  & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $TimestampUrl /td SHA256 $msixPath
  if ($LASTEXITCODE -ne 0) { throw "SignTool failed." }
}
elseif ($Sign) {
  Write-Host "==> Signing with self-signed test cert ($devSubject)..." -ForegroundColor Cyan
  # Ensure the Cert:\ drive exists. It's present by default; importing the module when
  # it's already loaded can throw a terminating FormatXmlUpdateException, so only import
  # (best-effort) when the drive is actually missing.
  if (-not (Test-Path "Cert:\")) {
    try { Import-Module Microsoft.PowerShell.Security -ErrorAction Stop } catch { }
  }
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $devSubject } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $devSubject `
      -KeyUsage DigitalSignature -FriendlyName "EdMd Dev" `
      -CertStoreLocation "Cert:\CurrentUser\My" `
      -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Write-Host "    Created new self-signed certificate."
  }
  $cerPath = Join-Path $outDir "EdMd-Dev.cer"
  Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
  $signtool = Find-SdkTool "signtool.exe"
  & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath
  if ($LASTEXITCODE -ne 0) { throw "SignTool failed." }
  Write-Host ""
  Write-Host "Signed (self-signed). To trust it locally (one-time, elevated PowerShell):" -ForegroundColor Yellow
  Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}

Write-Host ""
Write-Host "Done: $msixPath" -ForegroundColor Green
if (-not $PfxPath -and -not $Sign) {
  Write-Host "NOTE: unsigned .msix cannot be installed. Re-run with -PfxPath (prod) or -Sign (dev)." -ForegroundColor Yellow
}
