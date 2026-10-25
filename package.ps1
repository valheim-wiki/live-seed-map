# Release packages built against the installed client in place:
#   dist/thevalheimwiki-LiveSeedMap-<version>.zip  Thunderstore (r2modman, Thunderstore Mod Manager, Gale), built by tcli
#   dist/LiveSeedMap-<version>.zip                 BepInEx layout (Nexus Mods and Vortex, CurseForge, manual install)
#   dist/SHA256SUMS.txt
param([string]$ValheimRoot = '', [string]$Managed = '', [string]$BepInExCore = '')
$ErrorActionPreference = 'Stop'
$root = & git -C $PSScriptRoot rev-parse --show-toplevel
if ($LASTEXITCODE -or -not $root) { throw 'Run this script from its Git checkout.' }
$root = [IO.Path]::GetFullPath($root.Trim())
$mod = Join-Path $PSScriptRoot 'LiveSeedMap'
$dist = Join-Path $PSScriptRoot 'dist'

$version = ([xml](Get-Content (Join-Path $mod 'LiveSeedMap.csproj'))).Project.PropertyGroup.Version
if (-not (Select-String -Path (Join-Path $mod 'Plugin.cs') -SimpleMatch "Version = `"$version`"" -Quiet)) { throw "Plugin.cs does not carry version $version" }
if (-not (Select-String -Path (Join-Path $mod 'thunderstore.toml') -SimpleMatch "versionNumber = `"$version`"" -Quiet)) { throw "thunderstore.toml does not carry version $version" }
if (-not (Select-String -Path (Join-Path $mod 'CHANGELOG.md') -SimpleMatch "## $version" -Quiet)) { throw "CHANGELOG.md has no $version entry" }

$env:DOTNET_CLI_HOME = Join-Path $root 'data\tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root 'data\tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$build = Join-Path $mod 'bin\Release'
$buildArgs = @('build', (Join-Path $mod 'LiveSeedMap.csproj'), '-c', 'Release', '--nologo', '-v', 'q', '-o', $build)
if ($ValheimRoot) { $buildArgs += "-p:ValheimRoot=$ValheimRoot" }
if ($Managed) { $buildArgs += "-p:Managed=$Managed" }
if ($BepInExCore) { $buildArgs += "-p:BepInExCore=$BepInExCore" }
& dotnet @buildArgs
if ($LASTEXITCODE) { throw 'build failed' }

$tools = Join-Path $root 'data\tools\tcli'
if (-not (Test-Path (Join-Path $tools 'tcli.exe'))) { & dotnet tool install tcli --version 0.2.4 --tool-path $tools | Out-Null }
$tcliData = Join-Path $root 'data\tools\tcli-config'
New-Item -ItemType Directory -Force $tcliData | Out-Null
Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null
Push-Location $mod
try { & (Join-Path $tools 'tcli.exe') build --config-path thunderstore.toml --tcli-directory $tcliData; if ($LASTEXITCODE) { throw 'tcli build failed' } }
finally { Pop-Location }

# Entry names use forward slashes; PowerShell 5.1's Compress-Archive writes backslashes that other unzip tools keep.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$manual = [IO.Compression.ZipFile]::Open((Join-Path $dist "LiveSeedMap-$version.zip"), 'Create')
foreach ($file in (Join-Path $build 'LiveSeedMap.dll'), (Join-Path $mod 'README.md'), (Join-Path $mod 'CHANGELOG.md')) {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($manual, $file, 'BepInEx/plugins/LiveSeedMap/' + (Split-Path $file -Leaf)) | Out-Null
}
$manual.Dispose()

# LF line ends so `sha256sum -c` and `shasum -a 256 -c` read the names; Set-Content would write CRLF.
$sums = (Get-ChildItem $dist -Filter *.zip | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }) -join "`n"
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), $sums + "`n", [Text.Encoding]::ASCII)
Get-ChildItem $dist -Filter *.zip | ForEach-Object {
    $zip = [IO.Compression.ZipFile]::OpenRead($_.FullName)
    "$($_.Name) ($($_.Length) bytes): " + (($zip.Entries | ForEach-Object { $_.FullName }) -join ', ')
    $zip.Dispose()
}
Get-Content (Join-Path $dist 'SHA256SUMS.txt')
