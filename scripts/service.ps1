param([ValidateSet('start','stop','restart','status')]$Action='status')
switch($Action){
 'start' { sc.exe start QuickAdminAgent; sc.exe start QuickAdminWeb }
 'stop' { sc.exe stop QuickAdminWeb; sc.exe stop QuickAdminAgent }
 'restart' { sc.exe stop QuickAdminWeb; sc.exe stop QuickAdminAgent; Start-Sleep 2; sc.exe start QuickAdminAgent; sc.exe start QuickAdminWeb }
 'status' { sc.exe query QuickAdminAgent; sc.exe query QuickAdminWeb }
}
