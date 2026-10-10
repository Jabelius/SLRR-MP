// SlrrMp: relays SLRR city car states between the game's dinput8.dll bridge and other players.
//   SlrrMp host [--name N]              host on UDP Config.Port
//   SlrrMp join <ip[:port]> [--name N]  join a host
//   SlrrMp fake                         offline: your own trail replayed as a second car
//   --echo (join)                       send the host's car back as yours, offset: tests the full network path
//   --bridge <port>                     local game bridge port (run a second instance without a game)
// Type a line + Enter to chat; it shows up in everyone's game. Status is in the window title.
// Updates: on start, a newer GitHub release (Updater.DefaultRepo or UpdateRepo in SlrrMp.json) is offered Y/N.
// Car look: each player's saved car (main file + its .1 .2 ... part files, bundled) is pulled in chunks by
// content hash (K announce, G request, D data) and unpacked for the bridge as slrr-mp-cars/<hash>.car[.n].
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

var cfg = Config.Load(Path.Combine(AppContext.BaseDirectory, "SlrrMp.json"));
var argl = args.ToList();
string? Opt(string key)
{
	int i = argl.IndexOf(key);
	if (i < 0 || i + 1 >= argl.Count) return null;
	var v = argl[i + 1];
	argl.RemoveRange(i, 2);
	return v;
}
await Updater.Run(cfg.UpdateRepo, args);
bool echo = argl.Remove("--echo");
var name = Proto.CleanName(Opt("--name") ?? Environment.UserName);
int bridgePort = int.Parse(Opt("--bridge") ?? cfg.BridgePort.ToString());
var mode = argl.ElementAtOrDefault(0)?.ToLowerInvariant();
var target = argl.ElementAtOrDefault(1);
if (mode is not ("host" or "join" or "fake"))
{
	Console.Write("Your name: ");
	name = Proto.CleanName(Console.ReadLine() ?? name);
	Console.Write("[H]ost, [J]oin or [F]ake peer test? ");
	mode = (Console.ReadLine() ?? "").Trim().ToLowerInvariant() switch { "j" or "join" => "join", "f" or "fake" => "fake", _ => "host" };
	if (mode == "join") { Console.Write("Host IP: "); target = Console.ReadLine(); }
}

var clock = Stopwatch.StartNew();
double Now() => clock.Elapsed.TotalSeconds;
var gate = new object();
int rx = 0, tx = 0;

var bridge = Proto.Udp(new IPEndPoint(IPAddress.Loopback, bridgePort));
IPEndPoint? game = null;
double gameSeen = -99;
void ToGame(string msg)
{
	if (game == null) return;
	bridge.Send(Encoding.ASCII.GetBytes(msg), game);
}

UdpClient? net = null;
IPEndPoint? hostEp = null;
var peers = new Dictionary<IPEndPoint, Peer>();
int nextId = 1;
double ping = -1, lastSent = -99;
bool warned = false;
var trail = new Queue<(double at, string state)>();
var cars = new CarStore();
string? gameDir = null, ownHash = null;
var carOf = new Dictionary<int, string>(); // remote id -> car hash (client: from host's K)

void NetSend(string msg, IPEndPoint to) { net!.Send(Encoding.ASCII.GetBytes(msg), to); tx++; }

string? lastOwn = null; // last own state; null once "away" went out

void SendOwn(string state)
{
	if (mode == "fake") trail.Enqueue((Now(), state));
	else if (echo || !Due(state)) { }
	else if (mode == "host") foreach (var ep in peers.Keys) NetSend($"S 0 {name} {state}", ep);
	else NetSend($"S {name} {state}", hostEp!);
	if (!state.EndsWith(" 0")) lastOwn = null;
}

// throttle to SendHz, but never drop a state with flags (away)
bool Due(string state)
{
	double now = Now();
	if (now - lastSent < 1.0 / cfg.SendHz && state.EndsWith(" 0")) return false;
	lastSent = now;
	return true;
}

