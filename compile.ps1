# Compile GameBoost-DLSSG sources -> GameBoost-DLSSG.exe  (csc /win32icon)
# Sources: src\Core.cs (logic) + src\Dlssg.cs (frame-generation module) + src\Ui.cs (light minimal UI)
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads no-BOM files as ANSI/GBK;
#       non-ASCII literals break parsing (caused a false COMPILE_OK before).
$root  = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$srcs  = @(
    (Join-Path $root "src\Core.cs"),
    (Join-Path $root "src\Pack.cs"),
    (Join-Path $root "src\Dlssg.cs"),
    (Join-Path $root "src\Lib.cs"),
    (Join-Path $root "src\Ui.cs")
)
$out   = Join-Path $root "GameBoost-DLSSG.new.exe"
$final = Join-Path $root "GameBoost-DLSSG.exe"
$ico   = Join-Path $root "icon\icon.ico"
$resultFile = Join-Path $root "compile_result.txt"

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
$csc = "$fw\csc.exe"

# csc.exe is a .NET app: at startup it builds a case-insensitive env dictionary.
# If proxy tools inject both http_proxy and HTTP_PROXY, csc crashes with
# "An item with the same key has already been added" before compiling anything.
# Clear all proxy vars for this session so the child process inherits a clean env.
foreach ($n in @('http_proxy','https_proxy','all_proxy','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY')) {
    Remove-Item "env:$n" -ErrorAction SilentlyContinue
}

# Same class of crash: if the parent (bash-style) environment block carries both "PATH" and
# "Path" entries, csc dies with "An item with the same key has already been added: PATH".
# Normalize to a single well-cased entry before spawning csc.
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
    $args = @(
        "/nologo", "/target:winexe", "/platform:anycpu", "/unsafe",
        "/out:`"$out`"",
        "/r:System.dll", "/r:System.Core.dll",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
        "/r:System.Web.Extensions.dll", "/r:System.Management.dll",
        "/r:System.ServiceProcess.dll",
        # Pack.cs 解压作者发布的 zip 用（ZipArchive / ZipFile 分属这两个程序集）
        "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll"
    )
    if (Test-Path $ico) { $args += "/win32icon:`"$ico`"" }
    # UAC manifest (requireAdministrator): makes the built exe request elevation itself,
    # so shortcuts show the shield and there is exactly one UAC prompt per launch.
    $mf = Join-Path $root "src\app.manifest"
    if (Test-Path $mf) { $args += "/win32manifest:`"$mf`"" }
    foreach ($s in $srcs) { $args += "`"$s`"" }
    $p = Start-Process -FilePath $csc -ArgumentList $args -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput "$env:TEMP\gb_csc_out.txt" -RedirectStandardError "$env:TEMP\gb_csc_err.txt"
    if ($p.ExitCode -ne 0 -or -not (Test-Path $out)) {
        throw ("csc failed: " + (Get-Content "$env:TEMP\gb_csc_err.txt", "$env:TEMP\gb_csc_out.txt" -ErrorAction SilentlyContinue | Out-String))
    }
    if (Test-Path $final) {
        try { Remove-Item $final -Force -ErrorAction Stop }
        catch {
            try { Rename-Item $final "$final.bak" -Force } catch {}
            Start-Sleep -Seconds 1
            if (Test-Path $final) { Remove-Item $final -Force -ErrorAction SilentlyContinue }
        }
    }
    Move-Item $out $final -Force -ErrorAction Stop
    if (-not (Test-Path $final)) { throw "deploy failed: target exe missing" }
    "COMPILE_OK" | Out-File $resultFile -Encoding utf8
    exit 0
} catch {
    $msg = "COMPILE_FAIL: " + $_.Exception.Message
    if ($_.ErrorDetails) { $msg += "`nDETAIL: " + $_.ErrorDetails.Message }
    $msg | Out-File $resultFile -Encoding utf8
    exit 1
}
