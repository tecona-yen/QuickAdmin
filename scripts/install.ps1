param(
    [string]$PublishRoot = "C:\Program Files\QuickAdmin"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

Write-Host "Publishing agent..."
dotnet publish .\src\QuickAdmin.Agent\QuickAdmin.Agent.csproj -c Release -o "$PublishRoot\agent"
Write-Host "Publishing web..."
dotnet publish .\src\QuickAdmin.Web\QuickAdmin.Web.csproj -c Release -o "$PublishRoot\web"

Copy-Item .\config\quickadmin.json "$PublishRoot\web\config\quickadmin.json" -Force
Copy-Item .\config\quickadmin.json "$PublishRoot\agent\config\quickadmin.json" -Force

sc.exe create QuickAdminAgent binPath= "\"$PublishRoot\agent\QuickAdmin.Agent.exe\"" start= auto
sc.exe create QuickAdminWeb binPath= "\"$PublishRoot\web\QuickAdmin.Web.exe\"" start= auto
sc.exe start QuickAdminAgent
sc.exe start QuickAdminWeb
Write-Host "Installed. Web on http://localhost:8600"
