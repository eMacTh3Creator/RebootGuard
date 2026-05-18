# Builds RebootGuard.exe using the in-box .NET Framework C# compiler (no SDK required).
$ErrorActionPreference = 'Stop'

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\RebootGuard.cs'
$out  = Join-Path $root 'dist\RebootGuard.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    /out:"$out" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit $LASTEXITCODE)." }
Write-Host "Built: $out"
