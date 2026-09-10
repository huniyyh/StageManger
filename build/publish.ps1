<#
.SYNOPSIS
  Builds the installer and update packages with Velopack.

.DESCRIPTION
  1. dotnet publish (Release, win-x64, framework-dependent: the installer fetches the .NET Desktop runtime if needed)
  2. vpk pack -> Releases\StageManager-win-Setup.exe plus the update packages the app downloads later

  Run "dotnet tool restore" once so the local vpk tool is available.
  To publish delta updates, run "dotnet vpk download github --repoUrl <repo>" first so vpk can diff against the previous release.

.EXAMPLE
  .\build\publish.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Channel = "win",
    [string] $OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "publish"
if ($OutputDir -eq "") { $OutputDir = Join-Path $root "Releases" }

Write-Host "== publishing v$Version ==" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $root "src\StageManager.App") -c Release -r win-x64 --self-contained false -o $publishDir -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "== packing with Velopack ==" -ForegroundColor Cyan
Push-Location $root
try {
    dotnet vpk pack `
        --packId StageManager `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe StageManager.exe `
        --packTitle "Stage Manager" `
        --packAuthors huniyyh `
        --icon (Join-Path $root "src\StageManager.App\Assets\stagemanager.ico") `
        --framework net10.0-x64-desktop `
        --channel $Channel `
        --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
}
finally { Pop-Location }

Write-Host "== done ==" -ForegroundColor Cyan
Get-ChildItem $OutputDir | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
