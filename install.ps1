# Ember installer: builds, copies to Program Files, adds a Start menu shortcut and starts the app.
# The app registers its own logon task (elevated, no UAC prompt) on its first elevated run.
#   powershell -ExecutionPolicy Bypass -File install.ps1             install or update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove (settings and themes stay in %APPDATA%\Ember)
param([switch]$Uninstall, [switch]$Elevated)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:ProgramFiles 'Ember'
$exe = Join-Path $dest 'Ember.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Ember.lnk'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $Uninstall -and -not $Elevated) {
    Write-Host 'Buduję Ember...'
    dotnet publish "$src\Ember.csproj" -c Release -r win-x64 --self-contained false -o "$src\publish" --nologo -v q
    if ($LASTEXITCODE) { throw 'Kompilacja nie powiodła się' }
}

if (-not $admin) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
    if ($Uninstall) { $a += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $a -Wait
    return
}

# A running instance is asked to quit first, so the panel gets darkened properly; anything left is killed.
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -Wait -ErrorAction SilentlyContinue }
Get-Process Ember -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    schtasks /Delete /TN Ember /F 2>$null | Out-Null
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $lnk -ErrorAction SilentlyContinue
    Write-Host "Ember usunięty. Ustawienia i motywy zostały w $env:APPDATA\Ember"
    Start-Sleep -Seconds 3
    return
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$src\publish\*" $dest -Recurse -Force

$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.Description = 'Ember: ekran Turing 3.5"'
$sc.Save()

# The old TURZX software left a logon task (UsbMonitor) pointing at a deleted exe. If it ever came back it would grab the COM port.
schtasks /Query /TN UsbMonitor 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { schtasks /Change /TN UsbMonitor /DISABLE | Out-Null; Write-Host 'Wyłączyłem stare zadanie UsbMonitor (oryginalny soft TURZX).' }

Start-Process $exe
Write-Host "Gotowe: $dest"
Start-Sleep -Seconds 2
