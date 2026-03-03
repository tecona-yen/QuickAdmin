param([Parameter(Mandatory)] [string]$Password,[string]$ConfigPath="C:\Program Files\QuickAdmin\web\config\quickadmin.json")
$bytes = [System.Text.Encoding]::UTF8.GetBytes($Password)
$salt = New-Object byte[] 16
[System.Security.Cryptography.RandomNumberGenerator]::Fill($salt)
$pbkdf2 = [System.Security.Cryptography.Rfc2898DeriveBytes]::new($bytes,$salt,100000,[System.Security.Cryptography.HashAlgorithmName]::SHA256)
$hash = $pbkdf2.GetBytes(32)
$json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$json.auth.passwordHash = [Convert]::ToBase64String($hash)
$json.auth.passwordSalt = [Convert]::ToBase64String($salt)
$json | ConvertTo-Json -Depth 8 | Set-Content $ConfigPath
Write-Host "Password updated. Restart QuickAdminWeb service."
