param([switch]$SkipTests)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
if($SkipTests){& (Join-Path $PSScriptRoot 'build-server.ps1') -SkipTests}else{& (Join-Path $PSScriptRoot 'build-server.ps1')}
if($LASTEXITCODE -ne 0){throw 'Fallo al construir backend.'}
& dotnet publish ProBackUp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o artifacts/iis-app
if($LASTEXITCODE -ne 0){throw 'Fallo al publicar para IIS.'}
$package=Join-Path $PSScriptRoot 'IISProBackUpServer'
New-Item -ItemType Directory -Path (Join-Path $package 'sitio'),(Join-Path $package 'Backend') -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'artifacts\iis-app\*') -Destination (Join-Path $package 'sitio') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $package 'web.config.ejemplo') -Destination (Join-Path $package 'sitio\web.config') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'dist\server\*') -Destination (Join-Path $package 'Backend') -Force
Copy-Item -LiteralPath LICENSE.txt,THIRD-PARTY-NOTICES.txt -Destination $package -Force
$privateCertificates=(Join-Path $package 'Certificados')+'\'
$distributionFiles=Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object {-not $_.FullName.StartsWith($privateCertificates,[StringComparison]::OrdinalIgnoreCase)}
$unexpected=$distributionFiles | Where-Object {$_.Extension -in '.pfx','.p12' -or $_.Name -in 'server.json','settings.json','reports.db'}
if($unexpected){throw 'El paquete no debe contener certificados privados, contrasenas ni base de reportes.'}
foreach($script in Get-ChildItem -LiteralPath $package -Filter '*.ps1'){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors)
    if($errors){throw ($errors | Out-String)}
}
$xml=[xml](Get-Content -LiteralPath (Join-Path $package 'sitio\web.config') -Raw)
if($xml.configuration.location.'system.webServer'.aspNetCore.hostingModel -ne 'inprocess'){throw 'HostingModel incorrecto.'}
$files=$distributionFiles | Where-Object {$_.FullName -ne (Join-Path $package 'SHA256SUMS.txt')}
$files | Sort-Object FullName | ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash+'  '+$_.FullName.Substring($package.Length+1)} | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding ascii
$zipPath=Join-Path $PSScriptRoot 'dist\IISProBackUpServer.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream=[IO.File]::Open($zipPath,[IO.FileMode]::Create)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($file in @($files)+(Get-Item (Join-Path $package 'SHA256SUMS.txt'))){
        $entry='IISProBackUpServer/'+$file.FullName.Substring($package.Length+1).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal)
    }
} finally {$zip.Dispose();$stream.Dispose()}
Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'dist\IISProBackUpServer.zip') -Algorithm SHA256 | ForEach-Object {$_.Hash+'  IISProBackUpServer.zip'} | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'dist\IISProBackUpServer.sha256') -Encoding ascii
Write-Output ('Paquete IIS: '+$package)
Write-Output 'ZIP: dist\IISProBackUpServer.zip'
