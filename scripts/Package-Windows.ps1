[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '../artifacts/Clip-win-x64'),
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/Clip-win-x64.zip'),
    [string]$SevenZip,
    [ValidateSet('zip', '7z')][string]$Format = 'zip'
)
$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [IO.Path]::GetFullPath($Output)
if ([IO.Path]::GetExtension($outputPath) -ne ".$Format") { throw 'Output extension must match Format.' }
if (Test-Path -LiteralPath $outputPath) { throw "Archive already exists; choose a new filename: $outputPath" }
if (-not $SevenZip) {
    $command = Get-Command 7z, 7zz -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $SevenZip = $command.Source }
    elseif ($IsWindows -and (Test-Path "$env:ProgramFiles/7-Zip/7z.exe")) { $SevenZip = "$env:ProgramFiles/7-Zip/7z.exe" }
    else { throw '7-Zip is required for packaging. Install it or pass -SevenZip with its executable path.' }
}
foreach ($required in @('Clip.exe', 'LICENSE', 'ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe', 'ffmpeg/LICENSE-FFmpeg.txt', 'ffmpeg/README-FFmpeg.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePath $required) -PathType Leaf)) { throw "Missing published file: $required" }
}

# Capture the original hashes before compression; the extracted archive must match these exact bytes.
$files = @(Get-ChildItem -LiteralPath $sourcePath -File -Recurse |
    Where-Object { $_.Name -notlike 'smoke-*' -and $_.Extension -ne '.pdb' } |
    Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($sourcePath, $_.FullName).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$parent = [IO.Path]::GetDirectoryName($outputPath)
New-Item -Path $parent -ItemType Directory -Force | Out-Null
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('clip-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -Path $temporary -ItemType Directory | Out-Null
$archive = Join-Path $temporary "package.$Format"
$extracted = Join-Path $temporary 'extracted'
try {
    $compression = if ($Format -eq 'zip') { @('-tzip', '-mm=Deflate', '-mx=5', '-mmt=2') }
        else { @('-t7z', '-mx=3', '-md=128m', '-ms=on', '-mmt=2') }
    Push-Location $sourcePath
    try {
        & $SevenZip a @compression $archive -- @($files | ForEach-Object { $_.path })
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
    }
    finally { Pop-Location }
    & $SevenZip t $archive
    if ($LASTEXITCODE -ne 0) { throw '7-Zip archive integrity test failed.' }
    & $SevenZip x $archive "-o$extracted" -y
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
    if (@(Get-ChildItem -LiteralPath $extracted -File -Recurse).Count -ne $files.Count) {
        throw 'Archive file count does not match the published directory.'
    }
    foreach ($file in $files) {
        $path = Join-Path $extracted $file.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing archive entry: $($file.path)" }
        if ((Get-Item -LiteralPath $path).Length -ne $file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Extracted bytes differ from the published file: $($file.path)"
        }
    }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $length = (Get-Item -LiteralPath $archive).Length
    # Do not publish a partially written or unverified archive.
    [IO.File]::Move($archive, $outputPath, $false)
    "$hash  $([IO.Path]::GetFileName($outputPath))" | Set-Content -LiteralPath "$outputPath.sha256" -Encoding ascii
    [ordered]@{ archive = [IO.Path]::GetFileName($outputPath); length = $length; sha256 = $hash; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$outputPath.manifest.json" -Encoding utf8
    Write-Host "Verified $($files.Count) extracted files. Archive: $length bytes; SHA-256: $hash"
    if ($env:GITHUB_STEP_SUMMARY) {
        "Archive: $([IO.Path]::GetFileName($outputPath))`n`nSize: $length bytes`n`nSHA-256: $hash" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}
finally {
    # Only the GUID-named scratch directory created above belongs to this invocation.
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
