#Requires -Version 5.1
#Requires -RunAsAdministrator
param([string]$InstallRoot = "$env:ProgramFiles\ProBackUpIIS")
. (Join-Path $PSScriptRoot 'Comun.ps1')
if($PSVersionTable.PSEdition -eq 'Core'){throw 'Usa Windows PowerShell 5.1 de 64 bits.'}
$InstallRoot=Assert-InstallRoot $InstallRoot
Import-Module WebAdministration
$state=Get-Content -LiteralPath (Join-Path $InstallRoot 'iis-state.json') -Raw | ConvertFrom-Json
$site=Get-Website -Name $state.SiteName
if($site.PhysicalPath -ne $state.Release -or $site.ApplicationPool -ne $state.PoolName){throw 'Sitio o version instalada inesperados.'}
if($site.State -ne 'Started' -or (Get-WebAppPoolState -Name $state.PoolName).Value -ne 'Started'){throw 'El sitio o pool esta detenido.'}
$service=Get-OwnService
if($service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled'){throw 'El servicio independiente debe permanecer detenido y deshabilitado en modo IIS.'}
Test-LocalHttps $state.HostName $state.HttpsPort
Write-Host ('Correcto: IIS, pool, HTTPS, certificado y aplicacion. https://'+$state.HostName+':'+$state.HttpsPort+'/') -ForegroundColor Green
Write-Host 'Completa la prueba entrando con AD y comprobando el reporte de un cliente con su URL nueva.'
