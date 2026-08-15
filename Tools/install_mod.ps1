# Install a SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the game's addon
# folder AND point the Unity project at the game, so opening the editor and
# pressing Play needs no further setup.
#
#   .\install_mod.ps1 -Verify
#   .\install_mod.ps1 -ModPath "C:\Downloads\Battlefront3Legacy3.1Demo"
#   .\install_mod.ps1 -ModPath "C:\Downloads\Battlefront3Legacy3.1Demo" -Link
#   .\install_mod.ps1 -List
#
# -Link makes directory junctions instead of copying. The BF3 Legacy 3.1 pack
# is ~19 GB; a junction is instant, uses no extra disk, and lets you edit the
# mod in place while debugging. Delete the junction to uninstall - the original
# download is untouched. Copy (the default) is safer if the download folder is
# temporary or on a removable drive.
param(
    [string]$ModPath = "",
    [string]$GameDir = "",
    [switch]$List,
    [switch]$Link,
    [switch]$Verify,
    [switch]$NoUnitySetup,
    [switch]$Auto,          # hunt for the mod download instead of being told where it is
    [switch]$Force,         # reinstall components that are already there
    [switch]$PrintGameDir   # print the detected game folder and exit (for other scripts)
)

$ErrorActionPreference = "Stop"

# Folder name -> what it actually is, for the BF3 Legacy 3.1 pack. Mirrors
# PhxBF3LegacyContent.ComponentTable on the runtime side.
$BF3Components = [ordered]@{
    "BF3"             = "Pre-Demo 3.0 (main mod)"
    "BF3Era"          = "Era Mod 1.4"
    "BF3MoreMaps"     = "MoreMaps Patch 1.9"
    "BF3GCWSpaceDemo" = "GCW Space Demo"
    "BF3Vjun"         = "Extended Engagements (Vjun/Sulon/Lucrehulk)"
    "BF3Venator"      = "Venator"
    "BF3Cato-Hunt"    = "Cato Neimoidia: Hunt"
}

# ---- Battlefront Conversion Pack ----
# One folder ("BF1" as shipped) with all 25 maps, the KOTOR era and the extra
# units. Mirrors PhxConversionPackContent on the runtime side, including its
# install check: SIDE\patch.lvl and SIDE\patch2.lvl are what 2.0 + the 2.2
# patch put there, and every side the pack's maps ask for comes out of them.
function Test-ConversionPack([string]$dir) {
    if (-not $dir -or -not (Test-Path $dir)) { return $false }
    $name = (Split-Path -Leaf $dir).ToLower() -replace '[ \-]', ''
    if ($name -eq "bf1" -or $name -like "*conversionpack*" -or $name -like "*conv_pack*" -or
        $name -like "*convpack*") {
        return $true
    }
    return ((Test-Path (Join-Path $dir "data\_lvl_pc\side\patch.lvl")) -and
            (Test-Path (Join-Path $dir "data\_lvl_pc\side\patch2.lvl")))
}

function Get-ConversionPackIssues([string]$dir) {
    $issues = @()
    if (-not ((Test-Path (Join-Path $dir "data\_lvl_pc\side\patch.lvl")) -and
              (Test-Path (Join-Path $dir "data\_lvl_pc\side\patch2.lvl")))) {
        $issues += "SIDE\patch.lvl or SIDE\patch2.lvl is missing. Install Conversion Pack 2.0 " +
                   "first, then the 2.2 patch over it - without them the pack's units, heroes " +
                   "and vehicles will not load."
    }
    if (-not (Test-Path (Join-Path $dir "data\_lvl_pc\mission.lvl"))) {
        $issues += "data\_LVL_PC\mission.lvl is missing - the pack's maps cannot load."
    }
    return $issues
}

# Installers disagree on the folder name - Steam uses roman numerals, GOG ships
# "Star Wars - Battlefront 2". Keep in sync with PhxGamePathDetector.
$InstallNames = @(
    "Star Wars Battlefront II",
    "Star Wars - Battlefront II",
    "Star Wars Battlefront 2",
    "Star Wars - Battlefront 2",
    "STAR WARS Battlefront II",
    "Battlefront II",
    "SWBF2"
)
$Containers = @("GOG Games", "GOG Galaxy\Games", "Games", "LucasArts")

function Test-GamePath([string]$p) {
    return ($p -and (Test-Path (Join-Path $p "GameData\data\_lvl_pc\common.lvl")))
}

function Get-SearchRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    $roots.Add($env:USERPROFILE)
    $roots.Add((Join-Path $env:USERPROFILE "Desktop"))
    $roots.Add((Join-Path $env:USERPROFILE "Documents"))
    $roots.Add((Join-Path $env:USERPROFILE "Downloads"))
    $roots.Add(${env:ProgramFiles(x86)})
    $roots.Add($env:ProgramFiles)
    Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Root -match '^[A-Za-z]:\\$') { $roots.Add($_.Root) }
    }
    return $roots | Where-Object { $_ }
}

