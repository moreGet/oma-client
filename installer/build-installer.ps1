<#
.SYNOPSIS
    Builds the Windows installer: publish -> sign binaries -> compile -> sign installer.

.DESCRIPTION
    Order matters and is the whole point of this script. Signing rewrites a file,
    so our own binaries must be signed BEFORE Inno Setup packages them, and the
    installer itself can only be signed AFTER it is compiled. Getting this
    backwards produces an installer that looks signed but ships unsigned payloads.

    The app is published self-contained: OhMyAgent.AiAgent.Client is a
    framework-dependent WPF app by default, and on a machine without the .NET
    Desktop Runtime it dies with a "You must install or update .NET" dialog
    before any of our code runs. Bundling the runtime removes that dependency,
    at the cost of installer size.

    Signing is optional. Without -CertThumbprint the script still produces a
    working installer, and says plainly that it is unsigned. This lets the whole
    pipeline be exercised before a corporate code-signing certificate exists.

    ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI.

.PARAMETER Configuration
    MSBuild configuration. Release by default.

.PARAMETER Runtime
    Publish RID. win-x64 by default.

.PARAMETER CertThumbprint
    SHA-1 thumbprint of a code-signing certificate in Cert:\CurrentUser\My.
    Omit to skip signing entirely.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. A timestamp records that the signature was made
    while the certificate was valid, so signed builds stay valid after the
    certificate expires. On an isolated network, pass an internal TSA instead.

