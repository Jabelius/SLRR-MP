SLRR-MP - drive around Valo City together (Street Legal Racing: Redline v2.3.1)

You see your buddies' cars in the City. Everyone keeps their own career,
money and garage. Races and traffic stay single-player.

INSTALL
1. Close the game.
2. In Steam: right-click Street Legal Racing Redline > Manage > Browse local files.
3. Extract this zip into that folder, so you get a "SLRR-MP" folder next to
   StreetLegal_Redline.exe.
4. Open the SLRR-MP folder and double-click install.bat.
   The original game files it changes are saved in SLRR-MP\backup-original.

PLAY
1. Start SLRR-MP\SlrrMp.exe (Windows may warn about an unknown app:
   More info > Run anyway). Allow it through the firewall if asked.
2. One player picks Host. The others pick Join and type the host's IP.
   The host must forward UDP port 27650 on their router to their PC,
   or everyone uses the same VPN (Tailscale, ZeroTier, Radmin VPN) and
   joins the host's VPN IP.
3. Keep SlrrMp.exe open, start the game and drive out into the City.
   Other players show up as stock copies of their car model.

Solo test: pick Fake in SlrrMp.exe. A copy of your car follows you 2 s
behind, 5 m to the side.

SETTINGS
SlrrMp.json (next to SlrrMp.exe, created on first start): ports, send rate.
slrr-mp.ini (in the game folder): bridge port, smoothing delay, timeout.

UNINSTALL
1. Close the game.
2. Double-click SLRR-MP\uninstall.bat.
3. Delete the SLRR-MP folder.
