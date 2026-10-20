#Requires -Version 5.1
#Requires -RunAsAdministrator
param([string]$Url='https://probackup.coopeagri.local/')
. (Join-Path $PSScriptRoot 'Comun.ps1')
$next=$null
if(-not [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$next) -or $next.Scheme -ne 'https' -or $next.HostNameType -ne [UriHostNameType]::Dns -or $next.UserInfo -or $next.AbsolutePath -ne '/' -or $next.Query -or $next.Fragment){throw 'Indica una URL HTTPS con nombre DNS y sin rutas ni parametros.'}
if(@(Get-CimInstance Win32_Process -Filter "Name='ProBackUp.exe'" | Where-Object {$_.SessionId -ne 0}).Count -gt 0){throw 'Sal de ProBackUp desde la bandeja antes de cambiar la URL. El servicio puede seguir activo.'}
$root=Join-Path $env:ProgramData 'ProBackUp'
$path=Join-Path $root 'settings.json'
Assert-NoLinks $path
$gate=New-Object IO.FileStream((Join-Path $root 'operation.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $settings=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $old=[Uri]$settings.Central.Url
    if($old.Scheme -ne 'https' -or -not $old.DnsSafeHost.Equals($next.DnsSafeHost,[StringComparison]::OrdinalIgnoreCase)){throw 'Este ayudante solo cambia el puerto del mismo nombre DNS. Para otro servidor utiliza la interfaz y una clave nueva.'}
    Add-Type -AssemblyName System.Net.Http
    $handler=New-Object Net.Http.HttpClientHandler
    $handler.UseProxy=$false;$handler.AllowAutoRedirect=$false;$handler.CheckCertificateRevocationList=$true
    $client=New-Object Net.Http.HttpClient($handler)
    try {
        $client.Timeout=[TimeSpan]::FromSeconds(15)
        $response=$client.GetAsync((New-Object Uri($next,'/api/csrf'))).GetAwaiter().GetResult()
        try {if(-not $response.IsSuccessStatusCode){throw 'La URL nueva no responde como ProBackUp por HTTPS.'};$reply=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json;if(-not $reply.token){throw 'Respuesta inesperada de la URL nueva.'}} finally {$response.Dispose()}
    } finally {$client.Dispose()}
    $settings.Central.Url=$next.GetLeftPart([UriPartial]::Authority)+'/'
    $temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $backup=$path+'.before-iis-'+[guid]::NewGuid().ToString('N')+'.bak'
    try {[IO.File]::WriteAllText($temp,($settings | ConvertTo-Json -Depth 40),(New-Object Text.UTF8Encoding($false)));[IO.File]::Replace($temp,$path,$backup)} finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp}}
    Write-Host ('URL cambiada a '+$settings.Central.Url+'. Clave cifrada, tareas e historial conservados.') -ForegroundColor Green
    Write-Host 'El servicio recogera el cambio en su siguiente consulta.'
} finally {$gate.Dispose()}
