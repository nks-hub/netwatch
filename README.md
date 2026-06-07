# NetWatch

A portable, no-install, no-admin Windows tool that diagnoses employee network and VPN latency complaints by continuously measuring **where** the latency originates — the local NIC (including Wi-Fi signal), the router/gateway, the internet, and the VPN tunnel — with timestamped logs and CSV aggregation.

NetWatch is a single self-contained `netwatch.exe` built on **.NET Framework 4.8**, which ships in-box on every stock Windows 10 and Windows 11 machine. Copy it onto an affected user's PC, run it as a normal user, and the logs tell you which layer is actually slow.

## Highlights

- **Native ICMP ping, no admin.** Uses `System.Net.NetworkInformation.Ping` (the OS ICMP helper, not raw sockets) to continuously probe public internet anchors, the auto-detected default gateway, the auto-detected VPN tunnel, and the active adapter's DNS server. Per-target latency, jitter, packet loss, session max ping and drop count are tracked separately so you can see exactly which hop is at fault.
- **Vendor-agnostic VPN detection.** Finds a VPN adapter generically (WireGuard, OpenVPN, Fortinet, Cisco AnyConnect, GlobalProtect, Zscaler, WAN Miniport, TAP, and more) without hardcoding any endpoint. When the tunnel gateway is not exposed by the adapter (e.g. WireGuard /32 peers), it is recovered from the IPv4 route table. Targets that appear after launch (a VPN that connects later) are picked up and monitored automatically.
- **Traceroute with reverse DNS and route-change detection.** Periodic TTL-limited traceroute to a configurable target, resolving each hop's hostname and flagging when the path changes between runs.
- **Cloudflare speed test that doesn't lie.** Measures download, upload, unloaded latency and jitter against Cloudflare's public speed endpoints over ordinary outbound HTTPS (port 443, honours the system/corporate proxy). The ping and traceroute loops are **paused while the test saturates the link** (plus a short drain margin), so the test's own bufferbloat is never recorded as packet loss or elevated RTT.
- **Interface and Wi-Fi monitoring.** Per-adapter throughput (delta-based recv/send Mbps), link speed, and error/discard counters for Ethernet and Wi-Fi. Wi-Fi specifics — SSID, signal quality %, approximate RSSI (dBm), negotiated rx/tx rate, PHY type and channel — are read locale-independently via the Native Wifi API, working as a standard user.
- **Rolling aggregation windows.** Each target is summarized over **1m / 5m / 15m / 1h** windows (configurable) with loss %, p95, average, and jitter, alongside per-target **session Max ping** and **Drops** count shown in the live status.
- **Edge-triggered, debounced events.** Diagnostic events (VPN connect/disconnect, active-link change, weak Wi-Fi signal, high latency, high packet loss, target unreachable/recovered, route change) fire once on the transition, not on every sample. Reachability is debounced so a single dropped probe never flips a target to DOWN or raises a false alarm.
- **Live console dashboard** plus full logging: a human-readable session log, four daily CSVs, and periodic SUMMARY blocks for IT to review.

## What it measures

| Layer | How |
|-------|-----|
| Local NIC / Wi-Fi | Active adapter, link speed, recv/send throughput, error counters; SSID, signal %, RSSI, PHY rate, channel for Wi-Fi |
| Router / Gateway | Continuous ICMP ping to the auto-detected default gateway |
| ISP / Internet | Continuous ping to configurable public anchors + periodic Cloudflare speed test |
| DNS | Ping to the active adapter's first DNS server |
| VPN tunnel | Ping to the detected VPN tunnel gateway, the configured VPN public endpoint, and internal in-tunnel hosts |
| Path | Periodic traceroute with reverse DNS and change detection |

## Requirements

- Windows 10 or Windows 11 with the in-box **.NET Framework 4.8** (preinstalled on every Win10/11 machine — nothing to install).
- **No administrator rights.** Runs as a standard user.
- **No installation, no dependencies** — a single ~100 KB EXE. Copy it and run it.

## Build

Built against **net48** with the .NET SDK (any recent version, with the .NET Framework 4.8 targeting pack installed). No NuGet packages — System.* BCL only.