if (mode == "host")
{
	net = Proto.Udp(new IPEndPoint(IPAddress.Any, cfg.Port));
	Console.WriteLine($"Hosting on UDP {cfg.Port} as {name}. Buddies join your public/VPN IP (forward UDP {cfg.Port}).");
}
else if (mode == "join")
{
	hostEp = Proto.Resolve(target ?? "", cfg.Port);
	net = Proto.Udp(new IPEndPoint(IPAddress.Any, 0));
	Console.WriteLine($"Joining {hostEp} as {name}{(echo ? " (echo)" : "")}.");
}
else Console.WriteLine($"Fake peer: your trail replayed {cfg.FakeDelayMs} ms later, {cfg.FakeOffsetM} m to the side.");
Console.WriteLine($"Game bridge on 127.0.0.1:{bridgePort}. Ctrl+C quits.");

// own car state from the game
_ = Task.Run(async () =>
{
	while (true)
	{
		UdpReceiveResult r;
		try { r = await bridge.ReceiveAsync(); } catch (SocketException) { continue; }
		var state = Encoding.ASCII.GetString(r.Buffer);
		if (state.StartsWith("H ") || state.StartsWith("F "))
		{
			lock (gate) OnGameFile(state);
			continue;
		}
		if (!Proto.ValidState(state)) continue;
		lock (gate)
		{
			game = r.RemoteEndPoint;
			gameSeen = Now();
			lastOwn = state;
			SendOwn(state);
		}
	}
});

// other players
if (net != null) _ = Task.Run(async () =>
{
	while (true)
	{
		UdpReceiveResult r;
		try { r = await net.ReceiveAsync(); } catch (SocketException) { continue; }
		if (r.Buffer.Length > 1400) continue;
		var msg = Encoding.ASCII.GetString(r.Buffer);
		var f = msg.Split(' ', 3);
		lock (gate)
		{
			rx++;
			if (OnCarMessage(msg, r.RemoteEndPoint)) continue;
			if (mode == "host")
			{
				if (f.Length < 2 || (f[0] != "S" && f[0] != "P" && f[0] != "C")) continue;
				if (!peers.TryGetValue(r.RemoteEndPoint, out var p))
				{
					p = peers[r.RemoteEndPoint] = new Peer(nextId++);
					Console.WriteLine($"+ peer {p.Id} {r.RemoteEndPoint}");
				}
				p.Seen = Now();
				if (f[0] == "C")
				{
					var text = Proto.CleanText(msg[2..]);
					if (text.Length == 0) continue;
					Chat(p.Name, text);
					foreach (var ep in peers.Keys) if (!ep.Equals(r.RemoteEndPoint)) NetSend($"C {p.Id} {p.Name} {text}", ep);
					continue;
				}
				if (f.Length < 3) continue;
				if (f[0] == "P")
				{
					var pp = f[2].Split(' ');
					p.Name = Proto.CleanName(pp[0]);
					if (pp.ElementAtOrDefault(1) != Updater.Version && !p.Warned)
					{
						p.Warned = true;
						Console.WriteLine($"! {p.Name} runs SlrrMp {pp.ElementAtOrDefault(1) ?? "?"}, you run {Updater.Version}: update both");
					}
					NetSend($"Q {f[1]} {Updater.Version}", r.RemoteEndPoint);
					continue;
				}
				if (!Proto.ValidState(f[2])) continue;
				p.Name = Proto.CleanName(f[1]);
				var relay = $"S {p.Id} {p.Name} {f[2]}";
				ToGame(relay);
				foreach (var ep in peers.Keys) if (!ep.Equals(r.RemoteEndPoint)) NetSend(relay, ep);
			}
			else if (r.RemoteEndPoint.Equals(hostEp))
			{
				if (f[0] == "Q" && double.TryParse(f[1], out var sent))
				{
					ping = Now() - sent;
					var hv = f.ElementAtOrDefault(2) ?? "?";
					if (hv != Updater.Version && !warned) { warned = true; Console.WriteLine($"! host runs SlrrMp {hv}, you run {Updater.Version}: update both"); }
				}
				if (f[0] == "C" && msg.Split(' ', 4) is [_, _, var who, var said]) Chat(Proto.CleanName(who), Proto.CleanText(said));
				var s = msg.Split(' ', 4);
				if (f[0] != "S" || s.Length < 4 || !int.TryParse(s[1], out _) || !Proto.ValidState(s[3])) continue;
				ToGame($"S {s[1]} {Proto.CleanName(s[2])} {s[3]}");
				if (echo && s[1] == "0" && Due(s[3])) NetSend($"S {name} {Proto.Offset(s[3], cfg.FakeOffsetM)}", hostEp!);
			}
		}
	}
});

