// SlrrMp: relays SLRR city car states between the game's dinput8.dll bridge and other players.
//   SlrrMp host [--name N]              host on UDP Config.Port
//   SlrrMp join <ip[:port]> [--name N]  join a host
//   SlrrMp fake                         offline: your own trail replayed as a second car
//   --echo (join)                       send the host's car back as yours, offset: tests the full network path
//   --bridge <port>                     local game bridge port (run a second instance without a game)
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
var trail = new Queue<(double at, string state)>();

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
		if (r.Buffer.Length > 512) continue;
		var msg = Encoding.ASCII.GetString(r.Buffer);
		var f = msg.Split(' ', 3);
		lock (gate)
		{
			rx++;
			if (mode == "host")
			{
				if (f.Length < 3 || (f[0] != "S" && f[0] != "P")) continue;
				if (!peers.TryGetValue(r.RemoteEndPoint, out var p))
				{
					p = peers[r.RemoteEndPoint] = new Peer(nextId++);
					Console.WriteLine($"\n+ peer {p.Id} {r.RemoteEndPoint}");
				}
				p.Seen = Now();
				if (f[0] == "P") { p.Name = Proto.CleanName(f[2]); NetSend($"Q {f[1]}", r.RemoteEndPoint); continue; }
				if (!Proto.ValidState(f[2])) continue;
				p.Name = Proto.CleanName(f[1]);
				var relay = $"S {p.Id} {p.Name} {f[2]}";
				ToGame(relay);
				foreach (var ep in peers.Keys) if (!ep.Equals(r.RemoteEndPoint)) NetSend(relay, ep);
			}
			else if (r.RemoteEndPoint.Equals(hostEp))
			{
				if (f[0] == "Q" && double.TryParse(f[1], out var sent)) ping = Now() - sent;
				var s = msg.Split(' ', 4);
				if (f[0] != "S" || s.Length < 4 || !int.TryParse(s[1], out _) || !Proto.ValidState(s[3])) continue;
				ToGame($"S {s[1]} {Proto.CleanName(s[2])} {s[3]}");
				if (echo && s[1] == "0" && Due(s[3])) NetSend($"S {name} {Proto.Offset(s[3], cfg.FakeOffsetM)}", hostEp!);
			}
		}
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
		if (mode == "join") NetSend($"P {Now():0.000} {name}", hostEp!);
		// game left the City, quit or crashed without saying so: tell the others
		if (lastOwn != null && Now() - gameSeen > 1) SendOwn(lastOwn[..lastOwn.LastIndexOf(' ')] + " 1");
		foreach (var ep in peers.Where(kv => Now() - kv.Value.Seen > cfg.PeerTimeoutMs / 1000.0).Select(kv => kv.Key).ToList())
		{
			Console.WriteLine($"\n- peer {peers[ep].Id} {peers[ep].Name} timed out");
			peers.Remove(ep);
		}
		var who = mode == "host" ? $"peers={peers.Count} [{string.Join(",", peers.Values.Select(p => p.Name))}]"
			: mode == "join" ? $"ping={(ping < 0 ? "-" : $"{ping * 1000:0}ms")}" : "fake";
		Console.Write($"\r{who} game={(Now() - gameSeen < 2 ? "in city" : "-")} rx={rx}/s tx={tx}/s        ");
		rx = tx = 0;
	}
}

class Peer(int id)
{
	public int Id = id;
	public string Name = "?";
	public double Seen;
}

record Config
{
	public int Port { get; init; } = 27650;
	public int BridgePort { get; init; } = 27651;
	public int SendHz { get; init; } = 30;
	public int PeerTimeoutMs { get; init; } = 5000;
	public int FakeDelayMs { get; init; } = 2000;
	public float FakeOffsetM { get; init; } = 5;

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
