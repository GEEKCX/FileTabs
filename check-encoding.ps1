# ============================================================
# check-encoding.ps1 - XAML / C# file encoding checker
# ------------------------------------------------------------
# This script is intentionally pure ASCII.
# Reason: the machine ANSI code page is 936 (GBK). Windows PowerShell 5.1
# reads script files through that code page, so any non-ASCII character
# inside this file would be mis-decoded before it ever runs.
# Keep every line ASCII-only so the checker itself cannot be corrupted.
#
# Background:
#   Get-Content / Set-Content default to -Encoding Default, which is the
#   ANSI code page (936). Reading a UTF-8 Chinese file with them yields
#   mojibake such as jian-ting-wen, and writing it back destroys the file.
#   This script avoids PowerShell text cmdlets entirely and works on raw
#   bytes with .NET UTF-8 APIs, so it is safe to run anywhere.
#
# Scope:
#   Only files tracked by git are checked (git ls-files), so build output,
#   binaries and untracked scratch files cannot produce false positives.
#   Extension filter: .xaml .cs .csproj .ps1 .md .json .yml .yaml
#
# Checks per file:
#   1. UTF-8 BOM present (repo convention: no BOM)
#   2. Invalid UTF-8 byte sequence
#   3. U+FFFD replacement character present
#   4. GBK mojibake markers present (wrong decode landed on disk)
#   5. In .xaml: a line containing '<' with an odd number of double quotes
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File check-encoding.ps1
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File check-encoding.ps1 -Root D:\Projects\FileTabs
#   ... -Staged    # check the index instead of the working tree
# Exit code: 0 = clean, 1 = problems found
# ============================================================

[CmdletBinding()]
param(
    [string]$Root = (Get-Location).Path,
    [switch]$Staged,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host "Root not found: $Root"
    exit 1
}

$Root = (Resolve-Path -LiteralPath $Root).Path

# ---- collect tracked text files -------------------------------------------
$gitArgs = @('-C', $Root, 'ls-files')
if ($Staged) { $gitArgs += '--cached' }

$tracked = & git @gitArgs 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Not a git repository (or git unavailable): $Root"
    exit 1
}

$textExtensions = @('.xaml', '.cs', '.csproj', '.ps1', '.md', '.json', '.yml', '.yaml', '.gitattributes')

$files = @()
foreach ($rel in $tracked) {
    if ([string]::IsNullOrWhiteSpace($rel)) { continue }
    $ext = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
    if ($textExtensions -notcontains $ext) { continue }
    $full = Join-Path $Root $rel
    if (Test-Path -LiteralPath $full) { $files += $full }
}

if ($files.Count -eq 0) {
    Write-Host "No tracked text files to check under: $Root"
    exit 0
}

# ---- GBK mojibake markers -------------------------------------------------
# Only sequences that do NOT occur in normal Chinese prose are used, so
# ordinary words like gong-zuo / wen-jian / she-zhi are never flagged.
#
# These are the actual artefacts observed when UTF-8 Chinese was decoded as
# GBK on this machine: "jiu..." style garbage such as the one that replaced
# "settings" in MainWindow.xaml.
# Stored as code point pairs so this file stays ASCII.
$gbkPairs = @(
    @(0x6D93, 0x5F43),  # jiu + ...
    @(0x9428, 0x5DE7),  # ...
    @(0x93A5, 0x9452),  # ...
    @(0x9428, 0x7622),  # ...
    @(0x7E9D, 0x5F7E),  # ...
    @(0x6D09, 0x6FA4)   # ...
)
# Build a hash set of "XXXX-XXXX" keys for fast lookup.
$gbkPairSet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($pair in $gbkPairs) {
    [void]$gbkPairSet.Add(('{0:X4}-{1:X4}' -f $pair[0], $pair[1]))
}

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$Utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)

$problemCount = 0

foreach ($file in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($file)

    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF

    $invalidUtf8 = $false
    try {
        $text = $Utf8Strict.GetString($bytes)
    }
    catch {
        $invalidUtf8 = $true
        $text = $Utf8NoBom.GetString($bytes)
    }

    $replacementCount = 0
    foreach ($ch in $text.ToCharArray()) {
        if ([int]$ch -eq 0xFFFD) { $replacementCount++ }
    }

    $gbkMatches = 0
    if (-not $invalidUtf8 -and $replacementCount -eq 0) {
        # Look for any 2-char slice that is a known GBK mojibake pair.
        for ($i = 0; $i -lt $text.Length - 1; $i++) {
            $key = '{0:X4}-{1:X4}' -f [int]$text[$i], [int]$text[$i + 1]
            if ($gbkPairSet.Contains($key)) { $gbkMatches++ }
        }
    }

    $oddQuoteLines = @()
    if ([System.IO.Path]::GetExtension($file).ToLowerInvariant() -eq '.xaml') {
        $lineNo = 0
        foreach ($line in ($text -split "`r?`n")) {
            $lineNo++
            if ($line.IndexOf('<') -lt 0) { continue }
            $q = 0
            foreach ($c in $line.ToCharArray()) { if ($c -eq '"') { $q++ } }
            if (($q % 2) -ne 0) { $oddQuoteLines += $lineNo }
        }
    }

    if ($hasBom -or $invalidUtf8 -or $replacementCount -gt 0 -or $gbkMatches -gt 0 -or $oddQuoteLines.Count -gt 0) {
        $problemCount++
        $rel = $file.Substring($Root.Length).TrimStart('\', '/')
        Write-Host ("PROBLEM: {0}" -f $rel) -ForegroundColor Red
        if ($hasBom) { Write-Host "  - UTF-8 BOM present (repo convention: no BOM)" -ForegroundColor Yellow }
        if ($invalidUtf8) { Write-Host "  - invalid UTF-8 byte sequence" -ForegroundColor Yellow }
        if ($replacementCount -gt 0) { Write-Host ("  - {0} U+FFFD replacement chars" -f $replacementCount) -ForegroundColor Yellow }
        if ($gbkMatches -gt 0) { Write-Host ("  - {0} GBK mojibake marker(s)" -f $gbkMatches) -ForegroundColor Yellow }
        if ($oddQuoteLines.Count -gt 0) {
            Write-Host ("  - odd double-quote count on lines: {0}" -f ($oddQuoteLines -join ', ')) -ForegroundColor Yellow
        }
    }
}

if ($problemCount -eq 0) {
    if (-not $Quiet) {
        Write-Host ("Encoding check passed: {0} tracked text files clean." -f $files.Count) -ForegroundColor Green
    }
    exit 0
}

Write-Host ""
Write-Host ("Encoding check FAILED: {0} file(s)." -f $problemCount) -ForegroundColor Red
Write-Host "Fix guidance:" -ForegroundColor Cyan
Write-Host "  Use .NET APIs, never Get-Content / Set-Content, on files with Chinese:"
Write-Host "    `$t = [System.IO.File]::ReadAllText(`$p, [System.Text.Encoding]::UTF8)"
Write-Host "    [System.IO.File]::WriteAllText(`$p, `$t, (New-Object System.Text.UTF8Encoding(`$false)))"
Write-Host "  If a file is already corrupted, restore the clean copy: git checkout -- <file>"
exit 1
