# =============================================================================
#  Navisworks MCP Bridge - build an installable package
# =============================================================================
#  Builds MCPBridge.dll for Navisworks 2026 and 2027, assembles a standalone
#  install folder in dist\ (git-ignored) and packs it into a 7z archive:
#
#    dist\NavisworksMCP-<version>\
#        INSTALL.BAT               installer (finds MCPBridge.bundle next to it)
#        README.md
#        MCPBridge.bundle\
#            PackageContents.xml
#            Contents\server_http.py
#            Contents\2026\...     build for Navisworks 2026
#            Contents\2027\...     build for Navisworks 2027
#    dist\NavisworksMCP-<version>.7z
#
#  On the target PC: unpack the archive, close Navisworks, run INSTALL.BAT.
#  The version comes from release\MCPBridge.bundle\PackageContents.xml.
#
#  Usage:
#    .\Build-Package.ps1
#    .\Build-Package.ps1 -Versions 2026          # only one Navisworks release
#    .\Build-Package.ps1 -SevenZip "D:\7-Zip\7z.exe"
# =============================================================================

param(
    [string[]]$Versions = @("2026", "2027"),
    [string]$SevenZip = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# --- 7-Zip -------------------------------------------------------------------
if (-not $SevenZip) {
    $cmd = Get-Command 7z -ErrorAction SilentlyContinue
    if ($cmd) { $SevenZip = $cmd.Source }
    else {
        foreach ($c in "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") {
            if (Test-Path $c) { $SevenZip = $c; break }
        }
    }
}
if (-not $SevenZip -or -not (Test-Path $SevenZip)) {
    throw "7z.exe not found. Install 7-Zip or pass -SevenZip <path>."
}

# --- Version from the bundle manifest ----------------------------------------
$manifest = Join-Path $root "release\MCPBridge.bundle\PackageContents.xml"
$version = ([xml](Get-Content -Raw $manifest)).ApplicationPackage.AppVersion
if (-not $version) { throw "AppVersion not found in $manifest" }

$name    = "NavisworksMCP-$version"
$dist    = Join-Path $root "dist"
$stage   = Join-Path $dist $name
$bundle  = Join-Path $stage "MCPBridge.bundle"
$archive = Join-Path $dist "$name.7z"

Write-Host "=== Navisworks MCP Bridge $version -> $archive ===" -ForegroundColor Cyan

# --- Build -------------------------------------------------------------------
foreach ($v in $Versions) {
    Write-Host "`n--- build for Navisworks $v" -ForegroundColor Cyan
    dotnet build (Join-Path $root "NavisworksMcpAddin.csproj") -c Release "-p:NavisworksVersion=$v" -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build for Navisworks $v failed." }
    if (-not (Test-Path (Join-Path $root "bin\Release\$v\MCPBridge.dll"))) {
        throw "bin\Release\$v\MCPBridge.dll was not produced."
    }
}

# --- Stage -------------------------------------------------------------------
Write-Host "`n--- stage $stage" -ForegroundColor Cyan
if (Test-Path $stage)   { Remove-Item $stage -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }
New-Item -ItemType Directory -Force (Join-Path $bundle "Contents") | Out-Null

Copy-Item $manifest $bundle
Copy-Item (Join-Path $root "server_http.py") (Join-Path $bundle "Contents")
foreach ($v in $Versions) {
    Copy-Item (Join-Path $root "bin\Release\$v") (Join-Path $bundle "Contents\$v") -Recurse
}
Copy-Item (Join-Path $root "INSTALL.BAT") $stage
Copy-Item (Join-Path $root "README.md")   $stage

# Every DLL referenced by the manifest must be in the package.
$xml = [xml](Get-Content -Raw (Join-Path $bundle "PackageContents.xml"))
foreach ($entry in $xml.ApplicationPackage.Components.ComponentEntry) {
    $dll = Join-Path $bundle ($entry.ModuleName -replace '^\./', '')
    if (-not (Test-Path $dll)) {
        Write-Host "[warn] $($entry.ModuleName) is listed in PackageContents.xml but not built (-Versions $($Versions -join ','))" -ForegroundColor Yellow
    }
}

# --- Pack --------------------------------------------------------------------
Write-Host "`n--- pack" -ForegroundColor Cyan
& $SevenZip a -t7z -mx=9 $archive $stage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE." }

Write-Host "`nPackage contents:" -ForegroundColor Green
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    Write-Host ("  {0,-60} {1,10:N0} bytes" -f $_.FullName.Substring($stage.Length + 1), $_.Length)
}
Write-Host ("`nDone: {0} ({1:N0} bytes)" -f $archive, (Get-Item $archive).Length) -ForegroundColor Green
Write-Host "On the target PC: unpack, close Navisworks, run INSTALL.BAT."
