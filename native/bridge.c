// SLRR-MP bridge: dinput8.dll proxy that rebinds the stubbed native
// java.net.NetworkEngine.httpRequest to mp_native, relaying car states to SlrrMp over local UDP.
// Build: zig cc -target x86-windows-gnu -O2 -shared -s -o dinput8.dll bridge.c dinput8.def -lws2_32
// Test:  zig cc -target x86-windows-gnu -o test.exe test.c -lws2_32 && ./test.exe
#include <winsock2.h>
#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

typedef int (__cdecl *ReadArgsFn)(void *ctx, void *self, ...);
typedef void *(__attribute__((thiscall)) *MakeStrFn)(void *vm, const char *s);

static ReadArgsFn read_args;
static MakeStrFn make_str;
static FILE *logfile;
static char reply[4096];

// knobs, overridden from slrr-mp.ini [mp] next to the exe
static int cfg_bridge_port = 27651, cfg_delay_ms = 100, cfg_timeout_ms = 3000, cfg_tick_ms = 10;

static void logm(const char *fmt, ...)
{
	if (!logfile) return;
	va_list ap;
	va_start(ap, fmt);
	vfprintf(logfile, fmt, ap);
	va_end(ap);
	fputc('\n', logfile);
	fflush(logfile);
}

// ---- remote players: sample buffer + interpolation ----

#define MAXP 16
#define HIST 32
typedef struct { double t; float p[3], r[3], v[3]; } Sample;
typedef struct { int used, id, type, color, n, head; double off, last_rx; Sample s[HIST]; } Peer;
static Peer peers[MAXP];

static Sample *nth(Peer *q, int i) { return &q->s[(q->head + i) % HIST]; } // 0 = oldest

static void drop_peer(Peer *q) { memset(q, 0, sizeof *q); }

// "S id name t type color x y z yaw pitch roll vx vy vz flags"; now = local seconds
static void on_packet(const char *msg, double now)
{
	int id, type, color, flags;
	double t;
	Sample x;
	if (sscanf(msg, "S %d %*s %lf %d %d %f %f %f %f %f %f %f %f %f %d", &id, &t, &type, &color,
		&x.p[0], &x.p[1], &x.p[2], &x.r[0], &x.r[1], &x.r[2], &x.v[0], &x.v[1], &x.v[2], &flags) != 14) return;
	x.t = t;
	Peer *q = 0, *free_slot = 0;
	for (int i = 0; i < MAXP; i++) {
		if (peers[i].used && peers[i].id == id) q = &peers[i];
		else if (!peers[i].used && !free_slot) free_slot = &peers[i];
	}
	if (flags & 1) { if (q) drop_peer(q); return; } // away
	if (!q) {
		if (!free_slot) return;
		q = free_slot;
		q->used = 1;
		q->id = id;
		q->off = now - t;
	}
	if (q->n && t <= nth(q, q->n - 1)->t) return; // late/duplicate datagram
	q->type = type;
	q->color = color;
	q->last_rx = now;
	// clock offset = slow average of (arrival - send) time: absorbs the ~16 ms tick quantization of `now`
	// without moving the render clock in steps. A jump > 1 s (sender restarted) resets it.
	double o = now - t;
	q->off = fabs(o - q->off) > 1 ? o : q->off + (o - q->off) * 0.02;
	if (q->n < HIST) q->n++;
	else q->head = (q->head + 1) % HIST;
	*nth(q, q->n - 1) = x;
}

static float lerp_angle(float a, float b, float f)
{
	float d = fmodf(b - a + 3.14159265f, 6.2831853f);
	if (d < 0) d += 6.2831853f;
	return a + (d - 3.14159265f) * f;
}

// render state at now - delay in the sender's clock
static void interp(Peer *q, double now, float p[3], float r[3])
{
	double rt = now - q->off - cfg_delay_ms / 1000.0;
	Sample *a = nth(q, 0);
	int i = 0;
	while (i + 1 < q->n && nth(q, i + 1)->t <= rt) a = nth(q, ++i);
	if (rt <= a->t || i + 1 == q->n) {
		// before the first sample: hold; past the newest: extrapolate by velocity, max 250 ms
		float dt = rt > a->t ? (float)fmin(rt - a->t, 0.25) : 0;
		for (int k = 0; k < 3; k++) { p[k] = a->p[k] + a->v[k] * dt; r[k] = a->r[k]; }
		return;
	}
	Sample *b = nth(q, i + 1);
	float f = (float)((rt - a->t) / (b->t - a->t));
	for (int k = 0; k < 3; k++) { p[k] = a->p[k] + (b->p[k] - a->p[k]) * f; r[k] = lerp_angle(a->r[k], b->r[k], f); }
}