function Find-GameDir([string]$given) {
    if (Test-GamePath $given) { return $given }

    $candidates = [System.Collections.Generic.List[string]]::new()

    # Steam: default roots plus every library in libraryfolders.vdf
    $steamRoots = @("${env:ProgramFiles(x86)}\Steam", "C:\Steam", "D:\Steam", "D:\SteamLibrary")
    foreach ($steam in $steamRoots) {
        foreach ($n in $InstallNames) { $candidates.Add("$steam\steamapps\common\$n") }
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches | ForEach-Object {
                foreach ($m in $_.Matches) {
                    $lib = $m.Groups[1].Value -replace '\\\\', '\'
                    foreach ($n in $InstallNames) { $candidates.Add("$lib\steamapps\common\$n") }
                }
            }
        }
    }

    # GOG / retail / manual copies, including one level inside game libraries
    # so a renamed install is still found.
    foreach ($root in Get-SearchRoots) {
        foreach ($n in $InstallNames) { $candidates.Add((Join-Path $root $n)) }
        foreach ($c in $Containers) {
            $dir = Join-Path $root $c
            foreach ($n in $InstallNames) { $candidates.Add((Join-Path $dir $n)) }
            if (Test-Path $dir) {
                Get-ChildItem -Directory $dir -ErrorAction SilentlyContinue |
                    ForEach-Object { $candidates.Add($_.FullName) }
            }
        }
    }

    return $candidates | Where-Object { Test-GamePath $_ } | Select-Object -First 1
}

# Locate a mod download without being told where it is: an addon folder is any
# folder holding an addme.script. Searches the places downloads actually land,
# nearest first, and ignores anything already inside the game's addon folder.
function Find-ModSource {
    $roots = @(
        (Split-Path -Parent $PSScriptRoot),
        (Join-Path $env:USERPROFILE "Downloads"),
        (Join-Path $env:USERPROFILE "Desktop"),
        (Join-Path $env:USERPROFILE "Documents")
    )

    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $scripts = Get-ChildItem -Path $r -Filter "addme.script" -Recurse -Depth 4 -File -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -notlike "*\GameData\addon\*" }
        if (-not $scripts) { continue }

        # Several addon folders normally share one parent (the pack's "addon"
        # folder). Pick the parent that accounts for the most of them.
        $best = $scripts | Group-Object { $_.Directory.Parent.FullName } |
                Sort-Object Count -Descending | Select-Object -First 1
        return $best.Name
    }
    return $null
}

function Get-ModRows([string]$addon) {
    $rows = @()
    if (-not (Test-Path $addon)) { return $rows }
    Get-ChildItem -Directory $addon -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if (Test-Path (Join-Path $_.FullName "addme.script"))          { $state = "enabled " }
        elseif (Test-Path (Join-Path $_.FullName "addme.script.off"))  { $state = "disabled" }
        else                                                          { $state = "no addme" }
        $isLink = ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        $isCP = Test-ConversionPack $_.FullName
        $rows += [pscustomobject]@{
            Name      = $_.Name
            State     = $state
            Linked    = $isLink
            Component = $BF3Components[$_.Name]
            IsCP      = $isCP
            CPIssues  = @(if ($isCP) { Get-ConversionPackIssues $_.FullName })
        }
    }
    return $rows
}

# ---- resolve the game ----
$GameDir = Find-GameDir $GameDir
if (-not (Test-GamePath $GameDir)) {
    if ($PrintGameDir) { exit 1 }
    Write-Error "Could not find a valid BF2 install. Pass -GameDir explicitly."
}

if ($PrintGameDir) { Write-Output $GameDir; exit 0 }
$Addon = Join-Path $GameDir "GameData\addon"

# ---- Unity setup: write the game path where the runtime looks for it ----
# PhxGame falls back to PhxGamePathDetector, which reads GamePathOverride from
# bf3legacy.json in Unity's persistent data path. Setting it there means the
# editor scene needs no edits - Game Path String stays empty and still works.
function Get-UnityConfigPath {
    $repo = Split-Path -Parent $PSScriptRoot
    $settings = Join-Path $repo "UnityProject\ProjectSettings\ProjectSettings.asset"
    $company = "Ben1138"; $product = "Phoenix"
    if (Test-Path $settings) {
        $txt = Get-Content $settings -Raw
        if ($txt -match '(?m)^\s*companyName:\s*(.+)$') { $company = $Matches[1].Trim() }
        if ($txt -match '(?m)^\s*productName:\s*(.+)$') { $product = $Matches[1].Trim() }
    }
    return (Join-Path $env:USERPROFILE "AppData\LocalLow\$company\$product\bf3legacy.json")
}

