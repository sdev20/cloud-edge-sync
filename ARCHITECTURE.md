# Architecture

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


- **In-memory edge Kong URL storage.** The Cloud Sync API keeps a simple
  in-memory map of `edge-site-id -> kong URL` and we refer it as **Instrument URL**
- **Single edge site.** The design assumes one Edge Kong / Edge Sync
  Service pair. Multi-site is a natural extension (see Future Work) but
  adds registry and routing concerns that aren't at this point.

## System Diagram

<img width="750" height="409" alt="image" src="https://github.com/user-attachments/assets/f7f4a428-746d-4daf-b786-f85cc1e18338" />


## Components

### Cloud Sync API (Azure)
The cloud-side service that initiates syncs with the edge. Responsibilities:

- **Registration endpoint** — accepts a heartbeat/registration call from
  the edge side containing its current Instrument URL, and updates the
  in-memory map.
- **Sync trigger logic** — on a sync cycle (scheduled or event-driven),
  looks up the current Instrument URL for the target edge site and issues an
  authenticated request through it to Edge Kong.
- **Failure handling** — if a call against a stored Instrument URL fails
  (connection refused, tunnel expired), marks that entry stale and waits
  for the next registration/heartbeat rather than retrying indefinitely.

### Instrument / Tunnel
The mechanism that gives Edge Kong a publicly reachable address despite
sitting on a NAT'd/private local network. Infrastructure choice (Cloudflare Tunnel, ngrok, frp, Tailscale Funnel).
The tunnel's public URL is exactly the "Instrument URL" the Cloud Sync API
stores.

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

**Registration (edge → cloud):**

1. Cloud Sync API updates its in-memory map for that edge site when registered with the endpoint available.
2. Performs a heartbeat call to detect a dead edge site.

**Sync (cloud → edge):**
1. Cloud Sync API looks up the Instrument URL for the target edge site.
2. Issues an authenticated HTTPS request to that URL.
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

- The registration endpoint on Cloud Sync API is itself a trust boundary —
  anything that can call it can redirect where syncs get sent. For a
  learning project this can stay unauthenticated or use a shared secret;
  call this out explicitly as a known simplification, not an oversight.
- Preferring `jwt` over `key-auth` for the Cloud Sync API → Edge Kong hop
  avoids distributing a long-lived shared secret across the tunnel; the
  cloud side signs a short-lived token per call instead.
- The Edge Sync Service should never be reachable except through Edge
  Kong — no direct tunnel exposure.

## Non-Goals / Future Work

- Multi-edge-site support (would reintroduce something like an Admin
  Service, or promote the in-memory map to a real registry/database).
- Kong Upstream + multiple Edge Sync Service Targets with health checks.
- Observability (Kong logging/Prometheus plugins) — worth adding once the
  base path works end-to-end.