// "n id type color x y z yaw pitch roll ..." for the script
static void snapshot(char *out, size_t cap, double now)
{
	int n = 0;
	size_t len = 0;
	char body[4096] = "";
	for (int i = 0; i < MAXP; i++) {
		Peer *q = &peers[i];
		if (!q->used) continue;
		if ((now - q->last_rx) * 1000 > cfg_timeout_ms) { drop_peer(q); continue; }
		float p[3], r[3];
		interp(q, now, p, r);
		len += snprintf(body + len, sizeof body - len, " %d %d %d %.3f %.3f %.3f %.4f %.4f %.4f",
			q->id, q->type, q->color, p[0], p[1], p[2], r[0], r[1], r[2]);
		if (len >= sizeof body) break;
		n++;
	}
	snprintf(out, cap, "%d%s", n, body);
}

// ---- local UDP to SlrrMp ----

static SOCKET sock = INVALID_SOCKET;
static struct sockaddr_in app_addr;

static double now_s(void)
{
	static LARGE_INTEGER f;
	LARGE_INTEGER c;
	if (!f.QuadPart) QueryPerformanceFrequency(&f);
	QueryPerformanceCounter(&c);
	return (double)c.QuadPart / f.QuadPart;
}

static int net_init(void)
{
	if (sock != INVALID_SOCKET) return 1;
	WSADATA w;
	if (WSAStartup(MAKEWORD(2, 2), &w)) return 0;
	sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (sock == INVALID_SOCKET) { logm("socket() failed %d", WSAGetLastError()); return 0; }
	u_long nb = 1;
	ioctlsocket(sock, FIONBIO, &nb);
	app_addr.sin_family = AF_INET;
	app_addr.sin_port = htons((u_short)cfg_bridge_port);
	app_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	logm("udp ready -> 127.0.0.1:%d", cfg_bridge_port);
	return 1;
}

static void net_pump(const char *own_state)
{
	if (!net_init()) return;
	if (own_state && *own_state) sendto(sock, own_state, (int)strlen(own_state), 0, (struct sockaddr *)&app_addr, sizeof app_addr);
	char buf[1024];
	int n;
	double now = now_s();
	// errors (e.g. WSAECONNRESET while SlrrMp is not running) just end the drain
	while ((n = recv(sock, buf, sizeof buf - 1, 0)) > 0) {
		buf[n] = 0;
		on_packet(buf, now);
	}
}

// cdecl fn(ctx) like every other native; returns a Java String or 0 (null).
static void *__cdecl mp_native(void *ctx)
{
	const char *method = 0, *host = 0, *path = 0, *body = 0;
	read_args(ctx, 0, &method, &host, &path, &body);
	if (!method) return 0;
	if (!strcmp(method, "LOG")) {
		logm("script: %s", body ? body : "(null)");
		return 0;
	}
	if (!strcmp(method, "CFG")) {
		snprintf(reply, sizeof reply, "%d", cfg_tick_ms);
		return make_str(*(void **)ctx, reply);
	}
	if (!strcmp(method, "MP")) {
		net_pump(body);
		snapshot(reply, sizeof reply, now_s());
		return make_str(*(void **)ctx, reply);
	}
	return 0;
}

// pattern with -1 wildcards
static BYTE *scan(BYTE *p, BYTE *end, const short *pat, int n, int *count)
{
	BYTE *hit = 0;
	*count = 0;
	for (; p + n <= end; p++) {
		int i = 0;
		while (i < n && (pat[i] < 0 || p[i] == pat[i])) i++;
		if (i == n && !(*count)++) hit = p;
	}
	return hit;
}

static DWORD rd32(BYTE *p) { DWORD v; memcpy(&v, p, 4); return v; }
static BYTE *call_target(BYTE *e8) { return e8 + 5 + (int)rd32(e8 + 1); }

