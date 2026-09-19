[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/Clip-win-x64'), [switch]$NoRestore, [string]$Version)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'src/Clip.Desktop/Clip.Desktop.csproj'
$outputPath = [IO.Path]::GetFullPath($Output)
& (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
$publishArgs = @('publish', $project, '-c', 'Release', '-p:PublishProfile=win-x64', '-o', $outputPath)
if (-not $Version -and $env:GITHUB_REF_TYPE -eq 'tag') { $Version = $env:GITHUB_REF_NAME -replace '^[vV]', '' }
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a semantic version, e.g. 1.2.3 or 1.2.3-dev.1.' }
    $publishArgs += "-p:Version=$Version"
}
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
$ffmpegOutput = Join-Path $outputPath 'ffmpeg'
New-Item $ffmpegOutput -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo '.tools/ffmpeg/*') $ffmpegOutput -Force
Copy-Item (Join-Path $repo 'README.md') $outputPath -Force
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $outputPath -Force
Copy-Item (Join-Path $PSScriptRoot 'Start-Clip-Diagnostics.cmd') $outputPath -Force
Copy-Item (Join-Path $PSScriptRoot 'Diagnose-Startup.ps1') $outputPath -Force
$licenseOutput = Join-Path $outputPath 'licenses'
New-Item $licenseOutput -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'src/Clip.Desktop/Assets/RemixIcon/LICENSE.txt') (Join-Path $licenseOutput 'RemixIcon.txt') -Force
Write-Host "Published: $outputPath"
