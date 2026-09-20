[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [switch]$AllowUnsigned,
    [switch]$SkipFirmware
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo "dist"
$appOutput = Join-Path $dist "app"
$project = Join-Path $repo "WindowsApp\RainbowRecoil.csproj"
$nugetConfig = Join-Path $repo "NuGet.Config"

New-Item -ItemType Directory -Force -Path $dist | Out-Null
dotnet restore $project --runtime win-x64 --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    --no-restore --output $appOutput -p:WindowsAppSDKSelfContained=true -p:Version=$Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$certificate = $env:ZEROSENSE_SIGNING_CERTIFICATE
$password = $env:ZEROSENSE_SIGNING_CERTIFICATE_PASSWORD
$signTool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if (-not $signTool) {
    $windowsKits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $windowsKits) {
        $signTool = Get-ChildItem -LiteralPath $windowsKits -Filter signtool.exe -Recurse |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $AllowUnsigned) {
    if (-not $certificate -or -not (Test-Path -LiteralPath $certificate) -or
        -not $password -or -not $signTool) {
        throw "Official releases require signtool.exe plus ZEROSENSE_SIGNING_CERTIFICATE and ZEROSENSE_SIGNING_CERTIFICATE_PASSWORD."
    }
    & $signTool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com `
        /f $certificate /p $password (Join-Path $appOutput "zerosense.exe")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $signTool verify /pa /all (Join-Path $appOutput "zerosense.exe")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipFirmware) {
    & (Join-Path $repo "RP2040_Firmware\build.bat")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -LiteralPath (Join-Path $repo "RP2040_Firmware\rainbow_recoil.uf2") `
        -Destination (Join-Path $dist "ZeroSense-RP2040-Zero-$Version.uf2") -Force
    Copy-Item -LiteralPath (Join-Path $repo "RP2040_Firmware\rainbow_recoil_rp2350_usb_c.uf2") `
        -Destination (Join-Path $dist "ZeroSense-RP2350-USB-C-$Version.uf2") -Force
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $defaultIscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path -LiteralPath $defaultIscc) { $iscc = $defaultIscc }
}
if (-not $iscc) { throw "Inno Setup 6 is required to build the installer." }
& $iscc "/DAppVersion=$Version" (Join-Path $repo "installer\ZeroSense.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$installer = Join-Path $dist "ZeroSense-Setup-$Version-win-x64.exe"
if (-not $AllowUnsigned) {
    & $signTool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com `
        /f $certificate /p $password $installer
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $signTool verify /pa /all $installer
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$artifacts = Get-ChildItem -LiteralPath $dist -File |
    Where-Object { $_.Extension -in '.exe', '.uf2', '.zip' }
$checksumPath = Join-Path $dist "SHA256SUMS.txt"
$checksumLines = foreach ($artifact in $artifacts) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifact.FullName).Hash.ToLowerInvariant()
    "$hash  $($artifact.Name)"
}
[IO.File]::WriteAllLines($checksumPath, $checksumLines)
Write-Host "Release assets created in $dist" -ForegroundColor Green
