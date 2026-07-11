# Packs a dotnet-publish win-x64 output into a self-signed .msix, ready to
# upload to Partner Center (Store re-signs with its own trusted certificate
# during certification — the upload signature only needs to satisfy MSIX's
# "must be signed" requirement and match the manifest's Publisher, it doesn't
# need to chain to a purchased/trusted root).
#
# Usage:
#   pwsh scripts/windows/build-msix.ps1 -PublishDir publish\win-x64 -Version 1.2.3 -Output pgNimbus-1.2.3-win-x64.msix
#
# Requires the Windows 10/11 SDK (makeappx.exe, signtool.exe) — resolved by
# globbing every installed SDK bin\<ver>\x64 dir and taking the newest, so it
# doesn't hardcode a version that will drift as the SDK updates. Windows-only.
param(
    [Parameter(Mandatory)] [string]$PublishDir,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Output,
    [string]$ManifestTemplate = (Join-Path $PSScriptRoot '..\..\installer\msix\Package.appxmanifest'),
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\..\PgNimbus.App\Assets\Msix')
)
$ErrorActionPreference = 'Stop'

function Resolve-SdkTool([string]$Name) {
    $root = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $found = Get-ChildItem -Path $root -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '^\d+(\.\d+){1,3}$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $found) { throw "$Name not found under $root — install the Windows 10/11 SDK." }
    return $found
}

# MSIX requires a plain 4-part numeric version with the last field reserved
# as 0 by Store convention; drop any prerelease suffix (e.g. "0.0.0-ci.42").
function ConvertTo-MsixVersion([string]$V) {
    $core = ($V -split '-')[0]
    $parts = @($core -split '\.') + @('0', '0', '0', '0')
    return ($parts[0..2] -join '.') + '.0'
}

$makeappx = Resolve-SdkTool 'makeappx.exe'
$makepri  = Resolve-SdkTool 'makepri.exe'
$signtool = Resolve-SdkTool 'signtool.exe'
$msixVersion = ConvertTo-MsixVersion $Version

$manifestXml = Get-Content $ManifestTemplate -Raw
if ($manifestXml -notmatch 'Publisher="([^"]+)"') { throw "Publisher not found in $ManifestTemplate" }
$publisher = $Matches[1]

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pgnimbus-msix-" + [guid]::NewGuid())
try {
    New-Item -ItemType Directory -Force -Path "$stage\Assets" | Out-Null
    Copy-Item "$PublishDir\*" -Destination $stage -Recurse -Force
    Copy-Item "$AssetsDir\*.png" -Destination "$stage\Assets" -Force
    ($manifestXml -replace '\$VERSION\$', $msixVersion) | Set-Content -Path "$stage\AppxManifest.xml" -Encoding utf8NoBOM

    # The scale-/targetsize-qualified icon filenames (Square44x44Logo.scale-200.png,
    # Square44x44Logo.targetsize-48_altform-unplated.png, etc.) only get resolved by
    # Windows through a resource map — makeappx does not infer one from filenames
    # alone. Compile it here so the qualified assets actually take effect instead of
    # silently packing as inert extra files.
    Write-Host "Indexing resources (makepri)"
    $priConfig = Join-Path $stage 'priconfig.xml'
    & $makepri createconfig /cf $priConfig /dq en-US /o
    if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed with exit code $LASTEXITCODE" }

    # createconfig defaults to splitting scale-qualified resources out into
    # separate resources.scale-*.pri side files — meant for AppxBundle resource
    # packages, where the manifest declares a <ResourcePackage> per file. We
    # ship one flat, non-bundle package with every qualified asset already
    # inside it, so without that manifest wiring those side files would just
    # be dead weight and Windows would only ever see the scale-100 entries in
    # the main resources.pri. Strip the auto-split so everything lands in one
    # resources.pri instead.
    [xml]$priConfigXml = Get-Content $priConfig
    $priConfigXml.resources.packaging.RemoveAll()
    $priConfigXml.Save($priConfig)

    & $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage 'resources.pri') /o
    if ($LASTEXITCODE -ne 0) { throw "makepri new failed with exit code $LASTEXITCODE" }
    Remove-Item $priConfig -Force

    Write-Host "Packing MSIX (version $msixVersion) -> $Output"
    & $makeappx pack /d $stage /p $Output /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Signing MSIX with ephemeral self-signed cert (Subject=$publisher)"
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
    -KeyUsage DigitalSignature -FriendlyName 'pgNimbus MSIX upload cert (ephemeral)' `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(7)
try {
    & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $Output
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
}

Write-Host "wrote $Output"
