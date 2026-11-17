# QiDock Portable Single-File Publisher
# Builds a self-contained, compressed single-file executable for Windows x64.
param (
    [string]$OutputDir = "$PSScriptRoot\..\dist"
)

$ErrorActionPreference = "Stop"

Write-Host "Publishing QiDock as self-contained portable executable..." -ForegroundColor Cyan

$projectPath = "$PSScriptRoot\..\QiDock.csproj"

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "Publish succeeded! Portable executable is ready at: $OutputDir\QiDock.exe" -ForegroundColor Green
} else {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
}
