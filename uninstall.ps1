# SLRR-MP uninstall. Run with the game closed. Restores originals from backup-original\.
param([string]$Game = '<game folder>')
$ErrorActionPreference = 'Stop'
if (Get-Process StreetLegal_Redline -ErrorAction SilentlyContinue) { throw 'Close the game first.' }
$bak = Join-Path $PSScriptRoot 'backup-original'
foreach ($f in 'sl\Scripts\game\src\City.java', 'sl\Scripts\game\City.class') {
	Copy-Item (Join-Path $bak $f) (Join-Path $Game $f) -Force
}
foreach ($f in 'dinput8.dll', 'slrr-mp.ini', 'slrr-mp.log', 'sl\Scripts\game\src\MpSync.java', 'sl\Scripts\game\MpSync.class', 'sl\Scripts\game\MpGhost.class', 'sl\Scripts\game\src\MpSpike.java', 'sl\Scripts\game\MpSpike.class') {
	Remove-Item (Join-Path $Game $f) -ErrorAction SilentlyContinue
}
'Uninstalled.'