```powershell
dotnet build -c Release
```

Or run the staging script, which builds Release and copies the portable EXE into `dist\`:

```powershell
.\build.ps1
```

`build.ps1` prints the path and size of the staged `dist\netwatch.exe`, ready to copy to any Windows 10/11 machine.

## Usage

Double-click `netwatch.exe`, or run it from a console:

```powershell
netwatch.exe
```

This starts the live dashboard. Press **Ctrl+C** to stop; NetWatch writes a final summary and exits cleanly.

### Flags

| Flag | Effect |
|------|--------|
| (none) | Live dashboard with continuous monitoring |
| `-once` | Run one diagnostic pass (interfaces, 4× ping per target, one traceroute, one speed test), print a plain-text report, then exit. Ideal for attaching to a ticket. |
| `-frame` | Print one live dashboard frame to stdout and exit (capturable / pipeable status snapshot) |
| `-no-ui` | Continuous monitoring without the live dashboard (e.g. when redirecting output) |
| `-config <path>` | Use the given config file (default: `netwatch.conf` beside the EXE) |
| `-logdir <path>` | Override the log/CSV output directory |
| `-version` | Print the version and exit |
| `-h`, `-help` | Show usage and exit |

## Configuration

On first run, NetWatch writes a commented `netwatch.conf` next to the EXE. It is a simple, locale-independent `key = value` text file (comments start with `#`; lists are comma-separated). Edit values and restart.

**Nothing is hardcoded.** Targets and the VPN come from runtime auto-detection or this config. To measure a specific company VPN, IT sets `vpn_server` (the VPN endpoint) and `vpn_targets` (in-tunnel hosts).

| Key | Meaning |
|-----|---------|
| `internet_targets` | Public hosts/IPs pinged continuously (default `1.1.1.1, 8.8.8.8, google.com`) |
| `monitor_gateway` | Auto-detect and ping the default gateway (router / LAN health) |
| `monitor_vpn` | Auto-detect a VPN adapter and ping its tunnel gateway |
| `vpn_server` | Public address of the company VPN endpoint (pinged and traced) — set this to diagnose a VPN |
| `vpn_targets` | Internal hosts reachable only through the VPN (e.g. app servers) |
| `ping_interval_ms`, `ping_timeout_ms` | Ping cadence and timeout |
| `trace_target`, `trace_interval_sec`, `trace_max_hops`, `trace_reverse_dns` | Traceroute behaviour |
| `speedtest_enabled`, `speedtest_on_start`, `speedtest_interval_min` | Cloudflare speed test schedule |
| `interface_interval_sec` | Adapter / Wi-Fi sampling cadence |
| `summary_interval_sec`, `console_refresh_ms` | Log SUMMARY and dashboard refresh cadence |
| `windows_sec` | Rolling aggregation windows in seconds (default `60, 300, 900, 3600`) |
| `wifi_weak_signal_pct`, `high_latency_ms`, `high_loss_pct` | Alert thresholds (logged as events when crossed) |
| `log_dir` | Where logs land (relative paths are resolved next to the EXE) |

Example: to diagnose a VPN complaint, set the endpoint and the affected internal hosts (use your own values):

```ini
vpn_server  = vpn.example.com
vpn_targets = app.example.com, 198.51.100.10
```

## Logs and output

By default logs land in a `logs\` folder next to the EXE (override with `log_dir` or `-logdir`). Files rotate by local date:

- `log_<date>.log` — human-readable timestamped events plus periodic **SUMMARY** blocks for IT to review
- `ping_<date>.csv` — `time,target,addr,kind,success,rtt_ms`
- `speed_<date>.csv` — `time,down_mbps,up_mbps,latency_ms,jitter_ms,server,error`
- `trace_<date>.csv` — `time,target,addr,ttl,hop_addr,hop_host,rtt_ms,reached,timeout`
- `iface_<date>.csv` — adapter / Wi-Fi samples including signal, throughput and error counters

Attach the relevant CSVs (or the whole folder) to a support ticket so latency can be located precisely.

## Repository

- **GitHub:** https://github.com/nks-hub/netwatch
- **Author:** LuRy <lury@lury.cz>
- **License:** MIT
