# NetWatch

[![build](https://github.com/nks-hub/netwatch/actions/workflows/build.yml/badge.svg)](https://github.com/nks-hub/netwatch/actions/workflows/build.yml)
[![GitHub Release](https://img.shields.io/github/v/release/nks-hub/netwatch)](https://github.com/nks-hub/netwatch/releases)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4.svg)](#requirements)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> A portable, no-install, no-admin Windows console tool that pinpoints **where** a "slow VPN" or "slow network" complaint actually comes from — the local NIC (including Wi-Fi signal), the router/gateway, the internet, or the VPN tunnel — and proves it with timestamped logs and daily CSV aggregation.

NetWatch is a single self-contained `netwatch.exe` built on **.NET Framework 4.8**, which ships in-box on every stock Windows 10 and Windows 11 machine. Copy it onto an affected user's PC, run it as a normal user, watch the live dashboard, and the logs tell you which layer is genuinely at fault.

```
╭─ LINK ───────────────────────────────────────────────────────────╮
│ Wi-Fi WLAN   ip 192.0.2.34   link 866.7 Mbps                      │
│ Wi-Fi  OfficeNet  [████░] 81% (-59 dBm)  rx/tx 866.7/780          │
│ ↓ recv 4.21   ↑ send 0.38 Mbps                                    │
│ VPN  wireguard  CONNECTED  ip 198.51.100.7  gw 198.51.100.1       │
╰─ Wi-Fi · up ─────────────────────────────────────────────────────╯
```

## Features

- 📡 **Native ICMP ping, no admin** - Uses `System.Net.NetworkInformation.Ping` (the OS ICMP helper, not raw sockets) to continuously probe each target. RTT is taken from the OS-observed round-trip, not wall-clock, so latency is never inflated. Per-target last/avg/max/jitter/p95, loss %, session max ping and drop count are tracked separately so you can see exactly which hop is slow.
- 🔐 **Vendor-agnostic VPN auto-detection** - Finds a VPN adapter generically — WireGuard, OpenVPN, Fortinet/FortiClient, Cisco AnyConnect, GlobalProtect, Zscaler, Pulse Secure, SonicWall, Check Point, Sophos, NordLynx, Proton VPN, Mullvad, Tailscale, ZeroTier, and WAN Miniport / TAP — without hardcoding any endpoint. When the tunnel gateway is not exposed by the adapter (e.g. WireGuard-style /32 peers), it is recovered from the IPv4 route table; for split-tunnel /24 peers with no off-link route it derives the conventional tunnel-server host from the live subnet. A VPN that connects *after* launch is picked up and monitored automatically.
- 🧭 **Traceroute with reverse DNS and route-change detection** - Periodic TTL-limited traceroute to a configurable target, resolving each hop's hostname and flagging when the path changes between runs.
- ⚡ **A speed test that doesn't lie** - Measures download, upload, unloaded latency and jitter against Cloudflare's public speed endpoints over ordinary outbound HTTPS (port 443, honouring the system/corporate proxy). The ping and traceroute loops are **paused while the test saturates the link** (plus a short drain margin), so the test's own bufferbloat is never recorded as packet loss or elevated RTT. On failure it reports a concrete reason (e.g. `HTTP 429`, `network: Timeout`) and falls back first to a single fresh stream, then to the nearest **Ookla Speedtest** server as a second source — and labels which source produced each number.
- 🖥️ **Live "all-cards" dashboard** - A console UI that is resize-robust and responsive, redrawing in place. See [The live dashboard](#the-live-dashboard).
- 📝 **Full logging** - A human-readable session log, four daily CSVs, and periodic SUMMARY blocks for IT to review.

## Requirements

- Windows 10 or Windows 11 with the in-box **.NET Framework 4.8** (preinstalled on every Win10/11 machine — nothing to install).
- **No administrator rights.** Runs as a standard user.
- **No installation, no dependencies** — a single ~100 KB EXE. Copy it and run it.

## Installation

Download `netwatch.exe` (and the optional `netwatch.exe.config`) from [GitHub Releases](https://github.com/nks-hub/netwatch/releases) and copy it onto the affected PC. Nothing else is needed. To build it yourself, see [Development](#development).

## Usage

Double-click `netwatch.exe`, or run it from a console:

```powershell
netwatch.exe
```

This starts the live dashboard. Press **Ctrl+C** to stop; NetWatch writes a final summary and exits cleanly.

### Flags

| Flag | Effect |
|------|--------|
| *(none)* | Live dashboard with continuous monitoring |
| `-once` | Run one diagnostic pass (interfaces, 4× ping per target, one traceroute, one speed test), print a plain-text report, then exit. Ideal for attaching to a ticket. |
| `-frame` | Print one captured dashboard frame to stdout and exit (a pipeable / capturable status snapshot) |
| `-no-ui` | Continuous monitoring without the live dashboard (e.g. when redirecting output) |
| `-config <path>` | Use the given config file (default: `netwatch.conf` beside the EXE) |
| `-logdir <path>` | Override the log/CSV output directory |
| `-version` | Print the version and exit |
| `-h`, `-help` | Show usage and exit |

## Configuration

On first run, NetWatch writes a commented `netwatch.conf` next to the EXE. It is a simple, locale-independent `key = value` text file (comments start with `#`; lists are comma-separated). Edit values and restart.

**Nothing is hardcoded.** Targets and the VPN come from runtime auto-detection or this config. To measure a *specific* company VPN, IT sets `vpn_server` (the public endpoint, also traced) and `vpn_targets` (the in-tunnel hosts users actually need).

| Key | Meaning | Default |
|-----|---------|---------|
| `internet_targets` | Public hosts/IPs pinged continuously | `1.1.1.1, 8.8.8.8, google.com` |
| `monitor_gateway` | Auto-detect and ping the default gateway (router / LAN health) | `true` |
| `monitor_vpn` | Auto-detect a VPN adapter and ping its tunnel gateway | `true` |
| `vpn_server` | Public address of the company VPN endpoint (pinged and traced) — **set this to diagnose a VPN** | *(empty)* |
| `vpn_targets` | Internal hosts reachable only through the VPN (e.g. app servers) | *(empty)* |
| `ping_interval_ms`, `ping_timeout_ms` | Ping cadence and timeout | `1000`, `2000` |
| `trace_target`, `trace_interval_sec`, `trace_max_hops`, `trace_reverse_dns` | Traceroute behaviour | `8.8.8.8`, `300`, `30`, `true` |
| `speedtest_enabled`, `speedtest_on_start`, `speedtest_interval_min` | Speed-test schedule | `true`, `true`, `30` |
| `interface_interval_sec` | Adapter / Wi-Fi sampling cadence | `2` |
| `summary_interval_sec`, `console_refresh_ms` | Log SUMMARY and dashboard refresh cadence | `60`, `1000` |
| `windows_sec` | Rolling aggregation windows, in seconds | `60, 300, 900, 3600` |
| `wifi_weak_signal_pct`, `high_latency_ms`, `high_loss_pct` | Alert thresholds (logged as events when crossed) | `40`, `150`, `5` |
| `log_dir` | Where logs land (relative paths resolve next to the EXE) | `logs` |

Example — to diagnose a VPN complaint, set the endpoint and the affected internal hosts (use your own values):

```ini
vpn_server  = vpn.example.com
vpn_targets = app.example.com, 198.51.100.10
```

## What it measures

NetWatch runs every diagnostic layer concurrently and attributes latency to the precise hop.

| Layer | How |
|-------|-----|
| Local NIC / Wi-Fi | Active adapter, link speed, recv/send throughput, error/discard counters; for Wi-Fi: SSID, signal quality %, approximate RSSI (dBm), negotiated rx/tx rate, PHY type (802.11a/b/g/n/ac/ax) and channel |
| Router / Gateway | Continuous ICMP ping to the auto-detected default gateway |
| ISP / Internet | Continuous ping to configurable public anchors + a periodic speed test |
| DNS | Ping to the active adapter's first DNS server |
| VPN tunnel | Ping to the auto-detected tunnel gateway, the configured VPN public endpoint, and internal in-tunnel hosts |
| Path | Periodic traceroute with reverse DNS and route-change detection |

## The live dashboard

Running `netwatch.exe` with no arguments opens the live dashboard, a stack of titled cards:

- **Header card** — product line, author/repo, uptime, wall-clock, and the active log directory.
- **LINK card** — the active adapter with its IP and link speed; a Wi-Fi line with SSID, a signal bar, signal %, RSSI and rx/tx rate; a throughput line (recv/send Mbps plus in/out error counters); and the merged **VPN status** (vendor, CONNECTED/DISCONNECTED, IP and tunnel gateway).
- **TARGETS card** — the aligned ping table: `Target / Last / Avg / Max / Loss / Drops / Jit / P95` plus a per-target latency **sparkline**. `Max` and `Drops` are per-target session totals. Rows recolour by health (green / yellow / red), and the gateway and VPN rows are emphasised.
- **SPEEDTEST card** — download/upload **bar gauges** on a shared auto-scaled axis (with a tick at the session peak), unloaded latency and jitter, the measuring server/colo, session averages, test count, and a "next in" countdown.
- **TRACE / EVENTS card** — the last traceroute summary (target, hop count, time, path stable/CHANGED) and the most recent diagnostic events.

The dashboard is rendered with the Console API (cursor positioning + colours), so it is locale- and ANSI-independent and survives window resizes cleanly.

## Logs and output

By default logs land in a `logs\` folder next to the EXE (override with `log_dir` or `-logdir`). Files rotate by local date, and every record is flushed immediately so data survives an abrupt kill:

- `log_<date>.log` — human-readable timestamped events plus periodic **SUMMARY** blocks (active link, VPN, per-target 1m/longest-window loss/avg/p95, session max & drops, last speed test)
- `ping_<date>.csv` — `time,target,addr,kind,success,rtt_ms`
- `speed_<date>.csv` — `time,down_mbps,up_mbps,latency_ms,jitter_ms,server,error`
- `trace_<date>.csv` — `time,target,addr,ttl,hop_addr,hop_host,rtt_ms,reached,timeout`
- `iface_<date>.csv` — `time,name,kind,active,up,link_mbps,ip,gateway,recv_mbps,send_mbps,in_err,out_err,wifi,ssid,signal_pct,rssi_dbm,wifi_rx_mbps,wifi_tx_mbps`

Attach the relevant CSVs (or the whole folder) to a support ticket so latency can be located precisely.

## Development

Built against **net48** with the .NET SDK (any recent version, with the .NET Framework 4.8 targeting pack installed). No NuGet packages — System.* BCL only.

```powershell
dotnet build -c Release
```

Or run the staging script, which builds Release and copies the portable EXE into `dist\`:

```powershell
.\build.ps1
```

`build.ps1` prints the path and size of the staged `dist\netwatch.exe`, ready to copy to any Windows 10/11 machine. CI also publishes a prebuilt `netwatch.exe` as a build artifact, so you can grab a ready-to-run binary without building locally.

## Contributing

Contributions are welcome! For major changes, please open an issue first.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: description'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## Support

- 📧 **Email:** dev@nks-hub.cz
- 🐛 **Bug reports:** [GitHub Issues](https://github.com/nks-hub/netwatch/issues)

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">
  Made with ❤️ by <a href="https://github.com/nks-hub">NKS Hub</a>
</p>
