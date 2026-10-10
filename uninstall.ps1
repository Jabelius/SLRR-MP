# SLRR-MP uninstall. Run with the game closed. Restores originals from backup-original\.
param([string]$Game)
$ErrorActionPreference = 'Stop'
# buddy zip: this folder sits inside the game folder
if (-not $Game) { $Game = Split-Path $PSScriptRoot }
if (-not (Test-Path (Join-Path $Game 'StreetLegal_Redline.exe'))) {
	# elsewhere (dev checkout): find the game in Steam's library folders
	$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
	$vdf = if ($steam) { Join-Path $steam 'steamapps\libraryfolders.vdf' }
	if ($vdf -and (Test-Path $vdf)) {
		$Game = Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' |
			ForEach-Object { Join-Path ($_.Matches[0].Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Street Legal Racing Redline' } |
			Where-Object { Test-Path (Join-Path $_ 'StreetLegal_Redline.exe') } | Select-Object -First 1
	}
}
if (-not (Test-Path (Join-Path $Game 'StreetLegal_Redline.exe'))) { throw 'Game not found. Put the SLRR-MP folder inside the game folder.' }
if (Get-Process StreetLegal_Redline -ErrorAction SilentlyContinue) { throw 'Close the game first.' }
$bak = Join-Path $PSScriptRoot 'backup-original'
foreach ($f in 'sl\Scripts\game\src\City.java', 'sl\Scripts\game\City.class') {
	Copy-Item (Join-Path $bak $f) (Join-Path $Game $f) -Force
}
foreach ($f in 'dinput8.dll', 'slrr-mp.ini', 'slrr-mp.log', 'sl\Scripts\game\src\MpSync.java', 'sl\Scripts\game\MpSync.class', 'sl\Scripts\game\MpGhost.class', 'sl\Scripts\game\src\MpSpike.java', 'sl\Scripts\game\MpSpike.class', 'save\temp\mp_own.car', 'save\temp\mp', 'slrr-mp-cars') {
	Remove-Item (Join-Path $Game $f) -Recurse -ErrorAction SilentlyContinue
}
"Uninstalled from $Game."
