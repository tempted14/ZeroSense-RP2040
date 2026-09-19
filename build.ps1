$ErrorActionPreference = "Stop"

$tests = Join-Path $PSScriptRoot "Tests\RainbowRecoil.CoreTests.csproj"
$nugetConfig = Join-Path $PSScriptRoot "NuGet.Config"
Write-Host "Running core regression tests..." -ForegroundColor Cyan
dotnet restore $tests --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project $tests --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot "WindowsApp\build.ps1")
exit $LASTEXITCODE