void Chat(string who, string text)
{
	Console.WriteLine($"{who}: {text}");
	ToGame($"C {who}: {text}");
}

// chat: lines typed here go to everyone's game
_ = Task.Run(() =>
{
	while (Console.ReadLine() is { } line)
	{
		var text = Proto.CleanText(line);
		if (text.Length == 0) continue;
		lock (gate)
		{
			if (mode == "host") foreach (var ep in peers.Keys) NetSend($"C 0 {name} {text}", ep);
			else if (mode == "join") NetSend($"C {text}", hostEp!);
			else ToGame($"C {name}: {text}"); // fake: see it yourself
		}
	}
});

// game told us its folder ("H dir") or where it saved our car ("F rel")
void OnGameFile(string m)
{
	if (m[0] == 'H') { gameDir = m[2..]; return; }
	if (gameDir == null) return;
	try
	{
		var bytes = CarStore.Bundle(Path.Combine(gameDir, m[2..]));
		if (CarStore.Valid(bytes)) ownHash = cars.Add(bytes);
	}
	catch (IOException) { }
}

// K/G/D car file messages; true if handled
bool OnCarMessage(string msg, IPEndPoint from)
{
	var f = msg.Split(' ');
	switch (f[0])
	{
		case "K" when mode == "host" && f.Length == 3 && peers.TryGetValue(from, out var p) && CarStore.IsHash(f[1]) && int.TryParse(f[2], out var size):
			p.Car = f[1];
			cars.Want(f[1], size, from);
			return true;
		case "K" when mode == "join" && f.Length == 4 && int.TryParse(f[1], out var id) && CarStore.IsHash(f[2]) && int.TryParse(f[3], out var size2):
			carOf[id] = f[2];
			cars.Want(f[2], size2, hostEp!);
			if (echo && id == 0) ownHash = f[2]; // echo car = host's car
			return true;
		case "G" when f.Length == 3 && int.TryParse(f[2], out var idx):
			if (cars.Chunk(f[1], idx) is { } d) NetSend(d, from);
			return true;
		case "D" when f.Length == 5 && int.TryParse(f[2], out var di) && int.TryParse(f[3], out var total):
			try { cars.Got(f[1], di, total, Convert.FromBase64String(f[4])); } catch (FormatException) { }
			return true;
	}
	return false;
}

// tell the bridge which file each remote id drives
void CarsToGame()
{
	if (gameDir == null) return;
	var known = mode == "host" ? peers.Values.Where(p => p.Car != null).Select(p => (p.Id, p.Car!)) : carOf.Select(kv => (kv.Key, kv.Value));
	if (mode == "fake" && ownHash != null) known = [(99, ownHash)];
	foreach (var (id, hash) in known)
		if (cars.Write(hash, gameDir) is { } rel) ToGame($"F {id} {rel}");
}

// pull missing car chunks
if (net != null) _ = Task.Run(async () =>
{
	while (true)
	{
		lock (gate)
			foreach (var (msg, to) in cars.Requests()) NetSend(msg, to);
		await Task.Delay(100);
	}
});

// fake peer replay
if (mode == "fake") _ = Task.Run(async () =>
{
	while (true)
	{
		lock (gate)
			while (trail.Count > 0 && Now() - trail.Peek().at >= cfg.FakeDelayMs / 1000.0)
				ToGame($"S 99 fake {Proto.Offset(trail.Dequeue().state, cfg.FakeOffsetM)}");
		await Task.Delay(5);
	}
});

