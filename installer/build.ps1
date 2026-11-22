# Builds out\Fetchr-Setup-<version>.exe
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.2.0]
# Needs: .NET 10 SDK, Inno Setup 6, and 7-Zip installed on the build machine (its 7z.exe/7z.dll are bundled).
param([string]$Version = "0.2.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "out"
$pub = Join-Path $out "publish"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force $pub | Out-Null

# Self-contained: the .NET runtime ships inside, so the other PC needs nothing installed.
# The host is published on its own too, so it is self-contained as well (a referenced exe would not be).
foreach ($proj in "src\Fetchr.App", "src\Fetchr.Host") {
    & $dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -o $pub "-p:Version=$Version" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# Bundle 7-Zip (LGPL + unRAR restriction) so rar/7z work without installing anything.
$sz = (Get-ItemProperty "HKLM:\SOFTWARE\7-Zip" -ErrorAction SilentlyContinue).Path64
if (-not $sz) { $sz = "C:\Program Files\7-Zip\" }
New-Item -ItemType Directory -Force (Join-Path $pub "7-Zip") | Out-Null
foreach ($f in "7z.exe", "7z.dll", "License.txt") { Copy-Item (Join-Path $sz $f) (Join-Path $pub "7-Zip") }
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") $pub

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found" }
& $iscc /Q "/DMyVersion=$Version" "/DPublishDir=$pub" "/O$out" (Join-Path $PSScriptRoot "fetchr.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-Item (Join-Path $out "Fetchr-Setup-$Version.exe")
