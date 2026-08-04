# Installs an extracted SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the
# game's addon folder. Usage:
#   .\install_mod.ps1 -ModDir "C:\Downloads\BF3Legacy31" [-GameDir "C:\...\Star Wars Battlefront II"]
#
# If -GameDir is omitted, common Steam/GOG locations are probed.
param(
    [Parameter(Mandatory = $true)] [string]$ModDir,
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

function Test-GamePath([string]$p) {
    return ($p -and (Test-Path (Join-Path $p "GameData\data\_lvl_pc\common.lvl")))
}

if (-not (Test-GamePath $GameDir)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Star Wars Battlefront II",
        "C:\Steam\steamapps\common\Star Wars Battlefront II",
        "D:\SteamLibrary\steamapps\common\Star Wars Battlefront II",
        "${env:ProgramFiles(x86)}\GOG Galaxy\Games\Star Wars - Battlefront II",
        "C:\GOG Games\Star Wars - Battlefront II",
        "${env:ProgramFiles(x86)}\LucasArts\Star Wars Battlefront II"
    )
    $GameDir = $candidates | Where-Object { Test-GamePath $_ } | Select-Object -First 1
}

if (-not (Test-GamePath $GameDir)) {
    Write-Error "Could not find a valid BF2 install. Pass -GameDir explicitly."
}

$Addon = Join-Path $GameDir "GameData\addon"
New-Item -ItemType Directory -Force -Path $Addon | Out-Null

$installed = 0
if (Test-Path (Join-Path $ModDir "addme.script")) {
    Copy-Item -Recurse -Force $ModDir $Addon
    Write-Host "Installed: $(Split-Path $ModDir -Leaf)"
    $installed = 1
}
else {
    Get-ChildItem -Directory $ModDir | ForEach-Object {
        if ((Test-Path (Join-Path $_.FullName "addme.script")) -or
            (Test-Path (Join-Path $_.FullName "addme.script.off"))) {
            Copy-Item -Recurse -Force $_.FullName $Addon
            Write-Host "Installed: $($_.Name)"
            $script:installed = 1
        }
    }
}

if ($installed -eq 0) {
    Write-Error "No addme.script found in '$ModDir' (or its subfolders). Extract the mod download first."
}

Write-Host "Done. Addon folder: $Addon"
Write-Host "Tip: create '$Addon\modorder.txt' to control load order ('!name' disables)."