// once a second: ping, prune, status
while (true)
{
	await Task.Delay(1000);
	lock (gate)
	{
		if (mode == "join") NetSend($"P {Now():0.000} {name} {Updater.Version}", hostEp!);
		if (ownHash != null && cars.Size(ownHash) is int own)
		{
			if (mode == "join") NetSend($"K {ownHash} {own}", hostEp!);
			if (mode == "host")
				foreach (var ep in peers.Keys)
				{
					NetSend($"K 0 {ownHash} {own}", ep);
					foreach (var p in peers.Values) if (p.Car != null && cars.Size(p.Car) is int ps) NetSend($"K {p.Id} {p.Car} {ps}", ep);
				}
		}
		CarsToGame();
		// game left the City, quit or crashed without saying so: tell the others
		if (lastOwn != null && Now() - gameSeen > 1) SendOwn(lastOwn[..lastOwn.LastIndexOf(' ')] + " 1");
		foreach (var ep in peers.Where(kv => Now() - kv.Value.Seen > cfg.PeerTimeoutMs / 1000.0).Select(kv => kv.Key).ToList())
		{
			Console.WriteLine($"- peer {peers[ep].Id} {peers[ep].Name} timed out");
			peers.Remove(ep);
		}
		var who = mode == "host" ? $"peers={peers.Count} [{string.Join(",", peers.Values.Select(p => p.Name))}]"
			: mode == "join" ? $"ping={(ping < 0 ? "-" : $"{ping * 1000:0}ms")}" : "fake";
		Console.Title = $"SlrrMp {mode} {who} game={(Now() - gameSeen < 2 ? "in city" : "-")} rx={rx}/s tx={tx}/s";
		rx = tx = 0;
	}
}

class Peer(int id)
{
	public int Id = id;
	public string Name = "?";
	public double Seen;
	public string? Car;
	public bool Warned;
}

static class Updater
{
	public const string DefaultRepo = ""; // "owner/repo" once the public GitHub repo exists
	public static readonly string Version = typeof(Updater).Assembly.GetName().Version!.ToString(3);

	// offers the latest GitHub release (asset SLRR-MP.zip), installs it over this folder, re-runs install.ps1, restarts
	public static async Task Run(string? repoSetting, string[] args)
	{
		var repo = string.IsNullOrWhiteSpace(repoSetting) ? DefaultRepo : repoSetting;
		var dir = AppContext.BaseDirectory;
		var self = Environment.ProcessPath!;
		try { File.Delete(self + ".old"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		if (repo == "" || !File.Exists(Path.Combine(dir, "install.ps1"))) return; // no repo yet, or a dev build
		try
		{
			using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("SlrrMp/" + Version);
			using var check = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			var api = repo.StartsWith("http") ? repo : $"https://api.github.com/repos/{repo}/releases/latest"; // URL = self-hosted latest.json
			using var doc = JsonDocument.Parse(await http.GetStringAsync(api, check.Token));
			var tag = doc.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v');
			if (!System.Version.TryParse(tag, out var latest) || latest <= System.Version.Parse(Version)) return;
			var url = doc.RootElement.GetProperty("assets").EnumerateArray()
				.Where(a => a.GetProperty("name").GetString() == "SLRR-MP.zip")
				.Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault();
			if (url == null) return;

			Console.Write($"SlrrMp {tag} is available (you have {Version}). Update now? [Y/n] ");
			if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "n" or "no") return;
			while (Process.GetProcessesByName("StreetLegal_Redline").Length > 0)
			{
				Console.Write("Close the game first, then press Enter. ");
				Console.ReadLine();
			}

			Console.WriteLine("Downloading...");
			var tmp = Path.Combine(Path.GetTempPath(), "slrr-mp-update");
			if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
			Directory.CreateDirectory(tmp);
			var zip = Path.Combine(tmp, "SLRR-MP.zip");
			await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(url));
			ZipFile.ExtractToDirectory(zip, tmp);
			var src = Path.Combine(tmp, "SLRR-MP");

			// a running exe can be renamed but not overwritten
			File.Move(self, self + ".old");
			try
			{
				foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
				{
					var to = Path.Combine(dir, Path.GetRelativePath(src, f));
					Directory.CreateDirectory(Path.GetDirectoryName(to)!);
					File.Copy(f, to, true);
				}
			}
			catch
			{
				if (!File.Exists(self)) File.Move(self + ".old", self);
				throw;
			}

			var install = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(dir, "install.ps1")}\"") { UseShellExecute = false })!;
			install.WaitForExit();
			Console.WriteLine(install.ExitCode == 0 ? $"Updated to {tag}, restarting." : "Game files step failed: run install.bat. Restarting.");
			var again = new ProcessStartInfo(self) { UseShellExecute = false };
			foreach (var a in args) again.ArgumentList.Add(a);
			Process.Start(again);
			Environment.Exit(0);
		}
		catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException
			or KeyNotFoundException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
		{
			Console.WriteLine($"Update check failed ({e.Message}), continuing.");
		}
	}
}

