$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install the .NET 10 SDK first."
}

$dalamudPath = Join-Path $env:APPDATA "XIVLauncher\addon\Hooks\dev"
if (-not (Test-Path $dalamudPath)) {
    throw "Dalamud developer files were not found at: $dalamudPath"
}

$env:DALAMUD_HOME = $dalamudPath
dotnet build "$PSScriptRoot\LightlessAutoPair.csproj" -c Release
