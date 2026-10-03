# Run this script from an elevated PowerShell prompt after publishing.
# It intentionally scopes access to Private networks and the local subnet.
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'publish\RemoteDeck.exe'
if (-not (Test-Path -LiteralPath $app)) { throw "Published executable not found: $app" }
Get-NetFirewallRule -DisplayName 'RemoteDeck development' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'RemoteDeck' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'RemoteDeck' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8765 -Program $app -Profile Private -RemoteAddress LocalSubnet | Out-Null
Write-Host "Created Private/LocalSubnet firewall rule for $app"
Write-Host 'Do not create a router port-forward for TCP 8765.'