// car files by content hash: complete ones can be served, wanted ones are pulled chunk by chunk
class CarStore
{
	const int ChunkSize = 768, MaxSize = 4 << 20, Burst = 32, MaxPart = 64;
	readonly Dictionary<string, byte[]> done = [];
	readonly Dictionary<string, (byte[]?[] parts, IPEndPoint from)> wanted = [];

	public static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..16].ToLowerInvariant();
	public static bool IsHash(string h) => h.Length == 16 && h.All(char.IsAsciiHexDigitLower);

	// bundle = entries of [int index][int length][bytes]; index 0 = main file, n = "<file>.n"
	public static byte[] Bundle(string main)
	{
		var ms = new MemoryStream();
		var w = new BinaryWriter(ms);
		for (int i = 0; i <= MaxPart; i++)
		{
			var path = i == 0 ? main : $"{main}.{i}";
			if (!File.Exists(path)) continue;
			var b = File.ReadAllBytes(path);
			w.Write(i);
			w.Write(b.Length);
			w.Write(b);
		}
		return ms.ToArray();
	}

	public static List<(int index, byte[] data)>? Unbundle(byte[] b)
	{
		var parts = new List<(int, byte[])>();
		for (int o = 0; o < b.Length;)
		{
			if (o + 8 > b.Length) return null;
			int i = BitConverter.ToInt32(b, o), n = BitConverter.ToInt32(b, o + 4);
			if (i is < 0 or > MaxPart || n < 0 || o + 8 + n > b.Length || parts.Any(p => p.Item1 == i)) return null;
			parts.Add((i, b[(o + 8)..(o + 8 + n)]));
			o += 8 + n;
		}
		return parts;
	}

	// main file is a Vehicle.save file: "SDAT" ... 0x88664422 version 5
	public static bool Valid(byte[] bundle)
	{
		if (bundle.Length > MaxSize || Unbundle(bundle) is not { } parts) return false;
		var b = parts.FirstOrDefault(p => p.index == 0).data;
		return b is { Length: >= 16 } && b[0] == 'S' && b[1] == 'D' && b[2] == 'A' && b[3] == 'T'
			&& BitConverter.ToUInt32(b, 8) == 0x88664422 && BitConverter.ToInt32(b, 12) == 5;
	}

	public string Add(byte[] b)
	{
		var h = Hash(b);
		done[h] = b;
		return h;
	}

	public int? Size(string h) => done.TryGetValue(h, out var b) ? b.Length : null;

	public void Want(string h, int size, IPEndPoint from)
	{
		if (done.ContainsKey(h) || wanted.ContainsKey(h) || size is <= 0 or > MaxSize) return;
		wanted[h] = (new byte[]?[(size + ChunkSize - 1) / ChunkSize], from);
	}

	public string? Chunk(string h, int i)
	{
		if (!done.TryGetValue(h, out var b) || i < 0 || i * ChunkSize >= b.Length) return null;
		int total = (b.Length + ChunkSize - 1) / ChunkSize;
		return $"D {h} {i} {total} {Convert.ToBase64String(b, i * ChunkSize, Math.Min(ChunkSize, b.Length - i * ChunkSize))}";
	}

	public void Got(string h, int i, int total, byte[] data)
	{
		if (!wanted.TryGetValue(h, out var w) || total != w.parts.Length || i < 0 || i >= total || data.Length > ChunkSize) return;
		w.parts[i] = data;
		if (w.parts.Any(x => x == null)) return;
		wanted.Remove(h);
		var b = w.parts.SelectMany(x => x!).ToArray();
		if (Hash(b) == h && Valid(b)) done[h] = b; // a bad file is dropped and re-wanted on the next K
	}

	public IEnumerable<(string msg, IPEndPoint to)> Requests()
	{
		foreach (var (h, w) in wanted)
			foreach (var i in Enumerable.Range(0, w.parts.Length).Where(i => w.parts[i] == null).Take(Burst))
				yield return ($"G {h} {i}", w.from);
	}

	// writes slrr-mp-cars/<hash>.car once; returns the game-relative path when the file is ready
	public string? Write(string h, string gameDir)
	{
		if (!done.TryGetValue(h, out var b)) return null;
		var path = Path.Combine(gameDir, "slrr-mp-cars", h + ".car");
		if (!File.Exists(path))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			// part files first: the main file appearing means the set is complete
			foreach (var (i, data) in Unbundle(b)!.OrderByDescending(p => p.index))
				File.WriteAllBytes(i == 0 ? path : $"{path}.{i}", data);
		}
		return $"slrr-mp-cars/{h}.car";
	}
}

