# Proof run on NE8K: the live link inside the installed client, driven by the harness session.
# BepInEx loads from the pack's ignored data/mod-runtime through doorstop's --doorstop-target-assembly;
# nothing is added to or changed in the Steam install (DR-2026-09-22-no-game-copies).
# The game needs the Steam client of the console session, which a process started from SSH (session 0)
# cannot see, so it starts there through an interactive scheduled task registered and removed here.
# -Startup bounds each wait on the harness (its player, then its own quit) so a stalled game never outlives this script.
# -Stops names a file under the pack root with one survey stop per line ("x z fish"); the harness writes fish-native.jsonl.
param([string]$Seed = 'Fjord8Cod', [string]$Stops = '', [int]$Capture = 70, [int]$Startup = 600)
$Port = 45892
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
$rt = Join-Path $root 'data\mod-runtime'
$out = Join-Path $root 'tmp\live-run'
$pack = Join-Path $rt 'BepInExPack_Valheim-5.4.2351.zip'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

if (-not (Test-Path (Join-Path $rt 'BepInEx\core\BepInEx.Preloader.dll'))) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($pack)
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -like 'BepInExPack_Valheim/BepInEx/*' -and $entry.Name) {
            $dest = Join-Path $rt $entry.FullName.Substring('BepInExPack_Valheim/'.Length)
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        }
    }
    $zip.Dispose()
}

$env:DOTNET_CLI_HOME = Join-Path $root 'data\tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root 'data\tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$plugins = Join-Path $rt 'BepInEx\plugins'
New-Item -ItemType Directory -Force $plugins | Out-Null
foreach ($project in 'LiveSeedMap\LiveSeedMap.csproj', 'harness\LiveHarness.csproj') {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    & dotnet build (Join-Path $PSScriptRoot $project) -c Release --nologo -v q -o (Join-Path $rt "build\$name") | Out-File (Join-Path $out "build-$name.log")
    if ($LASTEXITCODE) { Get-Content (Join-Path $out "build-$name.log") | Select-String 'error' ; throw "build failed: $project" }
    Copy-Item (Join-Path $rt "build\$name\$name.dll") $plugins -Force
}

Remove-Item -Recurse -Force (Join-Path $rt 'saves'), (Join-Path $rt 'BepInEx\LogOutput.log'), (Join-Path $rt 'fish-native.jsonl') -ErrorAction SilentlyContinue
& reg.exe export 'HKCU\Software\IronGate\Valheim' (Join-Path $out 'prefs-before.reg') /y | Out-Null

if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Valheim is already running on this host; the harness will not start a second client' }
$preloader = Join-Path $rt 'BepInEx\core\BepInEx.Preloader.dll'
Set-Content -Path (Join-Path $rt 'harness.txt') -Value (@($Seed) + $(if ($Stops) { Get-Content (Join-Path $root $Stops) | Select-Object -Skip 1 } else { @() }))
$task = 'thevalheim-live-harness'
$arguments = "-batchmode -nographics -logFile `"$out\player.log`" --doorstop-enabled true --doorstop-target-assembly `"$preloader`""
$action = New-ScheduledTaskAction -Execute (Join-Path $game 'valheim.exe') -Argument $arguments -WorkingDirectory $game
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
$started = Get-Date
Start-ScheduledTask -TaskName $task
$process = $null
while (-not $process -and ((Get-Date) - $started).TotalSeconds -lt $Startup) {
    Start-Sleep -Seconds 1
    $process = Get-Process valheim -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $started.AddSeconds(-1) } | Select-Object -First 1
}
if (-not $process) { Unregister-ScheduledTask -TaskName $task -Confirm:$false; throw 'the scheduled game did not start' }
"pid $($process.Id) session $($process.SessionId)"
$log = Join-Path $rt 'BepInEx\LogOutput.log'
try {
    while (-not $process.HasExited -and ((Get-Date) - $started).TotalSeconds -lt $Startup -and -not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'harness: ready' -Quiet))) { Start-Sleep -Seconds 2 }
    'startup seconds: ' + [int]((Get-Date) - $started).TotalSeconds
    if ($process.HasExited) { 'exited early' }
    elseif (-not (Select-String -Path $log -Pattern 'harness: ready' -Quiet)) { 'no player within -Startup' }
    else {
        $base = "http://127.0.0.1:$Port"
        & curl.exe -s "$base/v1/state?range=100" -o (Join-Path $out 'state.json')
        'foreign origin: ' + (& curl.exe -s -o NUL -w '%{http_code}' -H 'Origin: https://example.com' "$base/v1/state")
        'foreign host: ' + (& curl.exe -s -o NUL -w '%{http_code}' -H "Host: example.com:$Port" "$base/v1/state")
        'site origin: ' + (& curl.exe -s -D - -o NUL -H 'Origin: https://thevalheim.wiki' "$base/v1/state" | Select-String 'HTTP/|Access-Control' | ForEach-Object { $_.Line.Trim() }) -join ' | '
        'preflight: ' + (& curl.exe -s -D - -o NUL -X OPTIONS -H 'Origin: https://thevalheim.wiki' -H 'Access-Control-Request-Private-Network: true' "$base/v1/stream" | Select-String 'HTTP/|Access-Control' | ForEach-Object { $_.Line.Trim() }) -join ' | '
        & curl.exe -sN --max-time $Capture "$base/v1/stream?range=100" -o (Join-Path $out 'stream.txt')
        $process.WaitForExit(1000 * $Startup) | Out-Null
    }
}
finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(); 'killed own process' }
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    Remove-Item (Join-Path $rt 'harness.txt') -ErrorAction SilentlyContinue
    Copy-Item $log (Join-Path $out 'bepinex.log') -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $rt 'fish-native.jsonl') $out -ErrorAction SilentlyContinue
    & reg.exe export 'HKCU\Software\IronGate\Valheim' (Join-Path $out 'prefs-after.reg') /y | Out-Null
    # Unity counts sessions in the game's preferences on every launch; the owner's values go back as they were.
    & cmd.exe /c "reg import `"$(Join-Path $out 'prefs-before.reg')`" 2>nul"
}
& reg.exe export 'HKCU\Software\IronGate\Valheim' (Join-Path $out 'prefs-restored.reg') /y | Out-Null
'prefs as before: ' + ((Get-FileHash (Join-Path $out 'prefs-before.reg')).Hash -eq (Get-FileHash (Join-Path $out 'prefs-restored.reg')).Hash)
'changed by the run: ' + ((Compare-Object (Get-Content (Join-Path $out 'prefs-before.reg')) (Get-Content (Join-Path $out 'prefs-after.reg')) | Where-Object SideIndicator -eq '=>' | ForEach-Object { ($_.InputObject -split '=')[0] }) -join ', ')
Select-String -Path (Join-Path $out 'bepinex.log') -Pattern 'harness:|LiveSeedMap|Listening|Error|Exception' | Select-Object -First 40 | ForEach-Object { $_.Line }
