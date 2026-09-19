[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '../.tools/ffmpeg'),
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content (Join-Path $PSScriptRoot 'ffmpeg-version.json') -Raw | ConvertFrom-Json
$destinationPath = [IO.Path]::GetFullPath($Destination)
$marker = Join-Path $destinationPath 'version.txt'
if ((Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq "$($manifest.version):$($manifest.sha256)") -and
    (Test-Path (Join-Path $destinationPath 'ffmpeg.exe')) -and (Test-Path (Join-Path $destinationPath 'ffprobe.exe'))) {
    Write-Host "Using cached FFmpeg $($manifest.version)"
    return
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('clip-ffmpeg-' + [Guid]::NewGuid().ToString('N'))
New-Item $temporary -ItemType Directory | Out-Null
try {
    if ($ArchivePath) {
        $archive = [IO.Path]::GetFullPath($ArchivePath)
    } else {
        $archive = Join-Path $temporary 'ffmpeg.zip'
        Invoke-WebRequest -Uri $manifest.url -OutFile $archive -MaximumRetryCount 3
    }
    $actualHash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.sha256) { throw "FFmpeg SHA-256 mismatch: $actualHash" }
    Expand-Archive $archive -DestinationPath (Join-Path $temporary 'expanded')
    $binary = Get-ChildItem (Join-Path $temporary 'expanded') -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    if (-not $binary) { throw 'FFmpeg executable not found in archive.' }
    $distribution = $binary.Directory.Parent.FullName
    New-Item $destinationPath -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $binary.Directory.FullName 'ffmpeg.exe') $destinationPath
    Copy-Item (Join-Path $binary.Directory.FullName 'ffprobe.exe') $destinationPath
    Copy-Item (Join-Path $distribution 'LICENSE') (Join-Path $destinationPath 'LICENSE-FFmpeg.txt')
    Copy-Item (Join-Path $distribution 'README.txt') (Join-Path $destinationPath 'README-FFmpeg.txt')
    "$($manifest.version):$($manifest.sha256)" | Set-Content $marker
    Write-Host "Installed verified FFmpeg $($manifest.version)"
}
finally {
    # Only this invocation's GUID-named temporary directory is removed.
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