record Config
{
	public int Port { get; init; } = 27650;
	public int BridgePort { get; init; } = 27651;
	public int SendHz { get; init; } = 30;
	public int PeerTimeoutMs { get; init; } = 5000;
	public int FakeDelayMs { get; init; } = 2000;
	public float FakeOffsetM { get; init; } = 5;
	public string? UpdateRepo { get; init; } // "owner/repo" or a releases/latest-style JSON URL; null = Updater.DefaultRepo

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	public static Config Load(string path)
	{
		if (File.Exists(path)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new();
		var c = new Config();
		File.WriteAllText(path, JsonSerializer.Serialize(c, Json));
		return c;
	}
}

static class Proto
{
	// state from the game script: "t type color x y z yaw pitch roll vx vy vz flags"
	public static bool ValidState(string s)
	{
		var f = s.Split(' ');
		return s.Length < 400 && f.Length == 13 && f.All(x => double.TryParse(x, out var d) && double.IsFinite(d));
	}

	public static string Offset(string state, float meters)
	{
		var f = state.Split(' ');
		f[3] = (double.Parse(f[3]) + meters).ToString("0.000");
		return string.Join(' ', f);
	}

	public static string CleanText(string s) => new string(s.Where(c => c is >= ' ' and <= '~').Take(100).ToArray()).Trim();

	public static string CleanName(string s)
	{
		var n = new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(16).ToArray());
		return n.Length > 0 ? n : "player";
	}

	public static IPEndPoint Resolve(string target, int defPort)
	{
		target = target.Trim();
		if (IPEndPoint.TryParse(target, out var ep)) return ep.Port == 0 ? new IPEndPoint(ep.Address, defPort) : ep;
		var parts = target.Split(':');
		var ip = Dns.GetHostAddresses(parts[0]).First(a => a.AddressFamily == AddressFamily.InterNetwork);
		return new IPEndPoint(ip, parts.Length > 1 ? int.Parse(parts[1]) : defPort);
	}

	public static UdpClient Udp(IPEndPoint bind)
	{
		var u = new UdpClient(bind);
		// stop ICMP "port unreachable" (peer/game not running) from surfacing as receive errors
		u.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);
		return u;
	}
}