static void patch(void)
{
	BYTE *base = (BYTE *)GetModuleHandleA(0);
	IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
	IMAGE_SECTION_HEADER *s = IMAGE_FIRST_SECTION(nt);
	BYTE *text = base + s->VirtualAddress, *text_end = text + s->Misc.VirtualSize;
	int c;

	// Integer.toString/toHexString(I): arg reader + String factory calls at fixed offsets
	static const short its[] = {0x55,0x8b,0xec,0x81,0xec,0x04,0x04,0x00,0x00,0x56,0x8b,0x75,0x08,0x8d,0x45,0xfc,
		0x50,0x6a,0x00,0x56,0xe8,-1,-1,-1,-1,0xff,0x75,0xfc,0x8d,0x85,0xfc,0xfb,0xff,0xff,0x68,-1,-1,-1,-1,
		0x50,0xe8,-1,-1,-1,-1,0x8b,0x0e,0x8d,0x85,0xfc,0xfb,0xff,0xff,0x83,0xc4,0x18,0x50,0xe8};
	BYTE *f = scan(text, text_end, its, sizeof its / sizeof *its, &c);
	if (!c) { logm("FAIL Integer.toString pattern not found"); return; }
	read_args = (ReadArgsFn)call_target(f + 0x14);
	make_str = (MakeStrFn)call_target(f + 0x39);

	static const short name[] = {0,'h','t','t','p','R','e','q','u','e','s','t',0};
	BYTE *str = 0;
	int total = 0;
	for (int i = 0; i < nt->FileHeader.NumberOfSections; i++) {
		BYTE *a = base + s[i].VirtualAddress, *hit = scan(a, a + s[i].Misc.VirtualSize, name, sizeof name / sizeof *name, &c);
		if (c) str = hit;
		total += c;
	}
	c = total;
	if (c != 1) { logm("FAIL \"httpRequest\" string hits=%d", c); return; }
	DWORD sa = (DWORD)(str + 1);

	// push fn; push sig; push "httpRequest"; push cls; call register
	short reg[] = {0x68,-1,-1,-1,-1,0x68,-1,-1,-1,-1,0x68,sa&0xff,(sa>>8)&0xff,(sa>>16)&0xff,sa>>24,0x68,-1,-1,-1,-1,0xe8};
	BYTE *site = scan(text, text_end, reg, sizeof reg / sizeof *reg, &c);
	if (c != 1) { logm("FAIL registration site hits=%d", c); return; }
	BYTE *stub = (BYTE *)rd32(site + 1);
	if (stub[0] != 0x33 || stub[1] != 0xc0 || stub[2] != 0xc3) { logm("FAIL fn at %p is not the return-0 stub", stub); return; }

	DWORD old, fn = (DWORD)mp_native;
	VirtualProtect(site + 1, 4, PAGE_EXECUTE_READWRITE, &old);
	memcpy(site + 1, &fn, 4);
	VirtualProtect(site + 1, 4, old, &old);
	FlushInstructionCache(GetCurrentProcess(), site + 1, 4);
	logm("OK base=%p readArgs=%p makeStr=%p site=%p stub=%p -> %p", base, read_args, make_str, site, stub, mp_native);
}

typedef HRESULT (WINAPI *DI8Create)(HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

__declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE h, DWORD v, REFIID r, LPVOID *o, LPUNKNOWN u)
{
	static DI8Create real;
	if (!real) {
		char p[MAX_PATH];
		GetSystemDirectoryA(p, MAX_PATH);
		strcat(p, "\\dinput8.dll");
		HMODULE m = LoadLibraryA(p);
		real = m ? (DI8Create)GetProcAddress(m, "DirectInput8Create") : 0;
		logm("real dinput8 %s", real ? "loaded" : "MISSING");
		if (!real) return E_FAIL;
	}
	return real(h, v, r, o, u);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
	if (reason == DLL_PROCESS_ATTACH) {
		DisableThreadLibraryCalls(h);
		char p[MAX_PATH];
		GetModuleFileNameA(0, p, MAX_PATH);
		char *file = strrchr(p, '\\') + 1;
		strcpy(file, "slrr-mp.log");
		logfile = fopen(p, "w");
		strcpy(file, "slrr-mp.ini");
		cfg_bridge_port = GetPrivateProfileIntA("mp", "bridge_port", cfg_bridge_port, p);
		cfg_delay_ms = GetPrivateProfileIntA("mp", "interp_delay_ms", cfg_delay_ms, p);
		cfg_timeout_ms = GetPrivateProfileIntA("mp", "timeout_ms", cfg_timeout_ms, p);
		cfg_tick_ms = GetPrivateProfileIntA("mp", "tick_ms", cfg_tick_ms, p);
		logm("slrr-mp bridge loaded, port=%d delay=%d timeout=%d tick=%d", cfg_bridge_port, cfg_delay_ms, cfg_timeout_ms, cfg_tick_ms);
		patch();
	}
	return TRUE;
}