function Set-UnityGamePath([string]$game) {
    $cfg = Get-UnityConfigPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cfg) | Out-Null

    # Preserve any settings the user already tuned; only set the path.
    $obj = $null
    if (Test-Path $cfg) {
        try { $obj = Get-Content $cfg -Raw | ConvertFrom-Json } catch { $obj = $null }
    }
    if ($null -eq $obj) { $obj = [pscustomobject]@{} }

    $forward = $game -replace '\\', '/'
    if ($obj.PSObject.Properties.Name -contains "GamePathOverride") {
        $obj.GamePathOverride = $forward
    } else {
        $obj | Add-Member -NotePropertyName GamePathOverride -NotePropertyValue $forward
    }

    ($obj | ConvertTo-Json -Depth 6) | Out-File -FilePath $cfg -Encoding utf8
    return $cfg
}

# ---- -Verify mode ----
if ($Verify) {
    Write-Host "Game:   $GameDir"
    $lvl = Join-Path $GameDir "GameData\data\_lvl_pc"
    $required = @("common.lvl", "core.lvl", "ingame.lvl", "inshell.lvl", "mission.lvl", "shell.lvl")
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $lvl $_)) }
    if ($missing) {
        Write-Host "  MISSING required lvl files: $($missing -join ', ')" -ForegroundColor Red
    } else {
        Write-Host "  All 6 required lvl files present." -ForegroundColor Green
    }

    Write-Host "Addon:  $Addon"
    $rows = Get-ModRows $Addon
    if ($rows.Count -eq 0) {
        Write-Host "  (no mods installed yet)"
    } else {
        foreach ($r in $rows) {
            $tag = ""
            if ($r.Component)  { $tag = " - BF3 Legacy: $($r.Component)" }
            elseif ($r.IsCP)   { $tag = " - Battlefront Conversion Pack" }
            $lnk = if ($r.Linked) { " (linked)" } else { "" }
            Write-Host "  [$($r.State)] $($r.Name)$lnk$tag"
            foreach ($issue in $r.CPIssues) { Write-Host "    WARNING: $issue" -ForegroundColor Yellow }
        }
        $have = ($rows | Where-Object { $_.Component -and $_.State -eq "enabled " }).Count
        if ($have -gt 0) {
            Write-Host "  BF3 Legacy: $have/$($BF3Components.Count) components enabled."
            if (-not ($rows | Where-Object { $_.Name -eq "BF3" })) {
                Write-Host "  WARNING: the main 'BF3' folder is missing; other components depend on it." -ForegroundColor Yellow
            }
        }
    }

    $cfg = Get-UnityConfigPath
    if (Test-Path $cfg) {
        try {
            $o = Get-Content $cfg -Raw | ConvertFrom-Json
            if ($o.GamePathOverride) {
                Write-Host "Unity:  configured - GamePathOverride = $($o.GamePathOverride)" -ForegroundColor Green
            } else {
                Write-Host "Unity:  $cfg exists but has no GamePathOverride" -ForegroundColor Yellow
            }
        } catch { Write-Host "Unity:  $cfg (unreadable)" -ForegroundColor Yellow }
    } else {
        Write-Host "Unity:  not configured. Run '.\install_mod.ps1' with no arguments to set it up." -ForegroundColor Yellow
    }
    exit 0
}

# ---- -List mode ----
if ($List) {
    Write-Host "Addon folder: $Addon"
    $rows = Get-ModRows $Addon
    if ($rows.Count -eq 0) { Write-Host "(none installed yet)"; exit 0 }
    foreach ($r in $rows) {
        $tag = ""
        if ($r.Component)  { $tag = " - BF3 Legacy: $($r.Component)" }
        elseif ($r.IsCP)   { $tag = " - Battlefront Conversion Pack" }
        $lnk = if ($r.Linked) { " (linked)" } else { "" }
        Write-Host "  [$($r.State)] $($r.Name)$lnk$tag"
        foreach ($issue in $r.CPIssues) { Write-Host "    WARNING: $issue" -ForegroundColor Yellow }
    }
    exit 0
}

if (-not $ModPath -and $Auto) {
    Write-Host "Game found: $GameDir"
    Write-Host "Looking for a mod download..."
    $ModPath = Find-ModSource
    if ($ModPath) {
        Write-Host "Found: $ModPath"
    } else {
        Write-Host "No mod download found in Downloads, Desktop, Documents or next to this repo." -ForegroundColor Yellow
        Write-Host "Drag the extracted mod folder onto this .bat, or pass -ModPath <folder>."
    }
    Write-Host ""
}

