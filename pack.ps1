# Builds dist\SLRR-MP.zip for buddies: extract into the game folder, run SLRR-MP\install.bat.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist'
$pkg = Join-Path $out 'pkg\SLRR-MP'

Push-Location (Join-Path $root 'native')
zig cc -target x86-windows-gnu -O2 -shared -s -o dinput8.dll bridge.c dinput8.def -lws2_32
if ($LASTEXITCODE) { throw 'zig build failed' }
Remove-Item bridge.lib -ErrorAction SilentlyContinue
Pop-Location

# local package cache only: no downloads
dotnet publish (Join-Path $root 'app') -c Release -r win-x64 -o (Join-Path $out 'app') --source (Join-Path $env:USERPROFILE '.nuget\packages')
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

Remove-Item (Join-Path $out 'pkg') -Recurse -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $pkg 'native'), (Join-Path $pkg 'mod') | Out-Null
Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1'), (Join-Path $root 'mod\README-INSTALL.txt') $pkg
Copy-Item (Join-Path $out 'app\SlrrMp.exe') $pkg
Copy-Item (Join-Path $root 'native\dinput8.dll') (Join-Path $pkg 'native')
Copy-Item (Join-Path $root 'mod\MpSync.java'), (Join-Path $root 'mod\slrr-mp.ini') (Join-Path $pkg 'mod')
foreach ($s in 'install', 'uninstall') {
	Set-Content (Join-Path $pkg "$s.bat") "@powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0$s.ps1`"`r`n@pause`r`n" -NoNewline -Encoding ascii
}

$zip = Join-Path $out 'SLRR-MP.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive (Join-Path $out 'pkg\SLRR-MP') $zip
"Built $zip"
