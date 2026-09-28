# Compile the SHARE build of GameBoost-DLSSG  ->  dist_share\GameBoost-DLSSG.exe
# Share build = /define:SHARE : frame-generation features only, portable data dir,
#               no watchdog / no remote-control link / no system tuning.
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads no-BOM files as ANSI/GBK.
$root  = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$srcs  = @(
    (Join-Path $root "src\Core.cs"),
    (Join-Path $root "src\Dlssg.cs"),
    (Join-Path $root "src\Lib.cs"),
    (Join-Path $root "src\Ui.cs")
)
$dist  = Join-Path $root "dist_share"
$out   = Join-Path $dist "GameBoost-DLSSG.exe"
$ico   = Join-Path $root "icon\icon.ico"
$resultFile = Join-Path $root "compile_share_result.txt"

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
$csc = "$fw\csc.exe"

foreach ($n in @('http_proxy','https_proxy','all_proxy','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY')) {
    Remove-Item "env:$n" -ErrorAction SilentlyContinue
}
try {
    $curPath = [System.Environment]::GetEnvironmentVariable('Path', 'Process')
    [System.Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [System.Environment]::SetEnvironmentVariable('Path', $curPath, 'Process')
} catch { }

$missing = @()
foreach ($s in $srcs) { if (-not (Test-Path $s)) { $missing += $s } }
if ($missing.Count -gt 0) {
    "COMPILE_FAIL: source missing: " + ($missing -join "; ") | Out-File $resultFile -Encoding utf8
    exit 1
}

try {
    if (-not (Test-Path $csc)) { throw "csc.exe not found under $fw" }
    if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
    $args = @(
        "/nologo", "/target:winexe", "/platform:anycpu", "/unsafe",
        "/define:SHARE",
        "/out:`"$out`"",
        "/r:System.dll", "/r:System.Core.dll",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
        "/r:System.Web.Extensions.dll", "/r:System.Management.dll",
        "/r:System.ServiceProcess.dll"
    )
    if (Test-Path $ico) { $args += "/win32icon:`"$ico`"" }
    $mf = Join-Path $root "src\app.manifest"
    if (Test-Path $mf) { $args += "/win32manifest:`"$mf`"" }
    foreach ($s in $srcs) { $args += "`"$s`"" }
    $p = Start-Process -FilePath $csc -ArgumentList $args -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput "$env:TEMP\gb_csc_share_out.txt" -RedirectStandardError "$env:TEMP\gb_csc_share_err.txt"
    if ($p.ExitCode -ne 0 -or -not (Test-Path $out)) {
        throw ("csc failed: " + (Get-Content "$env:TEMP\gb_csc_share_err.txt", "$env:TEMP\gb_csc_share_out.txt" -ErrorAction SilentlyContinue | Out-String))
    }
    "COMPILE_OK" | Out-File $resultFile -Encoding utf8
    exit 0
} catch {
    $msg = "COMPILE_FAIL: " + $_.Exception.Message
    if ($_.ErrorDetails) { $msg += "`nDETAIL: " + $_.ErrorDetails.Message }
    $msg | Out-File $resultFile -Encoding utf8
    exit 1
}
