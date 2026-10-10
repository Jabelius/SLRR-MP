# SLRR-MP install. Run with the game closed. Originals go to backup-original\ once.
param([string]$Game)
$ErrorActionPreference = 'Stop'
# buddy zip: this folder sits inside the game folder; dev repo: default install
if (-not $Game) { $Game = Split-Path $PSScriptRoot }
if (-not (Test-Path (Join-Path $Game 'StreetLegal_Redline.exe'))) { $Game = '<game folder>' }
if (-not (Test-Path (Join-Path $Game 'StreetLegal_Redline.exe'))) { throw 'Game not found. Put the SLRR-MP folder inside the game folder.' }
if (Get-Process StreetLegal_Redline -ErrorAction SilentlyContinue) { throw 'Close the game first.' }
$bak = Join-Path $PSScriptRoot 'backup-original'
$cityJava = 'sl\Scripts\game\src\City.java'
$backed = 'sl\Scripts\game\src\City.java', 'sl\Scripts\game\City.class'
$dll = Join-Path $Game 'dinput8.dll'

if ((Test-Path $dll) -and -not (Test-Path (Join-Path $bak $cityJava))) { throw "$dll exists and is not ours, aborting." }
foreach ($f in $backed) {
	$b = Join-Path $bak $f
	if (-not (Test-Path $b)) {
		New-Item -ItemType Directory -Force (Split-Path $b) | Out-Null
		Copy-Item (Join-Path $Game $f) $b
	}
}

# Hooks are applied to the pristine backup each time, so reinstalling is safe.
$enc = [Text.Encoding]::GetEncoding(28591)
$city = [IO.File]::ReadAllText((Join-Path $bak $cityJava), $enc)
function Hook([string]$anchor, [string]$insert) {
	$i = $script:city.IndexOf($anchor)
	if ($i -lt 0 -or $i -ne $script:city.LastIndexOf($anchor)) { throw "City.java anchor missing or not unique: $anchor" }
	$script:city = $script:city.Insert($i + $anchor.Length, $insert)
}
Hook "public class City extends Track`r`n{" "`r`n`tMpSync mpSync; //SLRR-MP"
Hook 'super.enter(prev_state); //waits for loading to finish' "`r`n`t`tmpSync = new MpSync(map, player.car); //SLRR-MP"
Hook "public void exit(GameState next_state)`r`n`t{" "`r`n`t`tif(mpSync) { mpSync.stop(); mpSync = null; } //SLRR-MP"
[IO.File]::WriteAllText((Join-Path $Game $cityJava), $city, $enc)

Remove-Item (Join-Path $Game 'sl\Scripts\game\src\MpSpike.java'), (Join-Path $Game 'sl\Scripts\game\MpSpike.class') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'mod\MpSync.java') (Join-Path $Game 'sl\Scripts\game\src\MpSync.java') -Force
$ini = Join-Path $Game 'slrr-mp.ini'
if (-not (Test-Path $ini)) { Copy-Item (Join-Path $PSScriptRoot 'mod\slrr-mp.ini') $ini }
Copy-Item (Join-Path $PSScriptRoot 'native\dinput8.dll') $dll -Force
"Installed into $Game."
