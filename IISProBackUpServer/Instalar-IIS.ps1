#Requires -Version 5.1
#Requires -RunAsAdministrator
param(
    [string]$SiteName = 'ProBackUpServer',
    [string]$PoolName = 'ProBackUpServer',
    [string]$HostName = 'probackup.coopeagri.local',
    [ValidateRange(1,65535)][int]$HttpsPort = 443,
    [string]$CertificateThumbprint = '',
    [string[]]$ClientSubnets = @('LocalSubnet'),
    [string]$InstallRoot = "$env:ProgramFiles\ProBackUpIIS"
)
. (Join-Path $PSScriptRoot 'Comun.ps1')
if ($PSVersionTable.PSEdition -eq 'Core') {throw 'Usa Windows PowerShell 5.1 de 64 bits, no pwsh/PowerShell 7.'}
if (-not [Environment]::Is64BitProcess) {throw 'Abre Windows PowerShell de 64 bits.'}
if ($SiteName -notmatch '^[A-Za-z0-9_.-]+$' -or $PoolName -notmatch '^[A-Za-z0-9_.-]+$') {throw 'Nombres de sitio y pool invalidos.'}
if ([Uri]::CheckHostName($HostName) -ne [UriHostNameType]::Dns) {throw 'Indica el nombre DNS publico, sin https ni rutas.'}
Assert-Package
Import-Module WebAdministration -ErrorAction Stop
if (-not (Get-WebGlobalModule | Where-Object {$_.Name -eq 'AspNetCoreModuleV2'})) {throw 'Instala primero el Hosting Bundle de .NET 10 (incluye ASP.NET Core Module V2). Consulta LEEME.md.'}
# Test-LocalHttps uses strict .NET TLS validation when curl.exe is unavailable.
$InstallRoot = Assert-InstallRoot $InstallRoot
$dataRoot = Join-Path $env:ProgramData 'ProBackUpServer'
Assert-NoLinks $dataRoot
$service = Get-OwnService
$config = Get-Content -LiteralPath (Join-Path $dataRoot 'server.json') -Raw | ConvertFrom-Json
if ($config.PublicHost -ne $HostName) {throw 'El nombre DNS no coincide con server.json. Configuralo con Backend antes de migrar a IIS.'}
if (-not $config.Ldap.Users -or -not ($config.Ldap.Users | Where-Object {$_.Role -eq 'Admin'})) {throw 'Falta la configuracion AD y un administrador autorizado.'}
if (-not $CertificateThumbprint) {
    Get-ChildItem Cert:\LocalMachine\My | Where-Object {$_.HasPrivateKey} | Select-Object Subject,Thumbprint,NotAfter | Format-Table | Out-Host
    $CertificateThumbprint = Read-Host 'Huella del certificado HTTPS en LocalMachine\My'
}
$CertificateThumbprint = ($CertificateThumbprint -replace '\s','').ToUpperInvariant()
if ($CertificateThumbprint -notmatch '^[A-F0-9]{40}$') {throw 'Huella de certificado invalida.'}
$cert = Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $CertificateThumbprint)
if (-not $cert.HasPrivateKey -or $cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)) {throw 'El certificado debe estar vigente e incluir su clave privada.'}
$dnsNames = @($cert.DnsNameList | ForEach-Object {$_.Unicode})
if ($dnsNames -notcontains $HostName) {throw 'El certificado necesita el nombre DNS exacto en SAN.'}
$chain = New-Object Security.Cryptography.X509Certificates.X509Chain
try {
    $chain.ChainPolicy.RevocationMode = 'Online'; $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    $chain.ChainPolicy.ApplicationPolicy.Add((New-Object Security.Cryptography.Oid '1.3.6.1.5.5.7.3.1'))
    if (-not $chain.Build($cert)) {throw 'La cadena del certificado no es confiable o no se pudo comprobar su revocacion.'}
} finally {$chain.Dispose()}
$statePath = Join-Path $InstallRoot 'iis-state.json'
$oldState = $null
if (Test-Path -LiteralPath $InstallRoot) {
    if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot '.probackup-iis-install')) -and (Get-ChildItem -LiteralPath $InstallRoot -Force | Select-Object -First 1)) {throw 'La carpeta destino contiene archivos ajenos.'}
    if (Test-Path -LiteralPath $statePath) {$oldState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json}
}
if ($oldState -and ($oldState.HttpsPort -ne $HttpsPort -or $oldState.HostName -ne $HostName)) {throw 'Para actualizar, conserva el nombre DNS y puerto de la instalacion existente.'}
if ($oldState -and ($oldState.SiteName -ne $SiteName -or $oldState.PoolName -ne $PoolName)) {throw 'La instalacion existente utiliza otro sitio o pool.'}
$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
$poolExists = Test-Path ('IIS:\AppPools\' + $PoolName)
if (($site -or $poolExists) -and -not $oldState) {throw 'Ya existe un sitio o pool con ese nombre y no pertenece a este paquete.'}
if ($site -and (-not $site.PhysicalPath.StartsWith(($InstallRoot + '\releases\'),[StringComparison]::OrdinalIgnoreCase) -or $site.ApplicationPool -ne $PoolName)) {throw 'El sitio existente no pertenece a esta instalacion.'}
foreach ($other in Get-Website | Where-Object {$_.Name -ne $SiteName}) {
    if ($other.ApplicationPool -eq $PoolName) {throw 'El pool esta compartido con otro sitio.'}
    foreach ($binding in $other.Bindings.Collection) {if ($binding.Protocol -eq 'https' -and $binding.BindingInformation -eq ('*:' + $HttpsPort + ':' + $HostName)) {throw 'El binding HTTPS ya pertenece a otro sitio.'}}
}
if (@(Get-WebApplication | Where-Object {$_.ApplicationPool -eq $PoolName}).Count -gt 0) {throw 'El pool esta compartido con una aplicacion ajena.'}
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $InstallRoot '.probackup-iis-install') -Value 'ProBackUpIIS'
$release = Join-Path $InstallRoot ('releases\3.4.0-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
Assert-NoLinks $release
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'sitio\*') -Destination $release -Recurse
$wasIisActive=Test-Path -LiteralPath (Join-Path $dataRoot 'iis-hosting.json')
$createdBinding=$false
$previousPath = if ($site) {$site.PhysicalPath} else {$null}
$wasRunning = $service.State -eq 'Running'
$delayed = [bool](Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ProBackUpServer' -Name DelayedAutoStart -ErrorAction SilentlyContinue).DelayedAutoStart
$original = if ($oldState) {$oldState | ConvertTo-Json | ConvertFrom-Json} else {[pscustomobject]@{SiteName=$SiteName;PoolName=$PoolName;StartMode=$service.StartMode;Delayed=$delayed;WasRunning=$wasRunning}}
if(-not $wasIisActive){$original.StartMode=$service.StartMode;$original.Delayed=$delayed;$original.WasRunning=$wasRunning}
$iisBackup = 'ProBackUp-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
Backup-WebConfiguration -Name $iisBackup
$createdSite = $false; $createdPool = $false; $identity = $null; $rule = $null; $createdFirewall = $false
$firewallName='ProBackUp-IIS-HTTPS'
$existingFirewall=Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue
$firewallWasEnabled=$existingFirewall -and $existingFirewall.Enabled -eq 'True'
if ($existingFirewall -and (-not $oldState -or $existingFirewall.Description -ne 'ProBackUp IIS - owned rule')) {throw 'La regla de firewall existente no pertenece a esta instalacion.'}
try {
    if ($site) {Stop-Website -Name $SiteName; if ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped') {Stop-WebAppPool -Name $PoolName}; Wait-PoolStopped $PoolName}
    Stop-Service ProBackUpServer
    (Get-Service ProBackUpServer).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    Set-Service ProBackUpServer -StartupType Disabled
    if (-not $poolExists) {New-WebAppPool -Name $PoolName | Out-Null; $createdPool=$true}
    $poolPath='IIS:\AppPools\'+$PoolName
    Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
    Set-ItemProperty $poolPath -Name managedPipelineMode -Value Integrated
    Set-ItemProperty $poolPath -Name enable32BitAppOnWin64 -Value $false
    Set-ItemProperty $poolPath -Name startMode -Value AlwaysRunning
    Set-ItemProperty $poolPath -Name processModel.identityType -Value ApplicationPoolIdentity
    Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty $poolPath -Name processModel.maxProcesses -Value 1
    Set-ItemProperty $poolPath -Name recycling.disallowOverlappingRotation -Value $true
    $identity = New-Object Security.Principal.NTAccount ('IIS AppPool\'+$PoolName)
    $sid = $identity.Translate([Security.Principal.SecurityIdentifier])
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid,'Modify','ContainerInherit,ObjectInherit','None','Allow')
    $acl=Get-Acl -LiteralPath $dataRoot; $acl.AddAccessRule($rule); Set-Acl -LiteralPath $dataRoot -AclObject $acl
    $read=New-Object Security.AccessControl.FileSystemAccessRule($sid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')
    $acl=Get-Acl -LiteralPath $InstallRoot; $acl.AddAccessRule($read); Set-Acl -LiteralPath $InstallRoot -AclObject $acl
    if (-not $site) {New-Website -Name $SiteName -PhysicalPath $release -ApplicationPool $PoolName -Port $HttpsPort -HostHeader $HostName -Ssl -SslFlags 1 | Out-Null; $createdSite=$true}
    else {
        Set-ItemProperty ('IIS:\Sites\'+$SiteName) -Name physicalPath -Value $release
        $binding=Get-WebBinding -Name $SiteName -Protocol https | Where-Object {$_.bindingInformation -eq ('*:'+$HttpsPort+':'+$HostName)}
        if (-not $binding) {if($wasIisActive){throw 'El binding de la instalacion IIS activa fue modificado.'};New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -IPAddress '*' -HostHeader $HostName -SslFlags 1;$createdBinding=$true}
    }
    $binding=Get-WebBinding -Name $SiteName -Protocol https | Where-Object {$_.bindingInformation -eq ('*:'+$HttpsPort+':'+$HostName)}
    $binding.AddSslCertificate($CertificateThumbprint,'My')
    $scope='MACHINE/WEBROOT/APPHOST'
    Set-WebConfigurationProperty -PSPath $scope -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $true
    foreach ($auth in @('windowsAuthentication','basicAuthentication','digestAuthentication')) {
        $section=Get-WebConfiguration -PSPath $scope -Location $SiteName -Filter ('system.webServer/security/authentication/'+$auth) -ErrorAction SilentlyContinue
        if($section){Set-WebConfigurationProperty -PSPath $scope -Location $SiteName -Filter ('system.webServer/security/authentication/'+$auth) -Name enabled -Value $false}
    }
    Set-WebConfigurationProperty -PSPath $scope -Location $SiteName -Filter 'system.webServer/security/access' -Name sslFlags -Value 'Ssl'
    Set-WebConfigurationProperty -PSPath $scope -Location $SiteName -Filter 'system.webServer/directoryBrowse' -Name enabled -Value $false
    Set-ItemProperty ('IIS:\Sites\'+$SiteName) -Name applicationDefaults.preloadEnabled -Value $true
    Set-ItemProperty ('IIS:\Sites\'+$SiteName) -Name limits.maxConnections -Value 200
    if(-not $existingFirewall){New-NetFirewallRule -Name $firewallName -DisplayName 'ProBackUp IIS HTTPS' -Description 'ProBackUp IIS - owned rule' -Protocol TCP -LocalPort $HttpsPort -Direction Inbound -Action Allow -Profile Domain,Private -RemoteAddress $ClientSubnets | Out-Null; $createdFirewall=$true}
    if($existingFirewall){Enable-NetFirewallRule -InputObject $existingFirewall}
    if (-not [Diagnostics.EventLog]::SourceExists('ProBackUp.Server')) {New-EventLog -LogName Application -Source 'ProBackUp.Server'}
    $original | Add-Member NoteProperty HostName $HostName -Force
    $original | Add-Member NoteProperty HttpsPort $HttpsPort -Force
    $original | Add-Member NoteProperty Release $release -Force
    $original | Add-Member NoteProperty IisBackup $iisBackup -Force
    $original | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    $original | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'iis-hosting.json') -Encoding UTF8
    if ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Started') {Start-WebAppPool -Name $PoolName}
    Start-Website -Name $SiteName
    Test-LocalHttps $HostName $HttpsPort
    Write-Host ('IIS listo: https://'+$HostName+':'+$HttpsPort+'/') -ForegroundColor Green
    Write-Host 'Actualiza DNS y la URL de los clientes. No ejecutes el servicio y el pool a la vez.'
    Write-Host ('Copia de configuracion IIS: '+$iisBackup)
} catch {
    $failure=$_.Exception.Message
    try {
        if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {Stop-Website -Name $SiteName}
        if($createdBinding){Remove-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $HostName}
        if ($createdSite) {Remove-Website -Name $SiteName}
        elseif ($previousPath) {Set-ItemProperty ('IIS:\Sites\'+$SiteName) -Name physicalPath -Value $previousPath}
        if(Test-Path ('IIS:\AppPools\'+$PoolName)){if((Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped'){Stop-WebAppPool -Name $PoolName};Wait-PoolStopped $PoolName}
        if ($createdPool) {Remove-WebAppPool -Name $PoolName}
        if ($createdFirewall) {Remove-NetFirewallRule -Name $firewallName}
        elseif($existingFirewall -and -not $firewallWasEnabled){Disable-NetFirewallRule -InputObject $existingFirewall}
        if (-not $wasIisActive) {
            if ($rule) {$acl=Get-Acl -LiteralPath $dataRoot; $acl.RemoveAccessRuleSpecific($rule); Set-Acl -LiteralPath $dataRoot -AclObject $acl}
            $marker=Join-Path $dataRoot 'iis-hosting.json'; if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker}
            if($oldState){$oldState | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8}elseif(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath}
            Set-ServiceStartup $original.StartMode $original.Delayed
            if ($wasRunning) {Start-Service ProBackUpServer}
        } else {
            $oldState | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
            $oldState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'iis-hosting.json') -Encoding UTF8
            Start-WebAppPool -Name $PoolName -ErrorAction SilentlyContinue; Start-Website -Name $SiteName
        }
    } catch {$failure += ' | Recuperacion: '+$_.Exception.Message}
    throw ($failure + ' Se conserva la version anterior y el respaldo de IIS '+$iisBackup+'. No se borro ningun respaldo.')
}
