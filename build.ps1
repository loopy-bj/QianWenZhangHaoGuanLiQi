# Qianwen Account Switcher - build script
# Uses the csc.exe compiler shipped with .NET Framework (no SDK required)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe not found' }

$sources = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter '*.cs' |
    ForEach-Object { $_.FullName }
if (-not $sources) { throw 'No .cs files found under src' }
Write-Host ("Source files: " + $sources.Count)

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll'
)

$exePath = Join-Path $root 'QianwenSwitcher.exe'
$rspPath = Join-Path $root 'build.rsp'
$lines = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/utf8output',
    ('/out:"' + $exePath + '"')
)
foreach ($r in $refs) { $lines += '/reference:' + $r }
foreach ($s in $sources) { $lines += '"' + $s + '"' }
Set-Content -Path $rspPath -Value $lines -Encoding ASCII

Write-Host 'Building QianwenSwitcher.exe ...' -ForegroundColor Cyan
try {
    & $csc "@$rspPath"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Write-Host ('Build OK: ' + $exePath) -ForegroundColor Green
}
finally {
    Remove-Item $rspPath -Force -ErrorAction SilentlyContinue
}