.PARAMETER SkipPublish
    Reuse an existing publish folder. Useful when iterating on the .iss only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 `
        -CertThumbprint 1A2B3C...  -TimestampUrl http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [string] $CertThumbprint,
    [string] $TimestampUrl  = 'http://timestamp.digicert.com',
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'OhMyAgent.AiAgent.Client\OhMyAgent.AiAgent.Client.csproj'
$issPath     = Join-Path $PSScriptRoot 'oma-client.iss'
$publishDir  = Join-Path $repoRoot "artifacts\publish\$Runtime"
$outputDir   = Join-Path $repoRoot 'artifacts'

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-Tool {
    <#
        Finds an executable that may not be on PATH. Returns $null rather than
        throwing so callers can decide whether the tool is mandatory.
    #>
    param(
        [string]   $Name,
        [string[]] $Candidates
    )

    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path $candidate)) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

# ---------------------------------------------------------------- prerequisites

Write-Step 'Locating tools'

# Finding dotnet is not enough: a machine can have a runtime-only install at
# C:\Program Files\dotnet (empty sdk folder) while the real SDK lives under the
# user profile. Picking the first dotnet.exe on PATH then fails at publish time
# with a confusing error, so each candidate is probed with --list-sdks.
function Resolve-DotnetWithSdk {
    $candidates = @()
    $onPath = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += @(
        "$env:USERPROFILE\.dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
        Write-Host "  (skipped $candidate - no SDK installed)"
    }
    return $null
}

$dotnet = Resolve-DotnetWithSdk
if (-not $dotnet) { throw 'No dotnet installation with an SDK found. Install the .NET 10 SDK.' }
Write-Host "  dotnet : $dotnet"

$iscc = Resolve-Tool -Name 'ISCC.exe' -Candidates @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup).' }
Write-Host "  ISCC   : $iscc"

$signtool = $null
if ($CertThumbprint) {
    $sdkCandidates = @()
    foreach ($root in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (Test-Path $root) {
            # Newest SDK first, so we do not pick up an ancient signtool.
            $sdkCandidates += (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' })
        }
    }
    $signtool = Resolve-Tool -Name 'signtool.exe' -Candidates $sdkCandidates
    if ($signtool) {
        Write-Host "  signtool: $signtool"
    } else {
        # Set-AuthenticodeSignature is built into PowerShell and needs no SDK,
        # so a missing signtool is not fatal.
        Write-Host '  signtool: not found - falling back to Set-AuthenticodeSignature'
    }

    $cert = Get-ChildItem "Cert:\CurrentUser\My\$CertThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "No certificate with thumbprint $CertThumbprint in Cert:\CurrentUser\My." }
    if (-not $cert.HasPrivateKey) { throw 'Certificate has no private key; it cannot sign.' }
    Write-Host "  cert   : $($cert.Subject)"
} else {
    Write-Host '  signing: SKIPPED (no -CertThumbprint)' -ForegroundColor Yellow
}

# Read the version from the csproj so the installer filename and the app version
# can never drift apart.
# XmlDocument.Load is used instead of "[xml](Get-Content ...)": Get-Content in
# Windows PowerShell 5.1 decodes BOM-less files with the ANSI codepage, which
# corrupts the non-ASCII metadata in this csproj and breaks the XML parse.
$csproj = New-Object System.Xml.XmlDocument
$csproj.Load($projectPath)
$versionNode = $csproj.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "Could not read <Version> from $projectPath" }
$version = $versionNode.InnerText.Trim()
if (-not $version) { throw "<Version> in $projectPath is empty" }
Write-Host "  version: $version"

# ---------------------------------------------------------------------- publish

if ($SkipPublish) {
    Write-Step 'Skipping publish (-SkipPublish)'
    if (-not (Test-Path $publishDir)) { throw "No existing publish at $publishDir" }
} else {
    Write-Step "Publishing $Configuration / $Runtime (self-contained)"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & $dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$exePath = Join-Path $publishDir 'OhMyAgent.AiAgent.Client.exe'
if (-not (Test-Path $exePath)) { throw "Publish did not produce $exePath" }

$publishSizeMb = [Math]::Round(
    ((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "  published: $publishDir ($publishSizeMb MB)"

# ------------------------------------------------------------- sign our binaries

function Invoke-Sign {
    param([string[]] $Paths)

    if (-not $CertThumbprint) { return }

    if ($signtool) {
        $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $CertThumbprint)
        if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        $signArgs += $Paths
        & $signtool @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    } else {
        $cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint"
        foreach ($path in $Paths) {
            $signParams = @{
                FilePath      = $path
                Certificate   = $cert
                HashAlgorithm = 'SHA256'
            }
            if ($TimestampUrl) { $signParams.TimestampServer = $TimestampUrl }
            Set-AuthenticodeSignature @signParams | Out-Null
        }
    }

    # Verify separately from signing, because "signed" and "trusted" are
    # different questions. A self-signed development certificate embeds a
    # perfectly valid signature but reports UnknownError/NotTrusted until its
    # root is installed in the trust store - that is a machine trust
    # configuration issue, not a signing failure, and must not fail the build.
    foreach ($path in $Paths) {
        $check = Get-AuthenticodeSignature $path
        if (-not $check.SignerCertificate) {
            throw "Signing produced no signature on $path (status: $($check.Status))"
        }
        if ($check.SignerCertificate.Thumbprint -ne $CertThumbprint) {
            throw "$path is signed by an unexpected certificate: $($check.SignerCertificate.Thumbprint)"
        }
        if ($check.Status -ne 'Valid') {
            $script:UntrustedRootSeen = $true
        }
    }
}

# Tracks whether any signature verified as something other than Valid, so the
# final report can explain it once instead of per file.
$script:UntrustedRootSeen = $false

if ($CertThumbprint) {
    Write-Step 'Signing application binaries'

    # Sign only what we build. Third-party and .NET runtime assemblies arrive
    # already signed by their publishers; re-signing them would be wrong and
    # would invalidate their original signatures.
    $ourBinaries = @('OhMyAgent.AiAgent.Client.exe',
                     'OhMyAgent.AiAgent.Client.dll',
                     'OhMyAgent.AiAgent.Core.dll') |
        ForEach-Object { Join-Path $publishDir $_ } |
        Where-Object { Test-Path $_ }

    foreach ($binary in $ourBinaries) { Write-Host "  $(Split-Path -Leaf $binary)" }
    Invoke-Sign -Paths $ourBinaries
}

# ---------------------------------------------------------------------- compile

Write-Step 'Compiling installer'

if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

& $iscc `
    "/DAppVersion=$version" `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$installerPath = Join-Path $outputDir "OhMyAgent-Setup-$version.exe"
if (-not (Test-Path $installerPath)) { throw "ISCC did not produce $installerPath" }

# --------------------------------------------------------------- sign installer

if ($CertThumbprint) {
    Write-Step 'Signing installer'
    Invoke-Sign -Paths @($installerPath)
}

# ----------------------------------------------------------------------- report

Write-Step 'Done'

$installerMb = [Math]::Round((Get-Item $installerPath).Length / 1MB, 1)
Write-Host "  installer : $installerPath ($installerMb MB)"

if ($CertThumbprint) {
    $sig = Get-AuthenticodeSignature $installerPath
    Write-Host "  signature : $($sig.Status)"
    if ($sig.SignerCertificate) { Write-Host "  signer    : $($sig.SignerCertificate.Subject)" }

    if ($sig.TimeStamperCertificate) {
        Write-Host '  timestamp : yes'
    } else {
        Write-Host '  timestamp : NONE - signature dies when the certificate expires' -ForegroundColor Yellow
        Write-Host '              On an isolated network, pass -TimestampUrl <internal RFC 3161 TSA>.'
    }

    if ($script:UntrustedRootSeen) {
        Write-Host ''
        Write-Host '  NOTE: signatures are embedded but the issuing root is not trusted' -ForegroundColor Yellow
        Write-Host '        on this machine. Expected for a self-signed development'
        Write-Host '        certificate. Deploy the root via GPO (Trusted Root +'
        Write-Host '        Trusted Publishers), or use a certificate from the'
        Write-Host '        corporate CA, which domain machines already trust.'
    }
} else {
    Write-Host '  signature : UNSIGNED' -ForegroundColor Yellow
    Write-Host '              SmartScreen will warn on download, and the app'
    Write-Host '              integrity screen will report binaries as unsigned.'
}
