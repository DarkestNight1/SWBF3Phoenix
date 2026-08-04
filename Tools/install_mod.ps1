# Install a SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the game's addon
# folder. Accepts either an extracted folder OR a .zip archive.
#
#   .\install_mod.ps1 -ModPath "C:\Downloads\BF3Legacy31.zip"
#   .\install_mod.ps1 -ModPath "C:\Downloads\BF3Legacy31" -GameDir "D:\...\Star Wars Battlefront II"
#   .\install_mod.ps1 -List
param(
    [string]$ModPath = "",
    [string]$GameDir = "",
    [switch]$List
)

$ErrorActionPreference = "Stop"

function Test-GamePath([string]$p) {
    return ($p -and (Test-Path (Join-Path $p "GameData\data\_lvl_pc\common.lvl")))
}

function Find-GameDir([string]$given) {
    if (Test-GamePath $given) { return $given }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add("${env:ProgramFiles(x86)}\Steam\steamapps\common\Star Wars Battlefront II")
    $candidates.Add("C:\Steam\steamapps\common\Star Wars Battlefront II")
    $candidates.Add("D:\SteamLibrary\steamapps\common\Star Wars Battlefront II")
    $candidates.Add("${env:ProgramFiles(x86)}\GOG Galaxy\Games\Star Wars - Battlefront II")
    $candidates.Add("C:\GOG Games\Star Wars - Battlefront II")
    $candidates.Add("${env:ProgramFiles(x86)}\LucasArts\Star Wars Battlefront II")

    # extra Steam libraries
    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "C:\Steam")) {
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches | ForEach-Object {
                foreach ($m in $_.Matches) {
                    $lib = $m.Groups[1].Value -replace '\\\\', '\'
                    $candidates.Add("$lib\steamapps\common\Star Wars Battlefront II")
                }
            }
        }
    }

    return $candidates | Where-Object { Test-GamePath $_ } | Select-Object -First 1
}

$GameDir = Find-GameDir $GameDir
if (-not (Test-GamePath $GameDir)) {
    Write-Error "Could not find a valid BF2 install. Pass -GameDir explicitly."
}
$Addon = Join-Path $GameDir "GameData\addon"

# ---- -List mode ----
if ($List) {
    Write-Host "Addon folder: $Addon"
    if (-not (Test-Path $Addon)) { Write-Host "(none installed yet)"; exit 0 }
    $any = $false
    Get-ChildItem -Directory $Addon | ForEach-Object {
        $any = $true
        if (Test-Path (Join-Path $_.FullName "addme.script")) { Write-Host "  [enabled ] $($_.Name)" }
        elseif (Test-Path (Join-Path $_.FullName "addme.script.off")) { Write-Host "  [disabled] $($_.Name)" }
        else { Write-Host "  [no addme] $($_.Name)" }
    }
    if (-not $any) { Write-Host "(none installed yet)" }
    exit 0
}

if (-not $ModPath) { Write-Error "Usage: .\install_mod.ps1 -ModPath <mod.zip | extracted-folder> [-GameDir <path>]" }

# ---- extract archive if needed ----
$Temp = $null
try {
    if (Test-Path $ModPath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($ModPath) -ne ".zip") {
            Write-Error "Unsupported archive '$ModPath'. Use a .zip or an extracted folder (7-Zip archives must be extracted first)."
        }
        $Temp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Force -Path $Temp | Out-Null
        Write-Host "Extracting $(Split-Path $ModPath -Leaf)..."
        Expand-Archive -Path $ModPath -DestinationPath $Temp -Force
        $ModDir = $Temp
    }
    else {
        $ModDir = $ModPath
    }

    if (-not (Test-Path $ModDir -PathType Container)) { Write-Error "'$ModDir' is not a folder." }

    New-Item -ItemType Directory -Force -Path $Addon | Out-Null

    # Find every addon folder anywhere in the tree, so the archive's internal
    # layout doesn't matter to the user.
    $scripts = Get-ChildItem -Path $ModDir -Filter "addme.script" -Recurse -Depth 4 -File -ErrorAction SilentlyContinue
    $installed = 0
    foreach ($s in $scripts) {
        $src = $s.Directory
        $dest = Join-Path $Addon $src.Name
        if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
        Copy-Item -Recurse -Force $src.FullName $Addon
        Write-Host "Installed: $($src.Name)"
        $installed++
    }

    if ($installed -eq 0) {
        Write-Error "No addme.script found under '$ModPath'. That file marks a SWBF2 addon - make sure this is a mod download."
    }

    Write-Host ""
    Write-Host "Done - $installed mod folder(s) into: $Addon"
    Write-Host "Load order (optional): create '$Addon\modorder.txt', one folder per line, '!name' to disable."
    Write-Host "Verify with: .\install_mod.ps1 -List"
}
finally {
    if ($Temp -and (Test-Path $Temp)) { Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue }
}
