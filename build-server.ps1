param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& dotnet publish ProBackUp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o artifacts/server
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del servidor.' }
if (-not $SkipTests) {
    $localRoot = Join-Path $env:TEMP ('ProBackUp-server-launch-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $localRoot | Out-Null
    $localExe = Join-Path $localRoot 'ProBackUp.Server.exe'
    try {
        Copy-Item -LiteralPath artifacts/server/ProBackUp.Server.exe -Destination $localExe
        $report = Join-Path $PSScriptRoot 'docs/server-test-results.txt'
        $process = Start-Process -FilePath $localExe -ArgumentList '--self-test', ('"' + $report + '"') -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw (Get-Content -LiteralPath $report -Raw) }
    } finally { if (Test-Path -LiteralPath $localExe) { Remove-Item -LiteralPath $localExe }; Remove-Item -LiteralPath $localRoot }
    Get-Content docs/server-test-results.txt
}
& dotnet publish ProBackUp.Server.Config -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o artifacts/server-config
if ($LASTEXITCODE -ne 0) { throw 'Fallo al compilar configurador grafico.' }
if (-not $SkipTests) {
    $configTestRoot = Join-Path $env:TEMP ('ProBackUp-config-launch-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $configTestRoot | Out-Null
    $configTestExe = Join-Path $configTestRoot 'ProBackUp.Server.Config.exe'
    try {
        Copy-Item -LiteralPath artifacts/server-config/ProBackUp.Server.Config.exe -Destination $configTestExe
        $configReport = Join-Path $PSScriptRoot 'docs/server-config-test-results.txt'
        $configProcess = Start-Process -FilePath $configTestExe -ArgumentList '--self-test', ('"' + $configReport + '"') -WindowStyle Hidden -Wait -PassThru
        if ($configProcess.ExitCode -ne 0) { throw (Get-Content -LiteralPath $configReport -Raw) }
        Get-Content -LiteralPath $configReport
    } finally { if (Test-Path -LiteralPath $configTestExe) { Remove-Item -LiteralPath $configTestExe }; Remove-Item -LiteralPath $configTestRoot }
}
New-Item -ItemType Directory -Force dist/server | Out-Null
Copy-Item -LiteralPath artifacts/server/ProBackUp.Server.exe,artifacts/server-config/ProBackUp.Server.Config.exe,ProBackUp.Server/Install-ProBackUpServer.ps1,docs/servidor-central.md,LICENSE.txt,THIRD-PARTY-NOTICES.txt -Destination dist/server -Force
Get-FileHash -LiteralPath dist/server/ProBackUp.Server.exe,dist/server/ProBackUp.Server.Config.exe,dist/server/Install-ProBackUpServer.ps1,dist/server/servidor-central.md -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content dist/server/SHA256SUMS.txt -Encoding ascii
Compress-Archive -Path dist/server/* -DestinationPath dist/ProBackUp-Server.zip -Force
Get-FileHash -LiteralPath dist/ProBackUp-Server.zip -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ProBackUp-Server.zip' } | Set-Content dist/ProBackUp-Server.sha256 -Encoding ascii
Write-Output 'Servidor: dist/ProBackUp-Server.zip'
