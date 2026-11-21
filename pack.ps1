<#
.SYNOPSIS
    Publishes the WIC codec plugin and packs it as wic-codec_<arch>.igplugin.zip.

.DESCRIPTION
    Produces the same layout as the release workflow: a single "Plugin_WicCodec" folder
    holding the native library and its manifest. ImageGlass accepts that either through
    Settings > Plugins > Add or as a manual copy into the _plugins directory.

.PARAMETER Rid
    Which runtimes to build. Defaults to both Windows architectures.

.PARAMETER Deploy
    Also copy the staged folder into %LOCALAPPDATA%\ImageGlass\_plugins for local testing.
    Requires exactly one -Rid. Close ImageGlass first: a loaded plugin's DLL is locked.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Rid = @('win-x64', 'win-arm64'),

    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pluginFolder = 'Plugin_WicCodec'

# ILCompiler shells out to vcvarsall.bat, which calls vswhere.exe by bare name. Outside a
# Developer Command Prompt that failure is captured into the linker path and the native link
# breaks with a confusing error, so make sure the Installer directory is reachable.
$installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
    $env:PATH = "$installer;$env:PATH"
}

if ($Deploy -and $Rid.Count -ne 1) {
    throw '-Deploy needs exactly one -Rid.'
}

# The manifest is the version ImageGlass shows in Settings > Plugins, so name the package after
# it rather than after the assembly version — those two are kept in step, and this is the one a
# user can check without unzipping.
$manifestPath = Join-Path $root 'source/igplugin.json'
$version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($version)) { throw "no version in $manifestPath" }

foreach ($runtime in $Rid) {
    $platform = if ($runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
    $publishDir = Join-Path $root "dist/$runtime"
    $staged = Join-Path $root "dist/staging/$runtime/$pluginFolder"

    Write-Host "`n=== publishing $runtime ===" -ForegroundColor Cyan

    # Publish never prunes its output folder, so leftovers from an older build would be packed.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'source/WicCodec.csproj') `
        --configuration Release --runtime $runtime -p:Platform=$platform --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $runtime" }

    # Ship only what the host loads: no debug symbols, no IntelliSense docs.
    if (Test-Path $staged) { Remove-Item $staged -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    Get-ChildItem $publishDir -File |
        Where-Object { $_.Extension -notin '.pdb', '.xml' } |
        Copy-Item -Destination $staged

    foreach ($required in 'WicCodec.dll', 'igplugin.json') {
        if (-not (Test-Path (Join-Path $staged $required))) {
            throw "package for $runtime is missing $required"
        }
    }

    $zip = Join-Path $root "dist/wic-codec_${version}_$runtime.igplugin.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $staged -DestinationPath $zip

    $size = [math]::Round((Get-Item $zip).Length / 1KB)
    Write-Host "packed $zip ($size KB)" -ForegroundColor Green
    Get-ChildItem $staged | Select-Object Name, Length | Format-Table -AutoSize

    if ($Deploy) {
        $target = Join-Path $env:LOCALAPPDATA "ImageGlass/_plugins/$pluginFolder"

        # A loaded plugin's DLL is locked, so move the old folder aside instead of deleting in
        # place: a half-failed recursive delete can strip the manifest and leave the plugin in a
        # state the host cannot even describe.
        if (Test-Path $target) {
            $retired = "$target.old-$(Get-Random)"
            Move-Item $target $retired
            try { Remove-Item $retired -Recurse -Force } catch {
                Write-Warning "left $retired behind (ImageGlass may still have it open)"
            }
        }

        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item "$staged/*" $target -Recurse
        Write-Host "deployed to $target" -ForegroundColor Green
    }
}
