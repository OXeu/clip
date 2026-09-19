[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/Clip-win-x64'), [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'src/Clip.Desktop/Clip.Desktop.csproj'
$outputPath = [IO.Path]::GetFullPath($Output)
& (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
$publishArgs = @('publish', $project, '-c', 'Release', '-p:PublishProfile=win-x64', '-o', $outputPath)
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
$ffmpegOutput = Join-Path $outputPath 'ffmpeg'
New-Item $ffmpegOutput -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo '.tools/ffmpeg/*') $ffmpegOutput -Force
Copy-Item (Join-Path $repo 'README.md') $outputPath -Force
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $outputPath -Force
Write-Host "Published: $outputPath"