if (-not $ModPath) {
    # No mod given: still worth wiring up Unity so the editor can find the game.
    if (-not $NoUnitySetup) {
        $cfg = Set-UnityGamePath $GameDir
        Write-Host "Game found:   $GameDir"
        Write-Host "Unity set up: $cfg"
        Write-Host ""
        Write-Host "Open UnityProject, open Runtime/Scenes/PhxMainScene and press Play."
        Write-Host "To install a mod:  .\install_mod.ps1 -ModPath <folder-or-zip> [-Link]"
        exit 0
    }
    Write-Error "Usage: .\install_mod.ps1 -ModPath <mod.zip | extracted-folder> [-Link] [-GameDir <path>]"
}

# ---- extract archive if needed ----
$Temp = $null
try {
    if (Test-Path $ModPath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($ModPath) -ne ".zip") {
            Write-Error "Unsupported archive '$ModPath'. Use a .zip or an extracted folder (7-Zip archives must be extracted first)."
        }
        if ($Link) {
            Write-Error "-Link needs an extracted folder to point at, not an archive."
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
    if (-not $scripts) {
        Write-Error "No addme.script found under '$ModPath'. That file marks a SWBF2 addon - make sure this is a mod download."
    }

    $installed = 0
    $skipped = 0
    foreach ($s in $scripts) {
        $src = $s.Directory
        $dest = Join-Path $Addon $src.Name

        # Already there? Don't re-copy gigabytes just because the script ran
        # twice. -Force reinstalls anyway.
        if (-not $Force -and (Test-Path (Join-Path $dest "addme.script"))) {
            Write-Host "Skipped:   $($src.Name) (already installed - use -Force to reinstall)"
            $skipped++
            continue
        }

        if (Test-Path $dest) {
            # Remove-Item on a junction deletes the link, not the target.
            Remove-Item -Recurse -Force $dest
        }

        if ($Link) {
            New-Item -ItemType Junction -Path $dest -Target $src.FullName | Out-Null
            Write-Host "Linked:    $($src.Name)  ->  $($src.FullName)"
        }
        else {
            $mb = [math]::Round((Get-ChildItem -Recurse -File $src.FullName -ErrorAction SilentlyContinue |
                                 Measure-Object -Property Length -Sum).Sum / 1MB)
            Write-Host "Copying:   $($src.Name) ($mb MB)..."
            Copy-Item -Recurse -Force $src.FullName $Addon
            Write-Host "Installed: $($src.Name)"
        }
        $installed++
    }

    Write-Host ""
    if ($skipped -gt 0) {
        Write-Host "Done - $installed installed, $skipped already present, in: $Addon"
    } else {
        Write-Host "Done - $installed mod folder(s) into: $Addon"
    }

    $rows = Get-ModRows $Addon
    $known = $rows | Where-Object { $_.Component }
    if ($known) {
        Write-Host ""
        Write-Host "Recognized BF3 Legacy components:"
        foreach ($r in $known) { Write-Host "  - $($r.Name): $($r.Component)" }
        if (-not ($rows | Where-Object { $_.Name -eq "BF3" })) {
            Write-Host "  WARNING: the main 'BF3' folder is missing; the others depend on it." -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host "You do NOT need the 1.3 patch or the UI Remaster the pack's readme asks for -"
        Write-Host "Phoenix provides those shell helpers itself."
    }

    $cp = $rows | Where-Object { $_.IsCP }
    if ($cp) {
        Write-Host ""
        Write-Host "Battlefront Conversion Pack installed. Phoenix recognizes it natively: its maps,"
        Write-Host "its Knights of the Old Republic era and its extra game modes appear in Instant"
        Write-Host "Action without the 1.3 patch. Install order matters - 2.0 first, then the 2.2"
        Write-Host "patch over it. Galactic Conquest is not implemented in Phoenix, so the pack's"
        Write-Host "KotOR GC download has no effect here. See docs/ConversionPack.md."
        foreach ($r in $cp) {
            foreach ($issue in $r.CPIssues) { Write-Host "  WARNING: $issue" -ForegroundColor Yellow }
        }
    }

    if (-not $NoUnitySetup) {
        $cfg = Set-UnityGamePath $GameDir
        Write-Host ""
        Write-Host "Unity set up: $cfg"
        Write-Host "Open UnityProject, open Runtime/Scenes/PhxMainScene and press Play - no scene edits needed."
    }

    Write-Host ""
    Write-Host "Load order (optional): create '$Addon\modorder.txt', one folder per line, '!name' to disable."
    Write-Host "Verify with: .\install_mod.ps1 -Verify"
}
finally {
    if ($Temp -and (Test-Path $Temp)) { Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue }
}
