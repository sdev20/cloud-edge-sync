# Architecture

For concrete commands, config, and implementation gotchas, see
[IMPLEMENTATION_DETAILS.md](IMPLEMENTATION_DETAILS.md).

## Overview

`cloud-edge-sync` is a hands-on reference project for learning Kong API
Gateway by building a realistic cloud-to-edge topology: a cloud-hosted sync
API that needs to reach a backend service on a private local network, where
that network has no fixed public address. Kong sits at the edge as the
gateway, owning routing for everything that crosses into the local network.
The original intent also included Kong-level authentication and rate
limiting; those were never implemented before the project was stopped — see
Known Limitations.

**Project status: stopped.** This document describes the system as it was
left, not a system still being actively extended.

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
  Service pair. Multi-site would be a natural extension (see Known
  Limitations) but adds registry and routing concerns not addressed here.

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
  background heartbeat — but it's a ready-made foundation for one: a
  possible future feature is a timer (or scheduled job) calling this same
  endpoint periodically and alerting when it starts failing, turning today's
  manual check into an automated heartbeat without changing the endpoint
  itself.

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
As built, it owns two Service/Route pairs (one for the actual sync push,
one for the on-demand health check), each:

- A **Service** pointing at the Edge Sync Service (host/port/path).
- A **Route** matching the public path Cloud Sync API calls, scoped to the
  method actually needed, with `strip_path` reconciling the public path
  against the Edge Sync Service's own route.

**Not implemented:** no plugins are attached to either Route. The original
intent was an auth plugin (`jwt` or `key-auth`) so only the Cloud Sync API
could invoke it, plus `rate-limiting` scoped to that Consumer — see Known
Limitations for why this was never built.

### Edge Sync Service
The real backend on the local network. Only reachable through Edge Kong —
never exposed directly through the tunnel.

## Data Flow

**Sync (cloud → edge):**
1. Cloud Sync API reads the configured Instrument URL from its App Settings.
2. Issues a fire-and-forget HTTPS request to that URL.
3. The tunnel forwards the request to Edge Kong.
4. Edge Kong matches the request to its configured Route and proxies to the
   Edge Sync Service via its Service definition. No auth or rate-limiting
   plugins run — none are configured (see Known Limitations).
5. Response travels back the same path.

## Kong Configuration Model (as built)

| Kong object | Used as |
|---|---|
| **Service** | Points Edge Kong at the Edge Sync Service (`host:port` + internal path). Two exist — one for the sync push, one for the health check. |
| **Route** | Defines the public-facing path/method Cloud Sync API calls; `strip_path` maps external path to the Service's internal path. |
| **Upstream** | Not used (single Edge Sync Service instance, so no load balancing is needed). |

**Not built:** Consumer, an auth plugin (`jwt`/`key-auth`), a rate-limiting
plugin. See Known Limitations.

## Security Considerations

- **No authentication on either Kong Route.** Anyone who obtains the tunnel
  URL can POST arbitrary instrument data or hit the health check — this is
  the most significant gap left open when the project was stopped. Had auth
  been built, `jwt` was the intended choice over `key-auth`, since it avoids
  distributing a long-lived shared secret across the tunnel (the cloud side
  would sign a short-lived token per call instead) — see Known Limitations.
- The Edge Sync Service should never be reachable except through Edge Kong
  **from the public internet** — the tunnel only ever exposes Kong's proxy
  port. Locally, `edge-webapi` is also published on `:5160` as a deliberate
  direct-debug exception (see IMPLEMENTATION_DETAILS.md) — not reachable
  from outside the local network, but worth knowing it bypasses Kong
  entirely when used.

## Known Limitations

This is where the project was stopped. Kept as an honest record of what's
missing, not a promise of future work:

- **No Kong authentication** (`jwt` or `key-auth`) on either Route — both
  are open to anyone with the tunnel URL.
- **No Kong rate-limiting** — nothing stops a misbehaving sync loop, or an
  outsider, from flooding either Route.
- **No Consumer defined** — a prerequisite for both of the above; Kong's
  auth and per-caller rate-limiting plugins both authenticate/scope against
  a Consumer.
- **Single edge site only** — multi-site support would require a real
  registry (something like the originally-considered Admin Service), since
  a single config value can't hold per-site URLs.
- **No Kong Upstream / load balancing** — fine for one Edge Sync Service
  instance, would matter if the edge service were ever scaled out.
- **No observability** — no Kong logging or Prometheus plugins; the only
  visibility into whether the edge is reachable is the manual
  `/instrument-connection` check.
- **No automated heartbeat.** `/instrument-connection` is on-demand only —
  something has to actually call it. A real heartbeat (the Cloud Sync API
  polling it on a timer and alerting on failure, or the edge side pushing a
  periodic signal) is a natural next step building on the same endpoint;
  just not built here.
