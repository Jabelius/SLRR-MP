// The SlrrMp window: name, Host / Join / Fake, a log with chat, a chat box and a status line.
using System.Net.Sockets;

static class Gui
{
	// WinForms needs an STA thread; the top-level program is async (MTA)
	public static void Run(Config cfg, string cfgPath)
	{
		var t = new Thread(() =>
		{
			Application.EnableVisualStyles();
			Application.SetHighDpiMode(HighDpiMode.SystemAware);
			Application.Run(new MainForm(cfg, cfgPath));
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
	}
}

class MainForm : Form
{
	Config cfg;
	readonly string cfgPath;
	readonly TextBox name = new() { Width = 130 };
	readonly TextBox host = new() { Width = 150, PlaceholderText = "host IP" };
	readonly Button hostBtn = new() { Text = "Host", AutoSize = true };
	readonly Button joinBtn = new() { Text = "Join", AutoSize = true };
	readonly Button fakeBtn = new() { Text = "Solo test", AutoSize = true };
	readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
	readonly TextBox say = new() { Dock = DockStyle.Bottom, PlaceholderText = "Chat: type and press Enter", Enabled = false };
	readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Text = "Not connected" };
	readonly Label version = new() { AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
	readonly Label game = new() { AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
	readonly LinkLabel checkUpdates = new() { Text = "Check for updates", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

	public MainForm(Config cfg, string cfgPath)
	{
		this.cfg = cfg;
		this.cfgPath = cfgPath;
		Text = $"SLRR-MP {Updater.Version}";
		Font = new Font("Segoe UI", 9f);
		name.Text = cfg.LastName ?? Proto.CleanName(Environment.UserName);
		host.Text = cfg.LastHost ?? "";

		var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
		Label L(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(6, 7, 2, 0) };
		top.Controls.AddRange([L("Name"), name, hostBtn, L("or"), host, joinBtn, L("or"), fakeBtn]);
		version.Text = $"SLRR-MP {Updater.Version}  for {GameInfo.Supported}";
		game.Text = GameInfo.Check(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\'))); // package sits in the game folder
		var info = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 2, 6, 4), WrapContents = false };
		info.Controls.AddRange([version, game, checkUpdates]);
		// dock order: the fill control first, edges after
		Controls.AddRange([log, say, status, info, top]);
		checkUpdates.LinkClicked += async (_, _) =>
		{
			checkUpdates.Enabled = false;
			var result = await Task.Run(() => Updater.Run(cfg.UpdateRepo, []));
			checkUpdates.Enabled = true;
			MessageBox.Show(this, result, "SLRR-MP updates");
		};

		hostBtn.Click += (_, _) => Start("host");
		joinBtn.Click += (_, _) => Start("join");
		fakeBtn.Click += (_, _) => Start("fake");
		host.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Start("join"); };
		say.KeyDown += (_, e) =>
		{
			if (e.KeyCode != Keys.Enter) return;
			e.SuppressKeyPress = true;
			var text = say.Text;
			say.Clear();
			Task.Run(() => Relay.Say(text));
		};

		Load += async (_, _) =>
		{
			// fit the measured top row at any display scaling (fixed pixel sizes don't scale with the font)
			float k = DeviceDpi / 96f;
			int w = Math.Max(Math.Max(top.GetPreferredSize(Size.Empty).Width, info.GetPreferredSize(Size.Empty).Width) + (int)(16 * k), (int)(760 * k));
			ClientSize = new Size(w, (int)(480 * k));
			MinimumSize = new Size(Width, (int)(300 * k));
			CenterToScreen();

			Ui.Log = s => BeginInvoke(() => log.AppendText(s + Environment.NewLine));
			Ui.Status = s => BeginInvoke(() => status.Text = s);
			Ui.Ask = q => (bool)Invoke(() => MessageBox.Show(this, q, "SLRR-MP", MessageBoxButtons.YesNo) == DialogResult.Yes);
			Ui.GameFound = dir => { var g = GameInfo.Check(dir); BeginInvoke(() => game.Text = g); };
			Ui.Log("Host: you share your IP (port-forward UDP " + cfg.Port + " or use a VPN like Tailscale). Join: type the host's IP.");
			Ui.Log("Solo test: a copy of your car follows you, to check the install.");
			SetIdle(false);
			Ui.Log(await Task.Run(() => Updater.Run(cfg.UpdateRepo, [])));
			SetIdle(true);
		};
	}

	void SetIdle(bool idle)
	{
		foreach (var c in new Control[] { name, host, hostBtn, joinBtn, fakeBtn }) c.Enabled = idle;
	}

	void Start(string mode)
	{
		if (!hostBtn.Enabled) return;
		var n = Proto.CleanName(name.Text);
		var target = host.Text.Trim();
		if (mode == "join" && target == "")
		{
			MessageBox.Show(this, "Type the host's IP address first.", "SLRR-MP");
			return;
		}
		name.Text = n;
		cfg = cfg with { LastName = n, LastHost = target };
		try { cfg.Save(cfgPath); } catch (IOException) { }
		SetIdle(false);
		say.Enabled = true;
		Text = $"SLRR-MP {Updater.Version} - {(mode == "fake" ? "solo test" : mode)}";
		Task.Run(async () =>
		{
			try { await Relay.Run(cfg, mode, n, target, false, cfg.BridgePort); }
			catch (Exception e) when (e is SocketException or FormatException or InvalidOperationException or ArgumentException)
			{
				Ui.Log("Could not start: " + e.Message);
				BeginInvoke(() => { SetIdle(true); say.Enabled = false; });
			}
		});
	}
}
