param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet falló con código $LASTEXITCODE" }
}
function Run-Test([string]$Executable, [string]$Report) {
    $localTestRoot = Join-Path $env:TEMP ('ProBackUp-launch-test-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $localTestRoot | Out-Null
    $localTestExe = Join-Path $localTestRoot 'ProBackUp.exe'
    try {
        Copy-Item -LiteralPath $Executable -Destination $localTestExe
        $testProcess = Start-Process -FilePath $localTestExe -ArgumentList '--self-test', ('"' + $Report + '"') -WindowStyle Hidden -Wait -PassThru
        if ($testProcess.ExitCode -ne 0) { throw (Get-Content -LiteralPath $Report -Raw) }
        Get-Content -LiteralPath $Report
        $trayReport = Join-Path $PSScriptRoot 'docs/tray-test-results.txt'
        $trayProcess = Start-Process -FilePath $localTestExe -ArgumentList '--tray-test', ('"' + $trayReport + '"') -WindowStyle Hidden -Wait -PassThru
        if ($trayProcess.ExitCode -ne 0) { throw (Get-Content -LiteralPath $trayReport -Raw) }
        Get-Content -LiteralPath $trayReport
        $restoreReport = Join-Path $PSScriptRoot 'docs/restore-test-results.txt'
        $restoreProcess = Start-Process -FilePath $localTestExe -ArgumentList '--restore-test', ('"' + $restoreReport + '"') -WindowStyle Hidden -Wait -PassThru
        if ($restoreProcess.ExitCode -ne 0) { throw (Get-Content -LiteralPath $restoreReport -Raw) }
        Get-Content -LiteralPath $restoreReport
    } finally {
        if (Test-Path -LiteralPath $localTestExe) { Remove-Item -LiteralPath $localTestExe -Force }
        Remove-Item -LiteralPath $localTestRoot
    }
}
New-Item -ItemType Directory -Force artifacts/app,dist,docs | Out-Null
Run-Dotnet -Arguments @('publish','ProBackUp.App','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-o','artifacts/app')
Copy-Item -LiteralPath LEEME.txt,LICENSE.txt,THIRD-PARTY-NOTICES.txt -Destination artifacts/app -Force
if (-not $SkipTests) { Run-Test (Join-Path $PSScriptRoot 'artifacts/app/ProBackUp.exe') (Join-Path $PSScriptRoot 'docs/test-results.txt') }
$payloadFile = Join-Path $PSScriptRoot 'ProBackUp.Setup/payload.zip'
if (Test-Path -LiteralPath $payloadFile) { Remove-Item -LiteralPath $payloadFile -Force }
Compress-Archive -LiteralPath artifacts/app/ProBackUp.exe,artifacts/app/LEEME.txt,artifacts/app/LICENSE.txt,artifacts/app/THIRD-PARTY-NOTICES.txt -DestinationPath $payloadFile -CompressionLevel Optimal
Set-Content -LiteralPath ProBackUp.Setup/payload.sha256 -Value (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash -Encoding ascii
Run-Dotnet -Arguments @('publish','ProBackUp.Setup','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-o','artifacts/setup')
Copy-Item -LiteralPath artifacts/setup/ProBackUp-Setup.exe -Destination dist/ProBackUp-Setup.exe -Force
Copy-Item -LiteralPath artifacts/app/ProBackUp.exe,LEEME.txt -Destination dist -Force
if (-not $SkipTests) {
    # Exercise the same installer assembly and embedded payload without triggering UAC on a build host.
    & dotnet (Join-Path $PSScriptRoot 'ProBackUp.Setup/bin/Release/net10.0-windows/win-x64/ProBackUp-Setup.dll') --self-test (Join-Path $PSScriptRoot 'docs/installer-test-results.txt') (Join-Path $PSScriptRoot 'dist/ProBackUp-Setup.exe')
    if ($LASTEXITCODE -ne 0) { throw (Get-Content -LiteralPath docs/installer-test-results.txt -Raw) }
    Get-Content -LiteralPath docs/installer-test-results.txt
}
Get-FileHash -LiteralPath dist/ProBackUp-Setup.exe,dist/ProBackUp.exe -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + (Split-Path -Leaf $_.Path) } | Set-Content -LiteralPath dist/SHA256SUMS.txt -Encoding ascii
Write-Output 'Instalador: dist/ProBackUp-Setup.exe'
