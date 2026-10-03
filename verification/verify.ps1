param([string]$GameDir = $env:APOCALYPTER_GAME_DIR)
$ErrorActionPreference = 'Stop'
if (!$GameDir) { throw 'Pass -GameDir or set APOCALYPTER_GAME_DIR.' }
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputRoot = Join-Path $taskRoot 'npcai/.verification'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (!(Test-Path (Join-Path $taskRoot 'npcai/source-baseline.json'))) {
    Get-ChildItem (Join-Path $taskRoot 'apocaraider') -Recurse -Force -File |
        Where-Object { $_.FullName -notmatch '\\.git\\' } |
        ForEach-Object { [pscustomobject]@{ Path=$_.FullName; Length=$_.Length; Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } |
        ConvertTo-Json -Depth 3 | Set-Content (Join-Path $taskRoot 'npcai/source-baseline.json')
}
if (!(Test-Path (Join-Path $outputRoot 'baseline/bin/Apocaraider.dll'))) {
    $baselineRoot = (Join-Path $outputRoot 'baseline').Replace('\','/')
    dotnet build (Join-Path $taskRoot 'apocaraider/Apocaraider.csproj') -c Release "-p:GameDir=$GameDir" "-p:BaseIntermediateOutputPath=$baselineRoot/obj/" "-p:MSBuildProjectExtensionsPath=$baselineRoot/obj/" "-p:OutputPath=$baselineRoot/bin/" "-p:PluginDir=$baselineRoot/package/" -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Read-only original baseline build failed.' }
}
foreach ($entry in @(@('womenofwasteland','WomenOfWasteland'), @('gunplayhud','GunplayHUD'), @('gunplay','Gunplay'), @('npcai','NPCAI'))) {
    dotnet build (Join-Path $taskRoot ($entry[0] + '/' + $entry[1] + '.csproj')) -c Release "-p:GameDir=$GameDir" -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($entry[0])" }
}
dotnet build (Join-Path $PSScriptRoot 'Verifier.csproj') -c Release "-p:GameDir=$GameDir" -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Verifier build failed.' }
$verifier = Join-Path $PSScriptRoot 'bin/Release/Verifier.exe'
1..15 | ForEach-Object {
    & $verifier $taskRoot $GameDir $_
    if ($LASTEXITCODE -ne 0) { throw "Combination $_ failed." }
}
& $verifier $taskRoot $GameDir ranges
if ($LASTEXITCODE -ne 0) { throw 'Range parity failed.' }
& $verifier $taskRoot $GameDir hooks
if ($LASTEXITCODE -ne 0) { throw 'Game hook inventory failed.' }
python (Join-Path $PSScriptRoot 'source_audit.py')
if ($LASTEXITCODE -ne 0) { throw 'Source audit failed.' }
