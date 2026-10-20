#Requires -RunAsAdministrator
param([string]$InstallPath = "$env:ProgramFiles\ProBackUpServer", [string[]]$ClientSubnets = @('LocalSubnet'), [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
function Assert-NoReparse([string]$Path) {
    $checkPath = [IO.Path]::GetFullPath($Path)
    while ($checkPath) {
        if (Test-Path -LiteralPath $checkPath) { if ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "No se admiten redirecciones: $checkPath" } }
        $checkPath = [IO.Path]::GetDirectoryName($checkPath)
    }
}
$dataRoot = Join-Path $env:ProgramData 'ProBackUpServer'
if (Test-Path -LiteralPath (Join-Path $dataRoot 'iis-hosting.json')) { throw 'Este servidor esta publicado en IIS. Usa el paquete IIS para actualizarlo o Restaurar-Servicio.ps1 antes de volver al servicio.' }
Assert-NoReparse $dataRoot
$serverExe = Join-Path $PSScriptRoot 'ProBackUp.Server.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw 'Falta ProBackUp.Server.exe junto al instalador.' }
$resolvedTarget = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
$programRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not $resolvedTarget.StartsWith($programRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolvedTarget -eq $programRoot.TrimEnd('\')) { throw 'La instalación debe estar en una subcarpeta de Program Files.' }
$targetExe = Join-Path $resolvedTarget 'ProBackUp.Server.exe'
Assert-NoReparse $targetExe
Assert-NoReparse $serverExe
$expectedPath = '"' + $targetExe + '"'
$service = Get-CimInstance Win32_Service -Filter "Name='ProBackUpServer'"
if (-not $service -and (Test-Path -LiteralPath $dataRoot) -and (Get-ChildItem -LiteralPath $dataRoot -Force | Select-Object -First 1)) { throw 'Existe ProgramData\ProBackUpServer sin un servicio propio registrado. Revise su contenido antes de instalar; no se reutiliza automáticamente.' }
if ($service -and $service.PathName -ne $expectedPath) { throw 'El servicio existente pertenece a otra ruta. No se modificó.' }
$marker = Join-Path $resolvedTarget '.probackup-server'
if ((Test-Path -LiteralPath $resolvedTarget) -and -not (Test-Path -LiteralPath $marker) -and (Get-ChildItem -LiteralPath $resolvedTarget -Force | Select-Object -First 1)) { throw 'La carpeta contiene archivos ajenos.' }
if ($service) { Stop-Service ProBackUpServer; (Get-Service ProBackUpServer).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
$binaryAcl = New-Object System.Security.AccessControl.DirectorySecurity
$binaryAcl.SetAccessRuleProtection($true,$false)
foreach ($sidValue in @('S-1-5-18','S-1-5-32-544')) {
    $sid = New-Object System.Security.Principal.SecurityIdentifier($sidValue)
    $binaryAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
$usersSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
$binaryAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($usersSid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $resolvedTarget -AclObject $binaryAcl
Copy-Item -LiteralPath $serverExe -Destination $targetExe -Force
Set-Content -LiteralPath $marker -Value 'ProBackUpServer'
if (-not $service) {
    New-Service -Name ProBackUpServer -BinaryPathName $expectedPath -StartupType Automatic -DisplayName 'ProBackUp Server' | Out-Null
}
& sc.exe sidtype ProBackUpServer unrestricted
if ($LASTEXITCODE -ne 0) { throw 'No se pudo asignar la identidad del servicio.' }
& sc.exe failure ProBackUpServer reset= 86400 actions= restart/10000/restart/30000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'No se pudo configurar la recuperación.' }
& sc.exe config ProBackUpServer start= delayed-auto obj= 'NT SERVICE\ProBackUpServer'
if ($LASTEXITCODE -ne 0) { throw 'No se pudo configurar la cuenta del servicio.' }
$configuratorSource = Join-Path $PSScriptRoot 'ProBackUp.Server.Config.exe'
$configuratorTarget = Join-Path $resolvedTarget 'ProBackUp.Server.Config.exe'
if (Test-Path -LiteralPath $configuratorSource) {
    Assert-NoReparse $configuratorTarget
    if (-not ([IO.Path]::GetFullPath($configuratorSource)).Equals($configuratorTarget,[StringComparison]::OrdinalIgnoreCase)) {
        if (-not (Test-Path -LiteralPath $configuratorTarget) -or (Get-FileHash -LiteralPath $configuratorSource).Hash -ne (Get-FileHash -LiteralPath $configuratorTarget).Hash) { Copy-Item -LiteralPath $configuratorSource -Destination $configuratorTarget -Force }
    }
    $startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'ProBackUp Server'
    Assert-NoReparse $startMenu
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $linkPath = Join-Path $startMenu 'Configurar ProBackUp Server.lnk'
    Assert-NoReparse $linkPath
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $configuratorTarget
    $link.WorkingDirectory = $resolvedTarget
    $link.IconLocation = $configuratorTarget + ',0'
    $link.Save()
}
if ($PrepareOnly) { Write-Host 'Servicio preparado para configuracion grafica. No se inicio.'; return }
# The console wizard requests secrets interactively; none are command-line arguments.
if (-not (Test-Path -LiteralPath "$env:ProgramData\ProBackUpServer\server.json")) {
    if (Test-Path -LiteralPath $configuratorTarget) { Start-Process -FilePath $configuratorTarget -Wait }
    else { & $targetExe --configure }
    if (-not (Test-Path -LiteralPath "$env:ProgramData\ProBackUpServer\server.json")) { throw 'Configuración incompleta. El servicio queda detenido; corrija LDAPS/certificados y ejecute --configure.' }
}
$config = Get-Content -LiteralPath "$env:ProgramData\ProBackUpServer\server.json" -Raw | ConvertFrom-Json
# Give only the service identity modify access to its data; admins and SYSTEM retain full control.
$dataAcl = New-Object System.Security.AccessControl.DirectorySecurity
$dataAcl.SetAccessRuleProtection($true,$false)
foreach ($sidValue in @('S-1-5-18','S-1-5-32-544')) {
    $sid = New-Object System.Security.Principal.SecurityIdentifier($sidValue)
    $dataAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
$serviceSid = (New-Object System.Security.Principal.NTAccount('NT SERVICE\ProBackUpServer')).Translate([System.Security.Principal.SecurityIdentifier])
$dataAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($serviceSid,'Modify','ContainerInherit,ObjectInherit','None','Allow')))
$binaryAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($serviceSid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $resolvedTarget -AclObject $binaryAcl
if (-not [System.Diagnostics.EventLog]::SourceExists('ProBackUp.Server')) { New-EventLog -LogName Application -Source 'ProBackUp.Server' }
Set-Acl -LiteralPath "$env:ProgramData\ProBackUpServer" -AclObject $dataAcl
$ruleName = 'ProBackUpServer-HTTPS'
$rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if (-not $rule) { $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue }
if (-not $rule) { $rule = Get-NetFirewallRule -DisplayName 'ProBackUp Server HTTPS (red interna)' -ErrorAction SilentlyContinue }
if (@($rule).Count -gt 1) { throw 'Hay varias reglas de ProBackUp. Revise duplicados antes de continuar.' }
if ($rule) { Set-NetFirewallRule -InputObject $rule -NewDisplayName $ruleName -Enabled True -Profile Domain,Private -Direction Inbound -Action Allow; $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $config.Port; $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $ClientSubnets }
else { New-NetFirewallRule -Name $ruleName -DisplayName $ruleName -Program $targetExe -Direction Inbound -Action Allow -Protocol TCP -LocalPort $config.Port -Profile Domain,Private -RemoteAddress $ClientSubnets | Out-Null }
Start-Service ProBackUpServer
(Get-Service ProBackUpServer).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Panel: https://$($config.PublicHost):$($config.Port)/"
Write-Host 'Use HTTPS con la CA interna confiable. Autentique el administrador LDAP y cree una clave para cada cliente.'
