# =============================================================================
#  Navisworks MCP Bridge - build an installable package
# =============================================================================
#  Builds MCPBridge.dll for Navisworks 2026 and 2027 and the MCP server, then
#  assembles a standalone install folder in dist\ (git-ignored) and packs it:
#
#    dist\NavisworksMCP-<version>\
#        INSTALL.BAT               installer EN/RU (finds MCPBridge.bundle next to it)
#        RELEASE.md                what the release is, requirements, installation (RU + EN)
#        README.md
#        MCPBridge.bundle\
#            PackageContents.xml   from installer\PackageContents.xml
#            Contents\2026\...     plugin built for Navisworks 2026
#            Contents\2027\...     plugin built for Navisworks 2027
#            Contents\Server\...   navis_mcp_server.dll, started as "dotnet.exe navis_mcp_server.dll"
#    dist\NavisworksMCP-<version>.7z   (.zip when 7-Zip is not installed)
#
#  Archives of earlier versions stay in dist\; only the unpacked folder of an
#  earlier version is replaced by the current one.
#
#  The version has one source: the Version constant in MCPBridgePlugin.cs. The
#  bundle manifest, the MCP server and the release notes get the same number.
#
#  Usage:
#    .\Build-Package.ps1
#    .\Build-Package.ps1 -Deploy                 # also install it here (INSTALL.BAT I), AI clients untouched
#    .\Build-Package.ps1 -SevenZip "D:\7-Zip\7z.exe"
# =============================================================================

param(
    [switch]$Deploy,
    [string]$SevenZip = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$years = "2026", "2027"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# --- Version -----------------------------------------------------------------
$m = Select-String -Path (Join-Path $root "MCPBridgePlugin.cs") -Pattern 'private const string Version = "([^"]+)"'
if (-not $m) { throw "Version constant not found in MCPBridgePlugin.cs" }
$version = $m.Matches[0].Groups[1].Value

$name    = "NavisworksMCP-$version"
$dist    = Join-Path $root "dist"
$stage   = Join-Path $dist $name
$bundle  = Join-Path $stage "MCPBridge.bundle"

Write-Host "=== Navisworks MCP Bridge $version ===" -ForegroundColor Cyan

# --- Build -------------------------------------------------------------------
foreach ($y in $years) {
    Write-Host "`n--- plugin for Navisworks $y" -ForegroundColor Cyan
    dotnet build (Join-Path $root "NavisworksMcpAddin.csproj") -c Release "-p:NavisworksVersion=$y" -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build for Navisworks $y failed." }
}
Write-Host "`n--- MCP server" -ForegroundColor Cyan
$serverOut = Join-Path $root "bin\Release\Server"
if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
dotnet publish (Join-Path $root "server\NavisMcpServer\NavisMcpServer.csproj") -c Release "-p:Version=$version" -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed." }

# --- Stage -------------------------------------------------------------------
Write-Host "`n--- stage $stage" -ForegroundColor Cyan
Get-ChildItem $dist -Filter "NavisworksMCP-*" -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $bundle "Contents\Server") | Out-Null

$manifest = [IO.File]::ReadAllText((Join-Path $root "installer\PackageContents.xml")).Replace("@VERSION@", $version)
[IO.File]::WriteAllText((Join-Path $bundle "PackageContents.xml"), $manifest, $utf8)
foreach ($y in $years) {
    Copy-Item (Join-Path $root "bin\Release\$y") (Join-Path $bundle "Contents\$y") -Recurse
}
Copy-Item (Join-Path $serverOut "*") (Join-Path $bundle "Contents\Server")

# INSTALL.BAT must be UTF-8 without BOM with CRLF line ends for cmd.exe.
$bat = [IO.File]::ReadAllText((Join-Path $root "installer\INSTALL.BAT")) -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $stage "INSTALL.BAT"), $bat, $utf8)
$notes = [IO.File]::ReadAllText((Join-Path $root "installer\RELEASE.md")).Replace("@VERSION@", $version)
[IO.File]::WriteAllText((Join-Path $stage "RELEASE.md"), $notes, $utf8)
Copy-Item (Join-Path $root "README.md") $stage

# Every DLL the manifest names must be in the package.
$xml = [xml]$manifest
foreach ($entry in $xml.ApplicationPackage.Components.ComponentEntry) {
    if (-not (Test-Path (Join-Path $bundle ($entry.ModuleName -replace '^\./', '')))) {
        throw "$($entry.ModuleName) is listed in PackageContents.xml but missing from the package."
    }
}
foreach ($f in "navis_mcp_server.dll", "navis_mcp_server.runtimeconfig.json") {
    if (-not (Test-Path (Join-Path $bundle "Contents\Server\$f"))) { throw "Contents\Server\$f is missing." }
}

# --- Pack --------------------------------------------------------------------
if (-not $SevenZip) {
    $cmd = Get-Command 7z -ErrorAction SilentlyContinue
    if ($cmd) { $SevenZip = $cmd.Source }
    else {
        foreach ($c in "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") {
            if (Test-Path $c) { $SevenZip = $c; break }
        }
    }
}
Write-Host "`n--- pack" -ForegroundColor Cyan
if ($SevenZip -and (Test-Path $SevenZip)) {
    $archive = Join-Path $dist "$name.7z"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    & $SevenZip a -t7z -mx=9 $archive $stage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE." }
} else {
    $archive = Join-Path $dist "$name.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path $stage -DestinationPath $archive
}

Write-Host "`nPackage contents:" -ForegroundColor Green
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    Write-Host ("  {0,-64} {1,10:N0} bytes" -f $_.FullName.Substring($stage.Length + 1), $_.Length)
}
Write-Host ("`nDone: {0} ({1:N0} bytes)" -f $archive, (Get-Item $archive).Length) -ForegroundColor Green
Write-Host "On the target PC: unpack, close Navisworks, run INSTALL.BAT."

if ($Deploy) {
    Write-Host "`n--- install here (AI clients untouched)" -ForegroundColor Cyan
    $env:NAVMCP_NO_CLIENTS = "1"
    try { & cmd.exe /c "`"$stage\INSTALL.BAT`" I EN" }
    finally { Remove-Item Env:NAVMCP_NO_CLIENTS -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "INSTALL.BAT failed with code $LASTEXITCODE" }
}
