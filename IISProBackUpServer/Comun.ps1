# Shared validation. No configuration or secrets are copied into the IIS public folder.
$ErrorActionPreference = 'Stop'
function Assert-NoLinks([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "No se admiten rutas redirigidas: $part" }
    }
}
function Assert-InstallRoot([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $prefix = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $full -eq $prefix.TrimEnd('\')) { throw 'La instalacion IIS debe estar dentro de una subcarpeta de Program Files.' }
    Assert-NoLinks $full
    return $full
}
function Get-OwnService {
    $service = Get-CimInstance Win32_Service -Filter "Name='ProBackUpServer'"
    $exe = Join-Path $env:ProgramFiles 'ProBackUpServer\ProBackUp.Server.exe'
    if (-not $service -or $service.PathName -ne ('"' + $exe + '"')) { throw 'Primero instala y configura Backend\Install-ProBackUpServer.ps1 en este equipo.' }
    Assert-NoLinks $exe
    if ((Get-Content -LiteralPath (Join-Path (Split-Path $exe) '.probackup-server') -Raw).Trim() -ne 'ProBackUpServer') { throw 'El servicio no pertenece a ProBackUp.' }
    if ([version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion -lt [version]'3.4.0.0') {throw 'Actualiza primero el servicio y configurador con Backend\Install-ProBackUpServer.ps1 de este paquete.'}
    return $service
}
function Assert-Package {
    foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Lista de integridad invalida.' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
        if (-not $path.StartsWith(([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta de paquete invalida.' }
        Assert-NoLinks $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw "Archivo alterado o incompleto: $relative" }
    }
}
function Set-ServiceStartup([string]$Mode, [bool]$Delayed) {
    $kind = switch ($Mode) { 'Auto' {'Automatic'} 'Manual' {'Manual'} 'Disabled' {'Disabled'} default {throw 'Inicio de servicio invalido.'} }
    Set-Service -Name ProBackUpServer -StartupType $kind
    if ($Mode -eq 'Auto' -and $Delayed) { & sc.exe config ProBackUpServer start= delayed-auto | Out-Null; if ($LASTEXITCODE -ne 0) {throw 'No se pudo restablecer el inicio diferido.'} }
}
function Test-LocalHttps([string]$HostName,[int]$Port) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) {
        if (-not ('ProBackUpLocalTlsProbe' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
public static class ProBackUpLocalTlsProbe {
 public static string Read(string host, int port) {
  using (var tcp = new TcpClient()) {
   var connect = tcp.BeginConnect("127.0.0.1", port, null, null);
   try { if (!connect.AsyncWaitHandle.WaitOne(10000)) throw new TimeoutException("Conexion HTTPS agotada."); tcp.EndConnect(connect); }
   finally { connect.AsyncWaitHandle.Close(); }
   using (var tls = new SslStream(tcp.GetStream(), false)) {
    tls.ReadTimeout = 15000; tls.WriteTimeout = 15000;
    tls.AuthenticateAsClient(host, new X509CertificateCollection(), SslProtocols.Tls12, true);
    var request = Encoding.ASCII.GetBytes("GET /api/csrf HTTP/1.0\r\nHost: " + host + ":" + port + "\r\nConnection: close\r\n\r\n");
    tls.Write(request, 0, request.Length);
    using (var output = new MemoryStream()) {
     var buffer = new byte[4096]; int count;
     var deadline = DateTime.UtcNow.AddSeconds(45);
     while ((count = tls.Read(buffer, 0, buffer.Length)) > 0) {
      output.Write(buffer, 0, count);
      if (output.Length > 1048576 || DateTime.UtcNow > deadline) throw new IOException("Respuesta HTTPS excedida.");
     }
     return Encoding.UTF8.GetString(output.ToArray());
    }
   }
  }
 }
}
"@
        }
        $raw = [ProBackUpLocalTlsProbe]::Read($HostName, $Port)
        $parts = $raw -split "`r`n`r`n", 2
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^HTTP/1\.[01] 200 ' -or $parts[0] -match '(?im)^Transfer-Encoding:') { throw 'Respuesta HTTPS local inesperada. Revise sitio, certificado y eventos de IIS.' }
        $reply = $parts[1] | ConvertFrom-Json
        if (-not $reply.token) { throw 'IIS no devolvio la aplicacion ProBackUp.' }
        return
    }
    $temp = Join-Path $env:TEMP ('ProBackUp-IIS-health-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        & $curl.Source --noproxy '*' --fail --silent --show-error --connect-timeout 10 --max-time 45 --resolve ($HostName + ':' + $Port + ':127.0.0.1') --output $temp ('https://' + $HostName + ':' + $Port + '/api/csrf')
        if ($LASTEXITCODE -ne 0) {throw 'Fallo HTTPS local en IIS. Revise certificado, Hosting Bundle, permisos y eventos de AspNetCoreModuleV2.'}
        $reply = Get-Content -LiteralPath $temp -Raw | ConvertFrom-Json
        if (-not $reply.token) {throw 'IIS no devolvio la aplicacion ProBackUp.'}
    } finally { if (Test-Path -LiteralPath $temp) {Remove-Item -LiteralPath $temp} }
}

function Wait-PoolStopped([string]$Name) {
    for($i=0;$i -lt 45;$i++){if((Get-WebAppPoolState -Name $Name).Value -eq 'Stopped'){return};Start-Sleep -Seconds 1}
    throw 'El pool no se detuvo. No se inicia una segunda instancia.'
}
