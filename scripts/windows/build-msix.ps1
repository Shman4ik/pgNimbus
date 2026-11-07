# Packs dotnet-publish outputs into a self-signed MSIX package or bundle, ready
# to upload to Partner Center (Store re-signs with its own trusted certificate
# during certification — the upload signature only needs to satisfy MSIX's
# "must be signed" requirement and match the manifest's Publisher, it doesn't
# need to chain to a purchased/trusted root).
#
# One architecture makes a .msix. Several make a .msixbundle with one .msix per
# architecture inside, which is what the release pipeline builds: Partner Center
# takes the bundle as one upload, and the Store installs the package that
# matches the device, so Windows on Arm gets the arm64 build instead of the x64
# one under emulation.
#
# Usage (each -Package is "<architecture>=<publish dir>", architecture x64 or arm64):
#   pwsh scripts/windows/build-msix.ps1 -Package x64=publish\win-x64 -Version 1.2.3 -Output pgNimbus-1.2.3-win-x64.msix
#   pwsh scripts/windows/build-msix.ps1 -Package x64=publish\win-x64,arm64=publish\win-arm64 -Version 1.2.3 -Output pgNimbus-1.2.3-win.msixbundle
#
# -CertificateOutput writes the public half of the signing certificate to a
# .cer file, for a test install (trust it in LocalMachine\TrustedPeople first).
#
# Requires the Windows 10/11 SDK (makeappx.exe, makepri.exe, signtool.exe) —
# resolved by globbing every installed SDK's bin\<ver>\<arch> dir and taking
# the newest, so it doesn't hardcode a version that will drift as the SDK
# updates. Windows-only.
param(
    [Parameter(Mandatory)] [string[]]$Package,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Output,
    [string]$CertificateOutput,
    [string]$ManifestTemplate = (Join-Path $PSScriptRoot '..\..\packaging\msix\Package.appxmanifest'),
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\..\src\PgNimbus.App\Assets\Msix')
)
$ErrorActionPreference = 'Stop'

function Resolve-SdkTool([string]$Name) {
    $root = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    # The machine's own architecture first; Windows on Arm also runs the x64 tools.
    $arches = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { @('arm64', 'x64') } else { @('x64') }
    $versions = Get-ChildItem -Path $root -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '^\d+(\.\d+){1,3}$' } |
        Sort-Object { [version]$_.Name } -Descending
    foreach ($arch in $arches) {
        $found = $versions |
            ForEach-Object { Join-Path $_.FullName "$arch\$Name" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($found) { return $found }
    }
    throw "$Name not found under $root — install the Windows 10/11 SDK."
}

# MSIX requires a plain 4-part numeric version with the last field reserved
# as 0 by Store convention; drop any prerelease suffix (e.g. "0.0.0-ci.42").
function ConvertTo-MsixVersion([string]$V) {
    $core = ($V -split '-')[0]
    $parts = @($core -split '\.') + @('0', '0', '0', '0')
    return ($parts[0..2] -join '.') + '.0'
}

# "x64=publish\win-x64" -> architecture and folder, checked before anything is built.
# A comma-joined list arrives as one string through pwsh -File, so split it too.
$packages = foreach ($entry in ($Package -split ',')) {
    $arch, $dir = $entry -split '=', 2
    if ($arch -notin 'x64', 'arm64' -or -not $dir) {
        throw "-Package '$entry' is not <x64|arm64>=<publish dir>"
    }
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { throw "$dir does not exist" }
    [pscustomobject]@{ Architecture = $arch; PublishDir = (Resolve-Path -LiteralPath $dir).Path }
}
$duplicate = $packages | Group-Object Architecture | Where-Object Count -gt 1 | Select-Object -First 1
if ($duplicate) { throw "-Package names $($duplicate.Name) twice" }

$bundle = [System.IO.Path]::GetExtension($Output) -eq '.msixbundle'
if (-not $bundle -and [System.IO.Path]::GetExtension($Output) -ne '.msix') {
    throw "-Output must end in .msix (one architecture) or .msixbundle"
}
if (-not $bundle -and @($packages).Count -ne 1) {
    throw "a .msix holds one architecture; name a .msixbundle to pack $(@($packages).Count)"
}

$makeappx = Resolve-SdkTool 'makeappx.exe'
$makepri  = Resolve-SdkTool 'makepri.exe'
$signtool = Resolve-SdkTool 'signtool.exe'
$msixVersion = ConvertTo-MsixVersion $Version
$Output = [System.IO.Path]::GetFullPath($Output)

