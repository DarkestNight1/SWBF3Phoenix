# Build a self-contained Phoenix player straight into the Battlefront II folder.
#
#   .\build_phoenix.ps1
#   .\build_phoenix.ps1 -GameDir "C:\path\to\Star Wars - Battlefront 2"
#   .\build_phoenix.ps1 -UnityExe "C:\Program Files\Unity\Hub\Editor\2020.3.22f1\Editor\Unity.exe"
#
# Result:
#   <BF2>\BattlefrontII.exe              the original game, untouched
#   <BF2>\Phoenix\Phoenix.exe            this build
#   <BF2>\Play Phoenix (BF3 Legacy).lnk  shortcut to it
#
# The player finds GameData by walking up from its own folder, so it needs no
# configuration and writes nothing outside <BF2>\Phoenix. Deleting that folder
# and the shortcut uninstalls it completely.
param(
    [string]$GameDir = "",
    [string]$UnityExe = "",
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"

$Repo = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Repo "UnityProject"

# ---- game folder (same detection the mod installer uses) ----
if (-not $GameDir) {
    $GameDir = & (Join-Path $PSScriptRoot "install_mod.ps1") -PrintGameDir
    if ($LASTEXITCODE -ne 0 -or -not $GameDir) {
        Write-Error "Could not find a Battlefront II install. Pass -GameDir explicitly."
    }
}
if (-not (Test-Path (Join-Path $GameDir "GameData\data\_lvl_pc\common.lvl"))) {
    Write-Error "'$GameDir' is not a Battlefront II install (no GameData\data\_lvl_pc\common.lvl)."
}

# ---- Unity, matching the version the project was authored with ----
function Find-Unity {
    $wanted = ""
    $pv = Join-Path $Project "ProjectSettings\ProjectVersion.txt"
    if (Test-Path $pv) {
        $line = Get-Content $pv | Where-Object { $_ -match "^m_EditorVersion:" } | Select-Object -First 1
        if ($line) { $wanted = ($line -replace "^m_EditorVersion:\s*", "").Trim() }
    }

    $hub = Join-Path $env:ProgramFiles "Unity\Hub\Editor"
    if ($wanted) {
        $exact = Join-Path $hub "$wanted\Editor\Unity.exe"
        if (Test-Path $exact) { return @{ Exe = $exact; Version = $wanted; Exact = $true } }
    }
    if (Test-Path $hub) {
        # fall back to the newest 2020.3.x, which is what this project targets
        $cand = Get-ChildItem $hub -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "2020.3.*" } | Sort-Object Name -Descending | Select-Object -First 1
        if ($cand) {
            $exe = Join-Path $cand.FullName "Editor\Unity.exe"
            if (Test-Path $exe) { return @{ Exe = $exe; Version = $cand.Name; Exact = $false } }
        }
    }
    return $null
}

if (-not $UnityExe) {
    $u = Find-Unity
    if (-not $u) {
        Write-Error "Unity not found under '$env:ProgramFiles\Unity\Hub\Editor'. Pass -UnityExe explicitly."
    }
    $UnityExe = $u.Exe
    if (-not $u.Exact) {
        Write-Host "WARNING: exact editor version not installed; using $($u.Version)." -ForegroundColor Yellow
    }
}

$OutDir = Join-Path $GameDir "Phoenix"
$LogFile = Join-Path $env:TEMP "phoenix_build.log"

Write-Host "Game:    $GameDir"
Write-Host "Unity:   $UnityExe"
Write-Host "Output:  $OutDir"
Write-Host "Log:     $LogFile"
Write-Host ""
Write-Host "Building - the first run imports the whole project and can take 20+ minutes."
Write-Host ""

# -quit is deliberately omitted: PhxBuild.BuildFromCommandLine calls
# EditorApplication.Exit itself with a meaningful exit code.
$args = @(
    "-batchmode", "-nographics",
    "-projectPath", $Project,
    "-executeMethod", "PhxBuild.BuildFromCommandLine",
    "-phxOutput", $OutDir,
    "-logFile", $LogFile
)
$proc = Start-Process -FilePath $UnityExe -ArgumentList $args -NoNewWindow -PassThru -Wait
$rc = $proc.ExitCode

