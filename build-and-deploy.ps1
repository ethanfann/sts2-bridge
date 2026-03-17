$ErrorActionPreference = 'Stop'

$ProjectName = 'FirstMod'
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistDir = Join-Path $ProjectDir 'dist'
$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
$GodotExe = 'C:\Users\ecfan\Downloads\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe'
$GameDll = Join-Path $GameDir 'data_sts2_windows_x86_64\sts2.dll'
$ModDir = Join-Path $GameDir "mods\$ProjectName"
$ProjectDll = Join-Path $ProjectDir 'sts2.dll'
$ManifestPath = Join-Path $ProjectDir 'mod_manifest.json'
$CsprojPath = Join-Path $ProjectDir 'FirstMod.csproj'
$ExportPresetPath = Join-Path $ProjectDir 'export_presets.cfg'
$OutputPck = Join-Path $DistDir "$ProjectName.pck"
$FallbackPck = Join-Path $ProjectDir "$ProjectName.pck"
$OutputDll = Join-Path $DistDir "$ProjectName.dll"
$OutputJson = Join-Path $DistDir "$ProjectName.json"

function Require-File {
    param(
        [string] $Path,
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing ${Label}: $Path"
    }
}

function Invoke-Godot {
    param(
        [string[]] $Arguments
    )

    & $GodotExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Godot failed with exit code $LASTEXITCODE"
    }
}

function Invoke-DotnetBuild {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet not found in PATH'
    }

    & dotnet build $CsprojPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

function Invoke-GodotExport {
    $script:ResolvedPckPath = $null
    $process = Start-Process -FilePath $GodotExe -ArgumentList @('--headless', '--path', $ProjectDir, '--export-pack', 'Windows Desktop', $OutputPck, '--quit') -PassThru
    $deadline = (Get-Date).AddMinutes(2)
    $stableCount = 0
    $lastSize = -1

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1

        $candidatePath = $null
        if (Test-Path -LiteralPath $OutputPck -PathType Leaf) {
            $candidatePath = $OutputPck
        } elseif (Test-Path -LiteralPath $FallbackPck -PathType Leaf) {
            $candidatePath = $FallbackPck
        }

        if ($candidatePath) {
            $item = Get-Item -LiteralPath $candidatePath
            if ($item.Length -eq $lastSize) {
                $stableCount += 1
            } else {
                $stableCount = 0
                $lastSize = $item.Length
            }

            if ($stableCount -ge 2 -and $item.Length -gt 0) {
                $script:ResolvedPckPath = $candidatePath
                break
            }
        }

        if ($process.HasExited) {
            break
        }
    }

    if (-not $script:ResolvedPckPath) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        throw 'Godot export did not produce FirstMod.pck'
    }

    if (-not $process.HasExited) {
        Write-Host 'Export finished; stopping stuck Godot process.'
        Stop-Process -Id $process.Id -Force
        Start-Sleep -Milliseconds 500
    }

    if ($script:ResolvedPckPath -ne $OutputPck) {
        Copy-Item -LiteralPath $script:ResolvedPckPath -Destination $OutputPck -Force
        $script:ResolvedPckPath = $OutputPck
    }
}

Require-File -Path $GodotExe -Label 'Godot exe'
Require-File -Path $GameDll -Label 'game sts2.dll'
Require-File -Path $ManifestPath -Label 'mod manifest'
Require-File -Path $CsprojPath -Label 'csproj'
Require-File -Path $ExportPresetPath -Label 'export preset'

$Manifest = Get-Content -LiteralPath $ManifestPath -Raw
if ($Manifest -notmatch '"pck_name"\s*:\s*"FirstMod"') {
    throw 'Manifest must contain "pck_name": "FirstMod"'
}

if ($Manifest -notmatch '"id"\s*:\s*"FirstMod"') {
    throw 'Manifest must contain "id": "FirstMod"'
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
New-Item -ItemType Directory -Force -Path $ModDir | Out-Null

Copy-Item -LiteralPath $GameDll -Destination $ProjectDll -Force
Remove-Item -LiteralPath $OutputDll, $OutputPck, $OutputJson -Force -ErrorAction SilentlyContinue

Write-Host "Using Godot: $GodotExe"
Write-Host "Using game dir: $GameDir"

Invoke-DotnetBuild
Invoke-GodotExport

$DllCandidates = @(
    (Join-Path $ProjectDir ".godot\mono\temp\bin\Debug\$ProjectName.dll"),
    (Join-Path $ProjectDir ".godot\mono\temp\bin\ExportRelease\win-x64\$ProjectName.dll"),
    (Join-Path $ProjectDir ".godot\mono\temp\bin\ExportDebug\win-x64\$ProjectName.dll")
)

$BuiltDll = $DllCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $BuiltDll) {
    throw 'Could not find built DLL output'
}

Copy-Item -LiteralPath $BuiltDll -Destination $OutputDll -Force
Copy-Item -LiteralPath $ManifestPath -Destination $OutputJson -Force

$InstalledDll = Join-Path $ModDir "$ProjectName.dll"
$InstalledPck = Join-Path $ModDir "$ProjectName.pck"
$InstalledJson = Join-Path $ModDir "$ProjectName.json"

Copy-Item -LiteralPath $OutputDll -Destination $InstalledDll -Force
Copy-Item -LiteralPath $OutputPck -Destination $InstalledPck -Force
Copy-Item -LiteralPath $OutputJson -Destination $InstalledJson -Force

Write-Host "Built and deployed $ProjectName"
Write-Host "DLL: $InstalledDll"
Write-Host "PCK: $InstalledPck"
Write-Host "JSON: $InstalledJson"
