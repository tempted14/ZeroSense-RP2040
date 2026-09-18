$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "RainbowRecoil.csproj"
$output = Join-Path $PSScriptRoot "artifacts\win-x64"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

Write-Host "Restoring Windows app dependencies..." -ForegroundColor Cyan
dotnet restore $project --runtime win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing the self-contained Windows x64 app..." -ForegroundColor Cyan
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $output `
    -p:WindowsAppSDKSelfContained=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$executable = Join-Path $output "zerosense.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish finished without creating $executable"
}

Write-Host "Build complete: $executable" -ForegroundColor Green