$exe = Join-Path $OutDir "Phoenix.exe"
if ($rc -ne 0 -or -not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "Build FAILED (Unity exit code $rc)." -ForegroundColor Red

    if (Test-Path $LogFile) {
        # A headless build needs an activated licence, and the raw log buries
        # that under licensing chatter - call it out before anything else.
        $licence = Select-String -Path $LogFile -Pattern "has not been activated|Failed to activate/update license|No valid Unity Editor license" -Quiet
        if ($licence) {
            # Distinguish "no licence at all" from "licence exists but this old
            # editor can't validate Hub's licensing client" - the fixes differ,
            # and telling someone to sign in when they already have is useless.
            $clientRejected = Select-String -Path $LogFile -Pattern "verifying Licensing Client signature|LicensingClient has failed validation" -Quiet
            $haveLicence = Test-Path "$env:LOCALAPPDATA\Unity\licenses\UnityEntitlementLicense.xml"

            Write-Host ""
            if ($clientRejected -or $haveLicence) {
                Write-Host "  Unity Hub has a licence, but this editor rejected Hub's licensing client:" -ForegroundColor Yellow
                Write-Host "    'Code 10 while verifying Licensing Client signature'"
                Write-Host "  Editors from the 2020.3 era don't trust the client shipped with Hub 3.x."
                Write-Host ""
                Write-Host "  Fix - manual activation, which bypasses the licensing client:"
                Write-Host "      .\Tools\unity_manual_activation.ps1 -Create"
                Write-Host "      (upload the .alf at https://license.unity3d.com/manual, download the .ulf)"
                Write-Host "      .\Tools\unity_manual_activation.ps1 -Apply `"<file>.ulf`""
                Write-Host ""
                Write-Host "  Or skip headless entirely and build from the open editor:"
                Write-Host "      Phoenix > Build Self-Contained Player..."
            }
            else {
                Write-Host "  Unity has no activated licence, so it cannot build headlessly." -ForegroundColor Yellow
                Write-Host "  Fix: open Unity Hub, sign in, and activate a licence (Personal is free)."
                Write-Host "  Then re-run this script - or build from the editor instead:"
                Write-Host "      Phoenix > Build Self-Contained Player..."
            }
            Write-Host ""
            exit 1
        }

        Write-Host "Last errors from $LogFile :" -ForegroundColor Red
        Select-String -Path $LogFile -Pattern "error CS|BuildFailedException|Build failed|Phx Build" |
            Select-Object -Last 15 | ForEach-Object { "  " + $_.Line.Trim() }
    }
    exit 1
}

# ---- shortcut in the game folder, next to the original exe ----
if (-not $NoShortcut) {
    try {
        $lnk = Join-Path $GameDir "Play Phoenix (BF3 Legacy).lnk"
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = $exe
        $s.WorkingDirectory = $OutDir
        $s.IconLocation = $exe
        $s.Description = "Star Wars Battlefront II via SWBF3 Phoenix (BF3 Legacy)"
        $s.Save()
        Write-Host "Shortcut: $lnk"
    }
    catch {
        Write-Host "WARNING: could not create the shortcut ($($_.Exception.Message))." -ForegroundColor Yellow
        Write-Host "         Run $exe directly instead."
    }
}

$size = [math]::Round((Get-ChildItem $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB)

Write-Host ""
Write-Host "Done - self-contained build in: $OutDir ($size MB)"
Write-Host ""
Write-Host "  Original game : $(Join-Path $GameDir 'BattlefrontII.exe')  (untouched)"
Write-Host "  Phoenix       : $exe"
Write-Host ""
Write-Host "Phoenix finds GameData by walking up from its own folder - no config, nothing"
Write-Host "written outside that folder. Delete it and the shortcut to uninstall."
