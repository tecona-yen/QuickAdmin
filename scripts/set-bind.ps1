param([ValidateSet('localhost','lan')]$Mode='localhost',[string]$ConfigPath="C:\Program Files\QuickAdmin\web\config\quickadmin.json")
$json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
if($Mode -eq 'lan'){ $json.server.allowLanAccess = $true; $json.server.bindAddress='0.0.0.0' } else { $json.server.allowLanAccess=$false; $json.server.bindAddress='127.0.0.1' }
$json.server.port = 8600
$json | ConvertTo-Json -Depth 8 | Set-Content $ConfigPath
Write-Host "Bind mode updated. Restart QuickAdminWeb service."