$manifestXml = Get-Content $ManifestTemplate -Raw
if ($manifestXml -notmatch 'Publisher="([^"]+)"') { throw "Publisher not found in $ManifestTemplate" }
$publisher = $Matches[1]

function Invoke-Sign([string]$Path) {
    & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $Path with exit code $LASTEXITCODE" }
}

function New-MsixPackage($Spec, [string]$Destination) {
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pgnimbus-msix-" + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Force -Path "$stage\Assets" | Out-Null
        # .pdb debug symbols are dev-only (crash-dump/symbol-server use) and add
        # tens of MB (e.g. libSkiaSharp.pdb) with no end-user benefit — same
        # exclusion the portable zip applies in build-zip.ps1.
        Copy-Item "$($Spec.PublishDir)\*" -Destination $stage -Recurse -Force -Exclude '*.pdb'
        Get-ChildItem -Path $stage -Filter '*.pdb' -Recurse | Remove-Item -Force

        # The package says which architecture it is for, and the Store hands it
        # only to devices of that architecture, so it must say what the files are.
        & (Join-Path $PSScriptRoot 'Assert-PeArchitecture.ps1') -Path $stage -Architecture $Spec.Architecture

        Copy-Item "$AssetsDir\*.png" -Destination "$stage\Assets" -Force
        ($manifestXml -replace '\$VERSION\$', $msixVersion -replace '\$ARCH\$', $Spec.Architecture) |
            Set-Content -Path "$stage\AppxManifest.xml" -Encoding utf8NoBOM

        # The scale-/targetsize-qualified icon filenames (Square44x44Logo.scale-200.png,
        # Square44x44Logo.targetsize-48_altform-unplated.png, etc.) only get resolved by
        # Windows through a resource map — makeappx does not infer one from filenames
        # alone. Compile it here so the qualified assets actually take effect instead of
        # silently packing as inert extra files.
        Write-Host "Indexing resources (makepri, $($Spec.Architecture))"
        $priConfig = Join-Path $stage 'priconfig.xml'
        & $makepri createconfig /cf $priConfig /dq en-US /o
        if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed with exit code $LASTEXITCODE" }

        # createconfig defaults to splitting scale-qualified resources out into
        # separate resources.scale-*.pri side files — meant for resource packages,
        # where the bundle carries one package per scale. Ours carry every
        # qualified asset inside each architecture's package (a few hundred KB of
        # icons), so without that wiring those side files would just be dead
        # weight and Windows would only ever see the scale-100 entries in the
        # main resources.pri. Strip the auto-split so everything lands in one
        # resources.pri instead.
        [xml]$priConfigXml = Get-Content $priConfig
        $priConfigXml.resources.packaging.RemoveAll()
        $priConfigXml.Save($priConfig)

        & $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage 'resources.pri') /o
        if ($LASTEXITCODE -ne 0) { throw "makepri new failed with exit code $LASTEXITCODE" }
        Remove-Item $priConfig -Force

        Write-Host "Packing MSIX ($($Spec.Architecture), version $msixVersion) -> $Destination"
        & $makeappx pack /d $stage /p $Destination /o
        if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }
    }
    finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# One ephemeral certificate signs every package and then the bundle, the way
# Visual Studio signs one: the bundle and the packages in it share a signer.
Write-Host "Creating ephemeral self-signed cert (Subject=$publisher)"
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
    -KeyUsage DigitalSignature -FriendlyName 'pgNimbus MSIX upload cert (ephemeral)' `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(7)
$packageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pgnimbus-msix-packages-" + [guid]::NewGuid())
try {
    if ($bundle) {
        New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
        foreach ($spec in $packages) {
            $path = Join-Path $packageDir "pgNimbus-$msixVersion-$($spec.Architecture).msix"
            New-MsixPackage $spec $path
            Invoke-Sign $path
        }
        # /bv: without it makeappx stamps the bundle with the current date and time.
        Write-Host "Bundling $(@($packages).Count) packages (version $msixVersion) -> $Output"
        & $makeappx bundle /d $packageDir /p $Output /bv $msixVersion /o
        if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed with exit code $LASTEXITCODE" }
    }
    else {
        New-MsixPackage $packages[0] $Output
    }
    Invoke-Sign $Output

    if ($CertificateOutput) {
        Export-Certificate -Cert $cert -FilePath $CertificateOutput -Type CERT | Out-Null
        Write-Host "wrote the signing certificate's public half to $CertificateOutput"
    }
}
finally {
    Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue
    # -DeleteKey takes the private key out of the CNG key store too; without it
    # every local run left one behind (CI runners are thrown away anyway).
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -Force -ErrorAction SilentlyContinue
}

Write-Host "wrote $Output"
