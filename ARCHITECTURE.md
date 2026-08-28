# Architecture

For concrete commands, config, and implementation gotchas, see
[IMPLEMENTATION_DETAILS.md](IMPLEMENTATION_DETAILS.md).

## Overview

`cloud-edge-sync` is a hands-on reference project for learning Kong API
Gateway by building a realistic cloud-to-edge topology: a cloud-hosted sync
API that needs to reach a backend service on a private local network, where
that network has no fixed public address. Kong sits at the edge as the
gateway, owning routing, authentication, and rate limiting for everything
that crosses into the local network.

## Scope & Simplifications

This project intentionally trades production-grade robustness for a small,
inspectable system, since the goal is to get hands-on with Kong, not to
build a distributed systems project:


- **Config-based edge Kong URL, not a registry.** The Cloud Sync API reads
  the current edge Kong URL — the **Instrument URL** — from its own
  configuration (`InstrumentConfiguration:InstrumentUri`, set as an Azure App
  Service Application Setting). There is no registration endpoint, no
  heartbeat, and no in-memory map: when the tunnel URL changes, the setting
  is updated manually.
- **Single edge site.** The design assumes one Edge Kong / Edge Sync
  Service pair. Multi-site is a natural extension (see Future Work) but
  adds registry and routing concerns that aren't at this point.

## System Diagram

<img width="750" height="409" alt="image" src="https://github.com/user-attachments/assets/f7f4a428-746d-4daf-b786-f85cc1e18338" />


## Components

### Cloud Sync API (Azure)
The cloud-side service that initiates syncs with the edge. Responsibilities:

- **Sync trigger logic** — on an instrument update, looks up the configured
  Instrument URL and issues a request through it to Edge Kong. This is a
  fire-and-forget pattern: the local write completes and returns immediately
  without waiting on the edge/tunnel round-trip, so a slow or unreachable
  edge never blocks the caller.
- **Failure handling** — currently just logging. If the fire-and-forget call
  fails (connection refused, tunnel expired), it's logged as a warning/error
  and nothing else happens — no retry, no stale-marking, no alerting.
- **On-demand connectivity check** — a separate `GET /instrument-connection`
  endpoint tests whether the currently configured Instrument URL is reachable
  (through the full tunnel → Kong → Edge Sync Service path) and reports
  `200`/`503`, without performing an actual sync. Manually triggered, not a
  background heartbeat.

### Instrument / Tunnel
The mechanism that gives Edge Kong a publicly reachable address despite
sitting on a NAT'd/private local network. Chosen: **Cloudflare Tunnel** — an
outbound-only connection from the local network to Cloudflare's edge, so no
static IP, inbound firewall rule, or VPN hardware is needed. The tunnel's
public URL is exactly the "Instrument URL" the Cloud Sync API stores.

**Why not the "real-world" enterprise option?** The equivalent pattern in a
company network is typically **Azure VPN Gateway (Site-to-Site)** — a
persistent IPsec tunnel between an on-prem router and an Azure VNet. That's
genuinely the right tool when the "site" is an office with business-grade
internet and a static (or at least stable) public IP, which is exactly the
environment it's designed for. It's the wrong tool here specifically because
a home network sits behind CGNAT with a dynamic IP — there's no stable
address for Azure's side of the tunnel to target — and separately, Azure VPN
Gateway is a continuously-billed resource (real cost even sitting idle),
whereas a home-lab/portfolio project has no ongoing budget to justify that.
Cloudflare Tunnel sidesteps the static-IP requirement entirely by making the
connection outbound-only, and costs nothing for this use case. See
[IMPLEMENTATION_DETAILS.md](IMPLEMENTATION_DETAILS.md) for the full
comparison (including Azure Relay Hybrid Connections, the closer Azure-native
match) and how the tunnel is actually set up.

### Edge Kong
The gateway on the local network, and the actual subject of this project.
Owns:

- A **Service** pointing at the Edge Sync Service (host/port/path).
- A **Route** matching the public path Cloud Sync API calls, scoped to the
  methods actually needed.
- **Plugins** on that Route: an auth plugin (e.g. `jwt` or `key-auth`) so
  only the Cloud Sync API can invoke it, and `rate-limiting` scoped to that
  Consumer so a misbehaving sync loop can't overwhelm the edge service.

### Edge Sync Service
The real backend on the local network. Only reachable through Edge Kong —
never exposed directly through the tunnel.

## Data Flow

**Sync (cloud → edge):**
1. Cloud Sync API reads the configured Instrument URL from its App Settings.
2. Issues a fire-and-forget HTTPS request to that URL.
3. The tunnel forwards the request to Edge Kong.
4. Edge Kong matches the request to its configured Route, runs the
   auth and rate-limiting plugins, and proxies to the Edge Sync Service
   via its Service definition.
5. Response travels back the same path.

## Kong Configuration Model (applied to this project)

| Kong object | Used as |
|---|---|
| **Service** | Points Edge Kong at the Edge Sync Service (`host:port` + internal path). |
| **Route** | Defines the public-facing path/method Cloud Sync API calls; `strip_path` maps external path to the Service's internal path. |
| **Consumer** | Represents "Cloud Sync API" as a caller, so plugins can be scoped per-Consumer. |
| **Plugin: auth** (`jwt` or `key-auth`) | Attached to the Route; rejects any caller that isn't the Cloud Sync API Consumer. |
| **Plugin: rate-limiting** | Attached to the Route, scoped to the Consumer; caps how often the cloud side can hit the edge service. |
| **Upstream** | Not used initially (single Edge Sync Service instance). Documented as a future extension point if the edge service is ever scaled to multiple instances. |

## Security Considerations

- Preferring `jwt` over `key-auth` for the Cloud Sync API → Edge Kong hop
  avoids distributing a long-lived shared secret across the tunnel; the
  cloud side signs a short-lived token per call instead.
- The Edge Sync Service should never be reachable except through Edge
  Kong — no direct tunnel exposure.

## Non-Goals / Future Work

- Multi-edge-site support (would require introducing a real registry —
  something like the originally-considered Admin Service — since a single
  config value can't hold per-site URLs).
- Kong Upstream + multiple Edge Sync Service Targets with health checks.
- Observability (Kong logging/Prometheus plugins) — worth adding once the
  base path works end-to-end.
