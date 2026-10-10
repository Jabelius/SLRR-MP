// Offline check of bridge.c packet parsing + interpolation (no game needed).
#include "bridge.c"
#include <assert.h>

static void feed(int id, double t, float x, float yaw, int flags, double now)
{
	char m[256];
	snprintf(m, sizeof m, "S %d bob %.3f 2687270 1 %.3f 9.8 1000 %.4f 0 0 10 0 0 %d", id, t, x, yaw, flags);
	on_packet(m, now);
}

static float field(const char *snap, int idx)
{
	const char *p = snap;
	for (int i = 0; i < idx; i++) p = strchr(p, ' ') + 1;
	return (float)atof(p);
}

int main(void)
{
	char s[4096];

	// 30 Hz stream, x = 10 m/s * t, sender clock 1000 s ahead, 50-70 ms latency
	for (int k = 0; k < 60; k++) {
		double t = 1000 + k / 30.0;
		feed(7, t, (float)(10 * t), 0, 0, t - 1000 + 0.05 + (k % 3) * 0.01);
	}
	double now = 59 / 30.0 + 0.05;
	snapshot(s, sizeof s, now);
	assert(field(s, 0) == 1 && field(s, 1) == 7 && field(s, 2) == 2687270);
	// offset converges toward the 50-70 ms latency without stepping; render time = now - off - delay
	double off = peers[0].off;
	assert(off > -1000 + 0.05 - 1e-6 && off < -1000 + 0.07);
	float want = (float)(10 * (now - off - 0.1));
	assert(fabsf(field(s, 5) - want) < 0.05f);

	// car file mapping shows up in the snapshot, "-" until known
	assert(strstr(s, " 7 2687270 1 - "));
	on_packet("F 7 save/temp/mp/0123abcd.car", now);
	snapshot(s, sizeof s, now);
	assert(strstr(s, " 7 2687270 1 save/temp/mp/0123abcd.car "));

	// extrapolation capped at 250 ms past the newest sample
	snapshot(s, sizeof s, now + 2);
	assert(fabsf(field(s, 5) - (float)(10 * (1000 + 59 / 30.0 + 0.25))) < 0.05f);

	// garbage and late datagrams are ignored
	on_packet("S 7 bob nonsense", now);
	feed(7, 1000, 0, 0, 0, now);
	snapshot(s, sizeof s, now);
	assert(fabsf(field(s, 5) - want) < 0.05f);

	// yaw lerps across +-pi the short way
	feed(8, 0.0, 0, 3.1f, 0, 0.0);
	feed(8, 1.0, 0, -3.1f, 0, 1.0);
	cfg_delay_ms = 500;
	snapshot(s, sizeof s, 1.0);
	float yaw = field(s, 18); // peer 8 yaw (record 2 = fields 11-20)
	assert(fabsf(fabsf(yaw) - 3.1416f) < 0.01f);
	cfg_delay_ms = 100;

	// away flag removes, timeout removes
	feed(8, 2.0, 0, 0, 1, 2.0);
	snapshot(s, sizeof s, now);
	assert(field(s, 0) == 1);
	snapshot(s, sizeof s, now + 10);
	assert(!strcmp(s, "0"));

	// smoothness: 30 Hz sends, arrivals drained on a 60 Hz tick with 0-15 ms extra latency;
	// rendered x must advance evenly every tick (old min-tracker stepped here)
	double base = 500, prev = 0;
	int sent = 0;
	for (int tick = 0; tick < 600; tick++) {
		double tnow = base + tick / 60.0;
		while (sent / 30.0 + (sent * 7 % 16) / 1000.0 <= tick / 60.0) {
			double t = 2000 + sent / 30.0;
			feed(3, t, (float)(10 * sent / 30.0), 0, 0, tnow);
			sent++;
		}
		snapshot(s, sizeof s, tnow);
		double x = field(s, 5);
		if (tick > 300) assert(fabs((x - prev) - 10 / 60.0) < 0.02);
		prev = x;
	}

	puts("bridge tests ok");
	return 0;
}
