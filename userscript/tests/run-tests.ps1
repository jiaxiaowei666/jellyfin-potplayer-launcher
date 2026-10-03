<#
.SYNOPSIS
    Run the userscript button-injection tests (jsdom based).

.DESCRIPTION
    Loads userscript/jellyfin-potplayer-button.user.js inside a real DOM (jsdom) and checks
    the button behaves across Jellyfin's client-side navigation - in particular the
    "More Like This" case, where the detail page reuses its DOM and a stale button used to
    block re-injection (symptom: no button until you refresh).

    jsdom is installed on demand into node_modules next to this script (git-ignored), and
    only when it is missing. Requires Node.js and npm on PATH.

.PARAMETER ForceInstall
    Re-install jsdom even if it is already present.

.EXAMPLE
    .\run-tests.ps1
#>
[CmdletBinding()]
param(
    [switch]$ForceInstall
)

$ErrorActionPreference = "Stop"
$testsDir = $PSScriptRoot
$userscript = Join-Path (Split-Path -Parent $testsDir) "jellyfin-potplayer-button.user.js"

if (-not (Test-Path $userscript)) { throw "userscript not found: $userscript" }
foreach ($tool in "node", "npm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found on PATH" }
}

# Recent jsdom needs Node 22+ (its undici dependency fails to load on Node 20).
$nodeMajor = [int](& node -p "process.versions.node.split('.')[0]")
if ($nodeMajor -lt 22) {
    throw "Node.js $nodeMajor found, but jsdom requires Node 22 or newer. Please upgrade Node."
}

# Note: Node resolves require() from the SCRIPT's directory, so jsdom must live in
# <testsDir>\node_modules (not in some other folder we happen to run from).
$jsdom = Join-Path $testsDir "node_modules\jsdom"
if ($ForceInstall -or -not (Test-Path $jsdom)) {
    Write-Host "==> Installing jsdom into $testsDir\node_modules" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $testsDir "package.json"))) {
        '{ "name": "jellyfin-potplayer-userscript-tests", "private": true }' |
            Set-Content (Join-Path $testsDir "package.json") -Encoding ascii
    }

    Push-Location $testsDir
    try {
        & npm install jsdom --silent --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm install jsdom failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
}

Write-Host "==> Running userscript tests under jsdom" -ForegroundColor Cyan
& node (Join-Path $testsDir "run.js") $userscript
exit $LASTEXITCODE
