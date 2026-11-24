# Installs b-learn for Windows:
#   irm https://raw.githubusercontent.com/BlakeHastings/b-learn/main/install/install.ps1 | iex
# Latest build of main instead of the stable release:
#   $env:BLEARN_CHANNEL = 'edge'; irm .../install.ps1 | iex
# Puts a single self-contained blearn.exe in %LOCALAPPDATA%\b-learn\bin and adds it to your user PATH.
$ErrorActionPreference = 'Stop'

$repo = 'BlakeHastings/b-learn'
$channel = if ($env:BLEARN_CHANNEL) { $env:BLEARN_CHANNEL } else { 'stable' }
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$asset = "blearn-win-$arch.exe"

$url = switch ($channel) {
    'stable' { "https://github.com/$repo/releases/latest/download/$asset" }
    'edge' { "https://github.com/$repo/releases/download/edge/$asset" }
    default { throw "BLEARN_CHANNEL must be stable or edge, not '$channel'." }
}
if ($env:BLEARN_DOWNLOAD_URL) { $url = $env:BLEARN_DOWNLOAD_URL }

$dir = if ($env:BLEARN_INSTALL_DIR) { $env:BLEARN_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'b-learn\bin' }
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$target = Join-Path $dir 'blearn.exe'
$temp = "$target.download"

Write-Host "Downloading b-learn ($channel, win-$arch)..."
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest very slow on Windows PowerShell
Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
try { Move-Item -Force $temp $target }
catch { throw "Could not replace $target. Close b-learn (Ctrl+C in its window) and run the installer again." }
Write-Host "Installed $(& $target --version) to $target"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($env:BLEARN_NO_MODIFY_PATH) {
    Write-Host "Run: $target"
} elseif (-not (($userPath -split ';') -contains $dir)) {
    [Environment]::SetEnvironmentVariable('Path', ($(if ($userPath) { "$userPath;" } else { '' }) + $dir), 'User')
    Write-Host "Added $dir to your PATH. Open a new terminal, then run: blearn"
} else {
    Write-Host 'Run: blearn'
}
