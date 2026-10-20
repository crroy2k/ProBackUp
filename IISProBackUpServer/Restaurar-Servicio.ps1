#Requires -Version 5.1
#Requires -RunAsAdministrator
param([string]$InstallRoot = "$env:ProgramFiles\ProBackUpIIS")
. (Join-Path $PSScriptRoot 'Comun.ps1')
if($PSVersionTable.PSEdition -eq 'Core'){throw 'Usa Windows PowerShell 5.1 de 64 bits.'}
$InstallRoot=Assert-InstallRoot $InstallRoot
if(-not (Test-Path -LiteralPath (Join-Path $InstallRoot '.probackup-iis-install'))){throw 'La carpeta no pertenece al paquete IIS.'}
$statePath=Join-Path $InstallRoot 'iis-state.json'
$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
Import-Module WebAdministration
$service=Get-OwnService
$site=Get-Website -Name $state.SiteName
if($site -and ($site.PhysicalPath -ne $state.Release -or $site.ApplicationPool -ne $state.PoolName)){throw 'El sitio se modifico fuera de esta instalacion. No se detuvo.'}
if(@(Get-Website | Where-Object {$_.Name -ne $state.SiteName -and $_.ApplicationPool -eq $state.PoolName}).Count -gt 0 -or @(Get-WebApplication | Where-Object {$_.ApplicationPool -eq $state.PoolName}).Count -gt 0){throw 'El pool se comparte con otra aplicacion; no se detuvo.'}
if($site -and $site.Bindings.Collection.Count -gt 1){throw 'El sitio tiene bindings adicionales ajenos al paquete. Revisalos antes de revertir.'}
if($site){Stop-Website -Name $state.SiteName}
if((Get-WebAppPoolState -Name $state.PoolName).Value -ne 'Stopped'){Stop-WebAppPool -Name $state.PoolName}
for($i=0;$i -lt 30;$i++){if((Get-WebAppPoolState -Name $state.PoolName).Value -eq 'Stopped'){break};Start-Sleep -Seconds 1}
if((Get-WebAppPoolState -Name $state.PoolName).Value -ne 'Stopped'){throw 'El pool no se detuvo. No se inicio el servicio.'}
if($site){$binding=Get-WebBinding -Name $state.SiteName -Protocol https | Where-Object {$_.bindingInformation -eq ('*:'+$state.HttpsPort+':'+$state.HostName)};if($binding){Remove-WebBinding -Name $state.SiteName -Protocol https -Port $state.HttpsPort -HostHeader $state.HostName}}
$dataRoot=Join-Path $env:ProgramData 'ProBackUpServer'
Assert-NoLinks $dataRoot
$sid=(New-Object Security.Principal.NTAccount ('IIS AppPool\'+$state.PoolName)).Translate([Security.Principal.SecurityIdentifier])
$acl=Get-Acl -LiteralPath $dataRoot
$rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'Modify','ContainerInherit,ObjectInherit','None','Allow')
$acl.RemoveAccessRuleSpecific($rule);Set-Acl -LiteralPath $dataRoot -AclObject $acl
$marker=Join-Path $dataRoot 'iis-hosting.json'
if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker}
Set-ServiceStartup $state.StartMode $state.Delayed
if($state.WasRunning){Start-Service ProBackUpServer;(Get-Service ProBackUpServer).WaitForStatus('Running',[TimeSpan]::FromSeconds(45));$config=Get-Content -LiteralPath (Join-Path $dataRoot 'server.json') -Raw | ConvertFrom-Json;Test-LocalHttps $config.PublicHost $config.Port}
$rule=Get-NetFirewallRule -Name 'ProBackUp-IIS-HTTPS' -ErrorAction SilentlyContinue
if($rule -and $rule.Description -eq 'ProBackUp IIS - owned rule'){Disable-NetFirewallRule -InputObject $rule}
Write-Host 'IIS de ProBackUp detenido. Inicio original del servicio restaurado; datos y versiones IIS conservados.' -ForegroundColor Green
Write-Host 'Actualiza la URL de los clientes al puerto HTTPS original. No arranques IIS y el servicio simultaneamente.'
