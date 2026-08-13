# Work around Unity licensing failing for OLD editors driven by a NEW Hub.
#
# Unity Hub 3.x ships a licensing client that editors from the 2020.3 era don't
# trust. The editor logs
#
#   [LicensingClient] Error: Code 10 while verifying Licensing Client signature
#   [Licensing::Module] Error: LicensingClient has failed validation
#   BatchMode: Unity has not been activated with a valid License.
#
# even though Unity Hub shows a perfectly good licence. Manual activation
# sidesteps the licensing client entirely: it writes a legacy Unity_lic.ulf that
# the editor reads directly.
#
# Step 1 - generate the request file (no account needed):
#     .\unity_manual_activation.ps1 -Create
#
# Step 2 - in a browser, sign in at https://license.unity3d.com/manual
#          upload the .alf it printed, download the .ulf it gives back.
#
# Step 3 - apply it:
#     .\unity_manual_activation.ps1 -Apply "C:\path\to\Unity_v2020.x.ulf"
param(
    [switch]$Create,
    [string]$Apply = "",
    [string]$UnityExe = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

$Repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = $Repo }

function Find-Unity {
    if ($UnityExe) { return $UnityExe }
    $pv = Join-Path $Repo "UnityProject\ProjectSettings\ProjectVersion.txt"
    $wanted = ""
    if (Test-Path $pv) {
        $line = Get-Content $pv | Where-Object { $_ -match "^m_EditorVersion:" } | Select-Object -First 1
        if ($line) { $wanted = ($line -replace "^m_EditorVersion:\s*", "").Trim() }
    }
    $hub = Join-Path $env:ProgramFiles "Unity\Hub\Editor"
    if ($wanted) {
        $exact = Join-Path $hub "$wanted\Editor\Unity.exe"
        if (Test-Path $exact) { return $exact }
    }
    $cand = Get-ChildItem $hub -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "2020.3.*" } | Sort-Object Name -Descending | Select-Object -First 1
    if ($cand) { return (Join-Path $cand.FullName "Editor\Unity.exe") }
    return $null
}

$unity = Find-Unity
if (-not $unity -or -not (Test-Path $unity)) {
    Write-Error "Unity editor not found. Pass -UnityExe explicitly."
}

if ($Create) {
    Push-Location $OutDir
    try {
        $log = Join-Path $env:TEMP "unity_alf.log"
        # This always exits non-zero (it has no licence - that is the point);
        # the .alf landing on disk is the success signal.
        Start-Process -FilePath $unity `
            -ArgumentList @("-batchmode", "-quit", "-nographics", "-createManualActivationFile", "-logFile", $log) `
            -NoNewWindow -PassThru -Wait | Out-Null

        $alf = Get-ChildItem $OutDir -Filter "*.alf" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $alf) {
            Write-Host "Failed to create the activation file. Log: $log" -ForegroundColor Red
            Get-Content $log -Tail 12
            exit 1
        }

        Write-Host ""
        Write-Host "Activation request written:" -ForegroundColor Green
        Write-Host "  $($alf.FullName)"
        Write-Host ""
        Write-Host "Next:"
        Write-Host "  1. Open https://license.unity3d.com/manual and sign in."
        Write-Host "  2. Upload that .alf file."
        Write-Host "  3. Download the .ulf licence file it returns."
        Write-Host "  4. Run:  .\Tools\unity_manual_activation.ps1 -Apply `"<path to .ulf>`""
        Write-Host ""
    }
    finally { Pop-Location }
    exit 0
}

if ($Apply) {
    if (-not (Test-Path $Apply)) { Write-Error "'$Apply' not found." }

    $log = Join-Path $env:TEMP "unity_ulf.log"
    $p = Start-Process -FilePath $unity `
        -ArgumentList @("-batchmode", "-quit", "-nographics", "-manualLicenseFile", $Apply, "-logFile", $log) `
        -NoNewWindow -PassThru -Wait

    $ulf = "C:\ProgramData\Unity\Unity_lic.ulf"
    if (Test-Path $ulf) {
        Write-Host "Licence installed: $ulf" -ForegroundColor Green
        Write-Host "Headless builds should work now - re-run BuildPhoenix.bat."
        exit 0
    }

    Write-Host "Activation did not produce $ulf (Unity exit $($p.ExitCode))." -ForegroundColor Red
    Write-Host "Log: $log"
    Get-Content $log -Tail 15
    exit 1
}

Write-Host "Nothing to do. Use -Create, then -Apply <file.ulf>. See the header of this script."
