[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CaptureDirectory,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$RunId,
    [string]$Branch,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$captures = (Resolve-Path -LiteralPath $CaptureDirectory).Path

function Invoke-Git([string[]]$GitArguments) {
    $output = & git -C $repo @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "Git command failed: $($GitArguments[0])" }
    return $output
}
function Get-RemoteHead {
    $result = Invoke-Git -GitArguments @('ls-remote', '--exit-code', 'origin', "refs/heads/$Branch")
    return ($result -split '\s+')[0]
}
function Read-BigEndian([byte[]]$Bytes, [int]$Offset) {
    return [long]$Bytes[$Offset] * 16777216 + [long]$Bytes[$Offset + 1] * 65536 +
        [long]$Bytes[$Offset + 2] * 256 + [long]$Bytes[$Offset + 3]
}

if ($Publish) {
    if (-not $Branch) { throw 'Publishing requires an explicit branch.' }
    Invoke-Git -GitArguments @('check-ref-format', "refs/heads/$Branch") | Out-Null
    if ((Invoke-Git -GitArguments @('rev-parse', 'HEAD')) -ne $SourceCommit) {
        throw 'The checkout does not match the tested commit.'
    }
    if (Invoke-Git -GitArguments @('status', '--porcelain')) {
        throw 'Publishing requires a clean checkout.'
    }
    if ((Get-RemoteHead) -ne $SourceCommit) {
        Write-Host 'A newer commit is on the branch; skipping screenshots from the older build.'
        return
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $captures 'smoke-success.txt') -PathType Leaf)) {
    throw 'The UI smoke test success marker is missing.'
}
$targets = @('README.md', 'docs/images/screenshot.png', 'docs/images/screenshot-dark.png')
$images = @(foreach ($theme in @('light', 'dark')) {
    $source = Join-Path $captures "smoke-theme-$theme-editor.png"
    $bytes = [IO.File]::ReadAllBytes($source)
    if ($bytes.Length -lt 33 -or [BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A' -or
        [Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ne 'IHDR') {
        throw "Invalid PNG screenshot: $source"
    }
    $width = Read-BigEndian $bytes 16
    $height = Read-BigEndian $bytes 20
    if ($width -lt 3000 -or $height -lt 2000) { throw "Screenshot resolution too low: $theme $width x $height" }
    $relative = if ($theme -eq 'light') { $targets[1] } else { $targets[2] }
    [pscustomobject]@{
        Source = $source
        Target = Join-Path $repo $relative
        Width = $width
        Height = $height
        Hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    }
})
if ($images[0].Width -ne $images[1].Width -or $images[0].Height -ne $images[1].Height) {
    throw 'Light and dark screenshots must have matching dimensions.'
}

$readmePath = Join-Path $repo 'README.md'
$readme = [IO.File]::ReadAllText($readmePath)
$marker = [regex]'(?m)^<!-- (?:clip-ui-screenshots:|3× 高清窗口截图：Windows CI run|实际窗口截图：Windows CI run)[^\r\n]*-->[ \t]*$'
if ($marker.Matches($readme).Count -ne 1) { throw 'README.md must contain exactly one screenshot provenance comment.' }
$changed = @($images | Where-Object {
    -not (Test-Path -LiteralPath $_.Target -PathType Leaf) -or
    (Get-FileHash -LiteralPath $_.Target -Algorithm SHA256).Hash -ne $_.Hash
})
if ($changed.Count -eq 0) {
    Write-Host 'README screenshots are unchanged; no commit is needed.'
    return
}

foreach ($image in $images) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($image.Target)) -Force | Out-Null
    Copy-Item -LiteralPath $image.Source -Destination $image.Target
}
$provenance = "<!-- clip-ui-screenshots: run=$RunId; commit=$SourceCommit; size=$($images[0].Width)x$($images[0].Height); density=3x -->"
[IO.File]::WriteAllText($readmePath, $marker.Replace($readme, $provenance, 1), [Text.UTF8Encoding]::new($false))
Write-Host "Updated README screenshots from smoke test run $RunId ($($images[0].Width) x $($images[0].Height))."
if (-not $Publish) { return }

Invoke-Git -GitArguments (@('add', '--') + $targets) | Out-Null
Invoke-Git -GitArguments (@('-c', 'user.name=github-actions[bot]', '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com',
    'commit', '--only', '-m', 'docs: refresh smoke-test UI screenshots', '--') + $targets)
& git -C $repo push origin "HEAD:refs/heads/$Branch"
if ($LASTEXITCODE -ne 0) {
    if ((Get-RemoteHead) -ne $SourceCommit) {
        Write-Host 'The branch advanced while screenshots were being published; the newer commit was kept.'
        return
    }
    throw 'Unable to publish README screenshots. Check repository write permissions and branch rules.'
}
