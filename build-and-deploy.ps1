param(
    [ValidateSet('build', 'install')]
    [string] $Action = 'build',
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)
$ErrorActionPreference = 'Stop'

$ProjectName = 'sts2-bridge'
$ProjectDir = $PSScriptRoot
$DistDir = Join-Path $ProjectDir 'dist\local'
$GameDataDir = Join-Path $GameDir 'data_sts2_windows_x86_64'
$OutputDll = Join-Path $DistDir "$ProjectName.dll"
$OutputJson = Join-Path $DistDir "$ProjectName.json"

function Require-File([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing file: $Path"
    }
}

if ($Action -eq 'build') {
    Require-File (Join-Path $GameDataDir 'sts2.dll')
    Require-File (Join-Path $GameDataDir '0Harmony.dll')
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 9 SDK and put dotnet on PATH.'
    }
    # A failed compilation must not leave an old package installable.
    Remove-Item -LiteralPath $OutputDll, $OutputJson -Force -ErrorAction SilentlyContinue
    & dotnet build (Join-Path $ProjectDir "$ProjectName.csproj") --configuration Release "-p:GameDataDir=$GameDataDir"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
    $BuiltDll = Join-Path $ProjectDir ".godot\mono\temp\bin\Release\$ProjectName.dll"
    Require-File $BuiltDll
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    Copy-Item -LiteralPath $BuiltDll -Destination $OutputDll -Force
    Copy-Item -LiteralPath (Join-Path $ProjectDir 'mod_manifest.json') -Destination $OutputJson -Force
    Write-Host "Built DLL-only package: $DistDir. No game files were changed."
} else {
    Require-File (Join-Path $GameDataDir 'sts2.dll')
    Require-File $OutputDll
    Require-File $OutputJson
    if (Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue) {
        throw 'Close Slay the Spire 2 before installing the mod.'
    }
    if (Test-Path -LiteralPath (Join-Path $GameDir 'mods\FirstMod')) {
        throw 'Move the old mods/FirstMod folder outside mods before installing sts2-bridge; do not load both.'
    }
    $ModDir = Join-Path $GameDir "mods\$ProjectName"
    New-Item -ItemType Directory -Force -Path $ModDir | Out-Null
    Copy-Item -LiteralPath $OutputDll -Destination (Join-Path $ModDir "$ProjectName.dll") -Force
    Copy-Item -LiteralPath $OutputJson -Destination (Join-Path $ModDir "$ProjectName.json") -Force
    Write-Host "Installed: $ModDir. Enable mods in the game and restart if prompted."
}
