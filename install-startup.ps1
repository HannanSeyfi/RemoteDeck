# Installs a per-user startup shortcut without requiring administrator rights.
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'publish\RemoteDeck.exe'
if (-not (Test-Path -LiteralPath $app)) { throw "Published executable not found: $app" }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'RemoteDeck.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $app
$shortcut.WorkingDirectory = Split-Path -Parent $app
$shortcut.Description = 'RemoteDeck local remote control'
$shortcut.Save()
Write-Host "Installed $shortcutPath"
