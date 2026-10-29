# Builds the AI Usage widget and registers it with Windows (Developer Mode).
# WidBar picks the new build up on its own; re-run after every change.
param([switch]$NoRestart)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'src\WidBar.AiUsage\WidBar.AiUsage.csproj'
$publish = Join-Path $root 'artifacts\publish'
$layout  = Join-Path $root 'artifacts\layout'

$dotnetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}

dotnet publish $project -c Release -o $publish --nologo
if ($LASTEXITCODE) { throw "publish failed ($LASTEXITCODE)" }

# The running widget locks its files and WidBar relaunches it straight away,
# so stop WidBar too for the copy and start it again at the end.
$widbarWasRunning = [bool](Get-Process WidBar -ErrorAction SilentlyContinue)
Get-Process WidBar, WidBar.AiUsage -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process WidBar, WidBar.AiUsage -ErrorAction SilentlyContinue | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue

# Killed processes can hold their files for a moment after they exit.
for ($try = 1; Test-Path $layout; $try++) {
    try { Remove-Item $layout -Recurse -Force -ErrorAction Stop }
    catch { if ($try -ge 10) { throw }; Start-Sleep -Seconds 1 }
}
New-Item -ItemType Directory -Force "$layout\Public", "$layout\PublicSystem", "$layout\Images" | Out-Null
Copy-Item "$publish\*" $layout -Recurse
# Publish leaves out the resource index (compiled XAML); take it from the build output.
$pri = Get-ChildItem (Join-Path $root 'src\WidBar.AiUsage\bin\x64\Release') -Recurse -Filter WidBar.AiUsage.pri |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item $pri.FullName "$layout\resources.pri"
Copy-Item (Join-Path $root 'src\WidBar.AiUsage\obj\widbar\plugin.json') "$layout\Public\plugin.json"
# The second widget (System) runs from the same exe; its manifest is hand-written.
Copy-Item (Join-Path $root 'package\system.plugin.json') "$layout\PublicSystem\plugin.json"
Copy-Item (Join-Path $root 'package\AppxManifest.xml') $layout

foreach ($name in 'StoreLogo', 'AppIcon', 'MediumTile', 'SmallTile', 'WideTile', 'LargeTile') {
    Copy-Item (Join-Path $root "package\Images\$name.scale-100.png") "$layout\Images\$name.png"
}

Add-AppxPackage -Register "$layout\AppxManifest.xml" -ForceApplicationShutdown
Get-AppxPackage Blitz.WidBarAiUsage | Select-Object Name, Version, InstallLocation

if ($widbarWasRunning -and -not $NoRestart) {
    Start-Process 'shell:AppsFolder\30113AndreaDelBello.WidBar-WidgetTaskbarsystem_2mj5svk4jk01m!App'
}
