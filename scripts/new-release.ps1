<#
.SYNOPSIS
    Cuts a release: promotes the Unreleased notes to a version, commits
    and tags.

.DESCRIPTION
    Does the bookkeeping that is easy to forget and that the release
    workflow checks for:

      * moves everything under "## [Unreleased]" to "## [x.y.z] - date"
      * opens a fresh, empty Unreleased section
      * rewrites the comparison links at the bottom of the changelog
      * commits the changelog and creates the vx.y.z tag

    It does not push. Nothing reaches GitHub, and no release is
    published, until you push the tag yourself.

.EXAMPLE
    pwsh scripts/new-release.ps1 -Version 1.0.0
    git push origin main --follow-tags
#>
[CmdletBinding()]
param(
    # The version to release, without a leading v (e.g. 1.2.3).
    [Parameter(Mandatory = $true)]
    [string] $Version,

    # Edit the changelog but leave committing and tagging to you.
    [switch] $NoCommit,

    # Proceed even with other uncommitted changes in the tree.
    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'

function Fail([string] $message) {
    Write-Host "error: $message" -ForegroundColor Red
    exit 1
}

# --- Checks -----------------------------------------------------------

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    Fail "'$Version' is not a semantic version like 1.2.3 or 1.2.3-beta.1."
}

$tag = "v$Version"

if (-not (Test-Path $changelogPath)) {
    Fail "CHANGELOG.md not found at $changelogPath."
}

if (git tag --list $tag) {
    Fail "Tag $tag already exists. Releases are immutable; pick the next version."
}

if (-not $AllowDirty) {
    $dirty = @(git status --porcelain | Where-Object { $_ -notmatch 'CHANGELOG\.md$' })

    if ($dirty.Count -gt 0) {
        Write-Host "Uncommitted changes:" -ForegroundColor Yellow
        $dirty | ForEach-Object { Write-Host "  $_" }
        Fail "Commit or stash these first, or pass -AllowDirty."
    }
}

# --- Split the changelog into body and link definitions ---------------

$lines = [System.Collections.Generic.List[string]](Get-Content $changelogPath)

$firstLink = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\[[^\]]+\]:\s*http') { $firstLink = $i; break }
}

if ($firstLink -lt 0) {
    $body = $lines
    $links = @()
} else {
    $body = $lines[0..($firstLink - 1)]
    $links = $lines[$firstLink..($lines.Count - 1)] | Where-Object { $_.Trim() -ne '' }
}

# --- Locate the Unreleased section ------------------------------------

$unreleasedAt = -1
for ($i = 0; $i -lt $body.Count; $i++) {
    if ($body[$i] -match '^##\s*\[Unreleased\]') { $unreleasedAt = $i; break }
}

if ($unreleasedAt -lt 0) {
    Fail "CHANGELOG.md has no '## [Unreleased]' section to release."
}

$nextHeadingAt = $body.Count
for ($i = $unreleasedAt + 1; $i -lt $body.Count; $i++) {
    if ($body[$i] -match '^##\s*\[') { $nextHeadingAt = $i; break }
}

$content = @($body[($unreleasedAt + 1)..($nextHeadingAt - 1)])

if (($content -join '').Trim() -eq '') {
    Fail "The Unreleased section is empty. Write down what changed before releasing."
}

# --- Previous version, for the comparison link ------------------------

$previous = $null
for ($i = $nextHeadingAt; $i -lt $body.Count; $i++) {
    if ($body[$i] -match '^##\s*\[(?<v>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)\]') {
        $previous = $Matches['v']
        break
    }
}

$remote = (git config --get remote.origin.url) -replace '\.git$', '' -replace '^git@github\.com:', 'https://github.com/'

# --- Rebuild ----------------------------------------------------------

$today = (Get-Date).ToString('yyyy-MM-dd')

$rebuilt = [System.Collections.Generic.List[string]]::new()

if ($unreleasedAt -gt 0) {
    $rebuilt.AddRange([string[]] @($body[0..($unreleasedAt - 1)]))
}

$rebuilt.Add('## [Unreleased]')
$rebuilt.Add('')
$rebuilt.Add("## [$Version] - $today")
$rebuilt.AddRange([string[]] $content)

if ($nextHeadingAt -lt $body.Count) {
    $rebuilt.AddRange([string[]] @($body[$nextHeadingAt..($body.Count - 1)]))
}

# Trailing blank line before the link block.
while ($rebuilt.Count -gt 0 -and $rebuilt[$rebuilt.Count - 1].Trim() -eq '') {
    $rebuilt.RemoveAt($rebuilt.Count - 1)
}
$rebuilt.Add('')

$newLinks = [System.Collections.Generic.List[string]]::new()
$newLinks.Add("[Unreleased]: $remote/compare/$tag...HEAD")

if ($previous) {
    $newLinks.Add("[$Version]: $remote/compare/v$previous...$tag")
} else {
    $newLinks.Add("[$Version]: $remote/releases/tag/$tag")
}

foreach ($link in $links) {
    if ($link -notmatch '^\[(Unreleased|' + [regex]::Escape($Version) + ')\]:') {
        $newLinks.Add($link)
    }
}

$rebuilt.AddRange([string[]] $newLinks)

Set-Content $changelogPath ($rebuilt -join "`n") -NoNewline -Encoding utf8
Add-Content $changelogPath "`n" -NoNewline -Encoding utf8

Write-Host "CHANGELOG.md: Unreleased -> [$Version] - $today" -ForegroundColor Green
Write-Host ""
Write-Host "Release notes for ${tag}:" -ForegroundColor Cyan
$content | Where-Object { $_.Trim() -ne '' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""

# --- Commit and tag ---------------------------------------------------

if ($NoCommit) {
    Write-Host "Left uncommitted (-NoCommit). Next:" -ForegroundColor Yellow
    Write-Host "  git add CHANGELOG.md"
    Write-Host "  git commit -m `"Release $Version`""
    Write-Host "  git tag $tag"
    Write-Host "  git push origin HEAD --follow-tags"
    exit 0
}

git add -- $changelogPath
if ($LASTEXITCODE -ne 0) { Fail "git add failed." }

git commit -m "Release $Version"
if ($LASTEXITCODE -ne 0) { Fail "git commit failed." }

git tag $tag
if ($LASTEXITCODE -ne 0) { Fail "git tag failed." }

Write-Host "Committed and tagged $tag. Nothing has been pushed." -ForegroundColor Green
Write-Host ""
Write-Host "To publish the release:" -ForegroundColor Cyan
Write-Host "  git push origin HEAD --follow-tags"
