[CmdletBinding()]
param([Parameter(Mandatory)][string]$CaptureDirectory)
$ErrorActionPreference = 'Stop'
$captures = (Resolve-Path -LiteralPath $CaptureDirectory).Path
$updater = Join-Path $PSScriptRoot 'Update-ReadmeScreenshots.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('clip-readme-test-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $temporary 'checkout'
$remote = Join-Path $temporary 'remote.git'
$bad = Join-Path $temporary 'invalid-captures'
New-Item -ItemType Directory -Path $repo, $bad | Out-Null

function Invoke-TestGit([string[]]$GitArguments) {
    $output = & git -C $repo @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "Test git command failed: $($GitArguments[0])" }
    return $output
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected failure: $Pattern"
}
try {
    Invoke-TestGit -GitArguments @('init', '--initial-branch=screenshots') | Out-Null
    Invoke-TestGit -GitArguments @('config', 'user.name', 'Screenshot test') | Out-Null
    Invoke-TestGit -GitArguments @('config', 'user.email', 'screenshot-test@example.invalid') | Out-Null
    Invoke-TestGit -GitArguments @('init', '--bare', $remote) | Out-Null
    Invoke-TestGit -GitArguments @('remote', 'add', 'origin', $remote) | Out-Null
    $readmePath = Join-Path $repo 'README.md'
    $initialReadme = "# Screenshot fixture`n`n<!-- clip-ui-screenshots: initial -->`n<picture>unchanged presentation</picture>`n"
    [IO.File]::WriteAllText($readmePath, $initialReadme)
    [IO.File]::WriteAllText((Join-Path $repo 'untouched.txt'), 'Keep this content.')
    New-Item -ItemType Directory -Path (Join-Path $repo 'docs/images') | Out-Null
    $tinyPng = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZAAAAABJRU5ErkJggg==')
    foreach ($name in @('screenshot.png', 'screenshot-dark.png')) {
        [IO.File]::WriteAllBytes((Join-Path $repo "docs/images/$name"), $tinyPng)
    }
    Invoke-TestGit -GitArguments @('add', '.') | Out-Null
    Invoke-TestGit -GitArguments @('commit', '-m', 'Initial fixture') | Out-Null
    Invoke-TestGit -GitArguments @('push', '-u', 'origin', 'screenshots') | Out-Null
    $source = Invoke-TestGit -GitArguments @('rev-parse', 'HEAD')

    & $updater -CaptureDirectory $captures -RepositoryRoot $repo -SourceCommit $source -RunId 1 -Branch screenshots -Publish
    $published = Invoke-TestGit -GitArguments @('rev-parse', 'HEAD')
    Require ($published -ne $source) 'Publishing did not create a screenshot commit.'
    Require ((Invoke-TestGit -GitArguments @('rev-parse', 'HEAD^')) -eq $source) 'The screenshot commit is not based on the tested code.'
    Require (((Invoke-TestGit -GitArguments @('ls-remote', 'origin', 'refs/heads/screenshots')) -split '\s+')[0] -eq $published) 'The screenshot commit was not pushed.'
    $paths = @(Invoke-TestGit -GitArguments @('diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD'))
    Require (($paths -join ',') -eq 'README.md,docs/images/screenshot-dark.png,docs/images/screenshot.png') 'The publisher changed unrelated paths.'
    Require ([IO.File]::ReadAllText($readmePath).Contains('<picture>unchanged presentation</picture>')) 'The README presentation was modified.'
    Require ([IO.File]::ReadAllText($readmePath).Contains("commit=$source")) 'The README provenance does not point to the tested commit.'
    Require ((Get-FileHash (Join-Path $repo 'docs/images/screenshot.png')).Hash -eq
        (Get-FileHash (Join-Path $captures 'smoke-theme-light-editor.png')).Hash) 'The screenshot was altered instead of copied.'
    Write-Host 'PASS successful smoke captures publish exactly the README and two original PNG files'

    $savedReadme = [IO.File]::ReadAllText($readmePath)
    & $updater -CaptureDirectory $captures -RepositoryRoot $repo -SourceCommit $published -RunId 2 -Branch screenshots -Publish
    Require ((Invoke-TestGit -GitArguments @('rev-parse', 'HEAD')) -eq $published) 'Unchanged images created another commit.'
    Require ([IO.File]::ReadAllText($readmePath) -eq $savedReadme) 'Unchanged images rewrote provenance.'
    Write-Host 'PASS unchanged screenshots do not create commit churn'

    Copy-Item -LiteralPath (Join-Path $captures 'smoke-theme-light-editor.png') -Destination $bad
    Copy-Item -LiteralPath (Join-Path $captures 'smoke-theme-dark-editor.png') -Destination $bad
    Expect-Failure { & $updater -CaptureDirectory $bad -RepositoryRoot $repo -SourceCommit $published -RunId 3 } 'success marker'
    Write-Host 'PASS artifacts without successful UI verification are rejected'
    [IO.File]::WriteAllText((Join-Path $bad 'smoke-success.txt'), 'passed')
    [IO.File]::WriteAllBytes((Join-Path $bad 'smoke-theme-light-editor.png'), $tinyPng)
    Expect-Failure { & $updater -CaptureDirectory $bad -RepositoryRoot $repo -SourceCommit $published -RunId 3 } 'resolution too low'
    Require (-not (Invoke-TestGit -GitArguments @('status', '--porcelain'))) 'Invalid captures partially changed the checkout.'
    Write-Host 'PASS low-resolution screenshots are rejected before either theme is replaced'

    Copy-Item -LiteralPath (Join-Path $captures 'smoke-theme-light-editor.png') -Destination $bad
    Remove-Item -LiteralPath (Join-Path $bad 'smoke-theme-dark-editor.png')
    Expect-Failure { & $updater -CaptureDirectory $bad -RepositoryRoot $repo -SourceCommit $published -RunId 3 } 'smoke-theme-dark-editor'
    Require (-not (Invoke-TestGit -GitArguments @('status', '--porcelain'))) 'A missing theme partially changed the checkout.'
    Write-Host 'PASS both themes must be present before any README files change'

    Invoke-TestGit -GitArguments @('checkout', '--detach', $source) | Out-Null
    & $updater -CaptureDirectory $captures -RepositoryRoot $repo -SourceCommit $source -RunId 4 -Branch screenshots -Publish
    Require ((Invoke-TestGit -GitArguments @('rev-parse', 'HEAD')) -eq $source) 'A stale build created a local commit.'
    Require (-not (Invoke-TestGit -GitArguments @('status', '--porcelain'))) 'A stale build modified the checkout.'
    Require (((Invoke-TestGit -GitArguments @('ls-remote', 'origin', 'refs/heads/screenshots')) -split '\s+')[0] -eq $published) 'A stale build overwrote the newer branch.'
    Write-Host 'PASS stale builds leave newer branch contents intact'

    [IO.File]::WriteAllText($readmePath, $initialReadme + 'User edit.')
    Expect-Failure { & $updater -CaptureDirectory $captures -RepositoryRoot $repo -SourceCommit $source -RunId 5 -Branch screenshots -Publish } 'clean checkout'
    Require ([IO.File]::ReadAllText($readmePath).EndsWith('User edit.')) 'Existing changes were overwritten.'
    Write-Host 'PASS publishing refuses an unclean checkout without changing user edits'
}
finally {
    # 仅清理本次测试创建的 GUID 临时目录。
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
