$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'Clip.exe'
$reportRoot = Join-Path $env:LOCALAPPDATA ('Clip/diagnostics/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -Path $reportRoot -ItemType Directory -Force | Out-Null
$report = Join-Path $reportRoot 'report.txt'
$started = Get-Date
$env:CLIP_LOG_DIR = $reportRoot
$env:DOTNET_HOST_TRACE = '1'
$env:DOTNET_HOST_TRACEFILE = Join-Path $reportRoot 'host-trace.log'
$env:COREHOST_TRACE = '1'
$env:COREHOST_TRACEFILE = $env:DOTNET_HOST_TRACEFILE
try {
    @(
        "Date: $started"
        "Windows: $([Environment]::OSVersion.VersionString)"
        "64-bit OS: $([Environment]::Is64BitOperatingSystem)"
        "Architecture: $env:PROCESSOR_ARCHITECTURE"
        "Executable: $app"
        "Bytes: $((Get-Item -LiteralPath $app).Length)"
        "SHA-256: $((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash)"
    ) | Set-Content -LiteralPath $report -Encoding UTF8
    $process = Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot -PassThru
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited -or $process.MainWindowHandle -ne 0) { break }
    }
    if ($process.HasExited) {
        $process.WaitForExit()
        "Process exited. Exit code: $($process.ExitCode)" | Add-Content -LiteralPath $report
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started.AddSeconds(-2) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match '(?i)Clip\.exe|Clip\.dll' } |
            Select-Object -First 5 TimeCreated, Id, ProviderName, Message | Format-List | Out-String |
            Add-Content -LiteralPath $report
    } else {
        "Process is running. PID: $($process.Id); window handle: $($process.MainWindowHandle)" | Add-Content -LiteralPath $report
    }
}
catch { $_ | Out-String | Add-Content -LiteralPath $report }
Write-Host "Startup report: $report"
Write-Host "Host and application logs: $reportRoot"
Get-Content -LiteralPath $report
Start-Process notepad.exe -ArgumentList ('"' + $report + '"')
