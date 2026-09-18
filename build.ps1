$ErrorActionPreference = "Stop"

$tests = Join-Path $PSScriptRoot "Tests\RainbowRecoil.CoreTests.csproj"
Write-Host "Running core regression tests..." -ForegroundColor Cyan
dotnet run --project $tests --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot "WindowsApp\build.ps1")
exit $LASTEXITCODE
