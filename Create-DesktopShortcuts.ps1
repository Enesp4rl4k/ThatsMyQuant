# =============================================================================
# ThatsMyQuant - Desktop Shortcut Creator (PowerShell 5.1 compatible)
# =============================================================================

$ErrorActionPreference = 'Continue'

$repoRoot = $PSScriptRoot
if (-not $repoRoot) { $repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$desktop = [Environment]::GetFolderPath('Desktop')

$dashboardExe = Join-Path $repoRoot '04_UI\ThatsMyQuant.Dashboard\bin\Release\net9.0-windows\ThatsMyQuant.Dashboard.exe'
$launcherExe  = Join-Path $repoRoot '04_UI\ThatsMyQuant.Launcher\bin\Release\net9.0-windows\ThatsMyQuant.Launcher.exe'

function New-AppShortcut {
    param($ShortcutName, $TargetPath, $Description)
    if (-not (Test-Path $TargetPath)) {
        Write-Host "[warn] $ShortcutName target missing: $TargetPath" -ForegroundColor Yellow
        return
    }
    $lnk = Join-Path $desktop ($ShortcutName + '.lnk')
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath = $TargetPath
    $sc.WorkingDirectory = Split-Path $TargetPath
    $sc.Description = $Description
    $sc.WindowStyle = 1
    $sc.Save()
    Write-Host "[ok] Desktop shortcut created: $lnk" -ForegroundColor Green
}

Write-Host ''
Write-Host '  ThatsMyQuant - Desktop Shortcut Creator  ' -ForegroundColor Cyan
Write-Host '==========================================' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $dashboardExe)) {
    Write-Host '[info] Dashboard Release build not found. Building...' -ForegroundColor Yellow
    Push-Location $repoRoot
    & dotnet build '04_UI\ThatsMyQuant.Dashboard\ThatsMyQuant.Dashboard.csproj' -c Release --nologo -v q
    Pop-Location
}

New-AppShortcut 'ThatsMyQuant Dashboard' $dashboardExe 'Open the ThatsMyQuant Dashboard (Institutional Terminal) directly.'
New-AppShortcut 'ThatsMyQuant Launcher'  $launcherExe  'Start the ThatsMyQuant Launcher (gateway + workers + dashboard).'

Write-Host ''
Write-Host "Done. Double-click 'ThatsMyQuant Dashboard' on your Desktop to open." -ForegroundColor Green
Write-Host ''
