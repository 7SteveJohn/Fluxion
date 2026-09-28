# Compile a MANIFEST-FREE probe build of GameBoost-DLSSG -> .probe\GameBoost-DLSSG-probe[-share].exe
# Purpose: the shipping exe carries a requireAdministrator manifest, so it cannot be
# started from a sandbox (CreateProcess fails with WinError 740) and startup crashes
# are invisible until the user double-clicks. The probe drops /win32manifest, so it
# runs asInvoker and `--uiprobe` can construct MainForm for real.
# NOTE: keep this file ASCII-only.
param([switch]$Share)

$root  = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$proj  = Split-Path -Parent $root
$srcs  = @(
    (Join-Path $proj "src\Core.cs"),
    (Join-Path $proj "src\Pack.cs"),
    (Join-Path $proj "src\Dlssg.cs"),
    (Join-Path $proj "src\Lib.cs"),
    (Join-Path $proj "src\Ui.cs")
)
$dir   = Join-Path $proj ".probe"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$name  = if ($Share) { "GameBoost-DLSSG-probe-share.exe" } else { "GameBoost-DLSSG-probe-full.exe" }
$out   = Join-Path $dir $name

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

$args = @(
    "/nologo", "/target:exe", "/platform:anycpu", "/unsafe",
    "/out:`"$out`"",
    "/r:System.dll", "/r:System.Core.dll",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
    "/r:System.Web.Extensions.dll", "/r:System.Management.dll",
    "/r:System.ServiceProcess.dll",
    # Pack.cs unzips release zips (ZipArchive / ZipFile live in these two assemblies)
    "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll"
)
if ($Share) { $args += "/define:SHARE" }
foreach ($s in $srcs) { $args += "`"$s`"" }

$p = Start-Process -FilePath $csc -ArgumentList $args -NoNewWindow -Wait -PassThru `
     -RedirectStandardOutput "$env:TEMP\gb_probe_out.txt" -RedirectStandardError "$env:TEMP\gb_probe_err.txt"
if ($p.ExitCode -ne 0) {
    Write-Output ("PROBE_COMPILE_FAIL: " + (Get-Content "$env:TEMP\gb_probe_err.txt","$env:TEMP\gb_probe_out.txt" -ErrorAction SilentlyContinue | Out-String))
    exit 1
}
Write-Output ("PROBE_OK " + $out)
