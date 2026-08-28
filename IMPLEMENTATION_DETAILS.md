# Implementation Details

This is the companion to [ARCHITECTURE.md](ARCHITECTURE.md) — that file describes
*what the system is and why it's shaped this way*; this one tracks *how it was
actually built*: concrete commands, config, choices between options, and the
gotchas hit along the way.

## Repo layout

- `SyncService.WebAPI/` — Cloud Sync API solution (Azure), split into
  `SyncService.Domain`, `SyncService.DomainServices`,
  `SyncService.Infrastructure.Client`, and the `SyncService.WebAPI` host project.
- `Edge.WebAPI/` — Edge Sync Service, standalone solution, no shared assemblies
  with the cloud side (deliberate — they're two independently-deployed services).
- `edge-kong/kong.yml` — Kong's declarative (DB-less) config.
- `docker-compose.yml` — brings up the edge stack (`edge-webapi` + `edge-kong`)
  locally. Does **not** include `SyncService.WebAPI` — it has no sibling
  container to coordinate with locally; its only container artifact is a
  `Dockerfile` used by the CI pipeline.
- `.github/workflows/deploy-sync-service.yml` — builds, pushes to ACR, deploys
  `SyncService.WebAPI` to Azure App Service.
- `scripts/provision-azure.sh` — one-time Azure resource provisioning (ACR,
  App Service Plan/Web App, managed identity, OIDC app registration).

## Cloud Sync API (SyncService.WebAPI)

- `InstrumentService.UpdateInstrument` pushes the update to the edge
  **fire-and-forget**, not awaited — the local write shouldn't block on the
  edge/tunnel being reachable. It resolves `ISyncToInstrument` from a **new**
  DI scope via `IServiceScopeFactory` inside the detached `Task.Run`, rather
  than reusing the scoped instance from the original request — the request's
  scope may already be disposed by the time the background work runs. All
  failures are caught and logged (`ILogger`), since an unobserved exception
  in a detached task would otherwise just vanish.


## Containerization

- Both `Dockerfile`s follow the same multi-stage shape: `sdk` image restores +
  publishes, `aspnet` runtime image copies the publish output.
  `ASPNETCORE_URLS=http://+:8080` / `EXPOSE 8080` in both.
- `SyncService.WebAPI/Dockerfile` lives at the **solution folder root**
  (sibling to `SyncService.Domain/`, etc.), not inside the innermost
  `SyncService.WebAPI/SyncService.WebAPI/` project folder — it needs to `COPY`
  sibling project folders for the restore layer, and Docker can't reach
  outside its build context. Build context for CI is `SyncService.WebAPI/`.
- `docker-compose.yml`: `edge-webapi` and `edge-kong` share an `edge-network`
  so Kong can address Edge.WebAPI by container name
  (`http://edge-webapi:8080`). `edge-webapi` deliberately has **no** host
  port for normal traffic — only Kong should be reachable externally — but
  does publish `5160:8080` specifically as a direct-debug escape hatch
  (bypassing Kong to isolate whether a bug is in the backend vs. Kong's
  routing/plugins). `edge-kong` publishes `8000` (proxy) and `8001` (Admin
  API, fine to expose locally, never in a real deployment).

## Kong (`edge-kong/kong.yml`)

- DB-less/declarative mode (`KONG_DATABASE=off`) — no Postgres. One edge
  site, no need for live Admin-API-driven config changes; the whole config is
  a versioned file instead.
- One Service (`edge-sync-service`) → `http://edge-webapi:8080/api/instruments`.
- One Route, `paths: [/external/api/instruments]`, `strip_path: true`,
  `methods: [POST]` — this is what reconciles the route-name mismatch noted
  above: Kong strips the matched public path and appends the Service's own
  `path`, so `POST /external/api/instruments` on the public side becomes
  `POST /api/instruments` on the upstream side.

## Connectivity check (on-demand "heartbeat")

Not an automated heartbeat — a manually-triggered endpoint that tests
whether the full cloud → tunnel → Kong → edge path is currently reachable,
without performing an actual instrument sync. Three pieces:

- **Edge.WebAPI**: `GET api/instruments/health` on the same
  `InstrumentsController`, returns `200 OK`, no body, no business logic.
- **`edge-kong/kong.yml`**: a dedicated Service + Route, not reusing the
  existing POST route — `edge-sync-service-health` →
  `http://edge-webapi:8080/api/instruments/health`, Route
  `paths: [/external/api/instruments/health]`, `strip_path: true`,
  `methods: [GET]`. Kept separate from the sync Route so the two concerns
  (pushing data vs. checking reachability) don't share method/path handling.
- **SyncService.WebAPI**: `ISyncToInstrument.CheckConnectivityAsync` issues a
  `GET` through the same `HttpClient` already pointed at `InstrumentUri`,
  catching `HttpRequestException` and a genuine `TaskCanceledException`
  timeout (filtered with `when (!cancellationToken.IsCancellationRequested)`
  so caller-initiated cancellation still propagates normally) and returning
  `false` rather than throwing — this is meant to report reachability, not
  bubble up a 500 when the edge is down. Exposed via a new
  `InstrumentConnectionController` at `GET /instrument-connection`, returning
  `200` if reachable, `503` if not.

Verified locally both directions: pointed at a live local Kong instance →
`200`; pointed at a port nothing was listening on → clean `503`, not a crash.

## Azure deployment pipeline (SyncService.WebAPI → ACR → App Service)

### Provisioning (`scripts/provision-azure.sh`)

Resource Group → ACR (Basic SKU) → Linux App Service Plan (B1 — Free/F1
doesn't support custom containers) → Web App → system-assigned managed
identity on the Web App granted `AcrPull` on the ACR → App Registration with
a federated OIDC credential (no stored client secret) → that app granted
`AcrPush` on the ACR and `Website Contributor` on the resource group.


# Azure Portal Setup Checklist — ACR + App Service Deployment Pipeline
### GitHub Actions workflow (`.github/workflows/deploy-sync-service.yml`)
The SyncService Web API is deployed to Azure App Service via GibHub Actions Pipeline which is under .github/workflows/deploy-sync-service.yml


1. **Resource Group** — create it (Portal search → "Resource groups" → Create)
2. **Container Registry (ACR)** — Portal search → "Container registries" → Create → same resource group, unique name, SKU Basic
3. **App Service Plan** — Portal search → "App Service plans" → Create → same resource group, **Linux**, **Basic B1** (Free F1 doesn't support containers)
4. **Web App** — Portal search → "App Services" → Create → Web App → **Publish: Docker Container** → select the plan → Docker tab: Single Container, Image Source: Docker Hub, placeholder image `mcr.microsoft.com/dotnet/aspnet:10.0` (pipeline overwrites this later)
5. **Managed identity on the Web App** — Web App → **Identity** → System assigned → On → Save
6. **Grant `AcrPull`** — ACR → **Access control (IAM)** → Add role assignment → Role: `AcrPull` → Assign to: Managed identity → select the Web App's identity
7. **App Registration** — Portal search → "App registrations" → New registration → note the **Application (client) ID** and **Directory (tenant) ID**
8. **Federated credential** — App Registration → Certificates & secrets → Federated credentials → Add credential → Scenario: GitHub Actions → org/repo/branch (`main`)
9. **Grant `AcrPush`** — ACR → IAM → Add role assignment → Role: `AcrPush` → Assign to: the App Registration (search by name)
10. **Grant `Website Contributor`** — Resource Group → IAM → Add role assignment → Role: `Website Contributor` → Assign to: the same App Registration
11. **GitHub secrets** — repo → Settings → Secrets and variables → Actions → add `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (subscription ID from Portal search → "Subscriptions")
12. **Site container config** — Web App → Deployment Center → Containers → Add container: Image source **Azure Container Registry**, Authentication **Managed Identity** (System assigned), fill in Image/Tag/Port manually (required when using managed identity)

## Cloudflare Tunnel (the "Instrument URL")

**Connectivity options considered:**

| Option | Cost | Why / why not |
|---|---|---|
| **Cloudflare Tunnel (chosen)** | Free (quick-tunnel, no account) | Outbound-only from the local network to Cloudflare's edge — no static IP, no inbound firewall rule, no VPN hardware. A named tunnel (free Cloudflare account + a domain) gives a stable URL instead of quick-tunnel's ephemeral one. |
| **ngrok** | Free tier, but now requires an account even for temporary tunnels | Same outbound-tunnel shape as Cloudflare Tunnel; a viable alternative if you don't mind the signup. |
| **Azure VPN Gateway (Site-to-Site)** | ~$27+/month minimum, continuously billed even idle | The real enterprise pattern — likely what a previous employer used for office-to-Azure connectivity. Requires a static/stable public IP (or supported IPsec router) on the local side, which a home network behind CGNAT doesn't have. Wrong tool here on fit, not just cost. |
| **Azure Relay Hybrid Connections** | Usage-billed: hourly per listener + data overage beyond 5 GB/month free per listener | The closer Azure-native match to a tunnel — a local agent makes an outbound connection to Azure Relay, no static IP needed, first-class App Service integration. Reasonable alternative to Cloudflare Tunnel if staying Azure-native mattered more than it did here. |

- Installed via `brew install cloudflared`.
- Quick tunnel (free, no account): `cloudflared tunnel --url http://localhost:8000`
  — points at exactly one destination, Kong's proxy port. There's no
  discovery of other local services; a port not named in that flag (or an
  `ingress` rule, for a named tunnel) has no path from the public URL to it.
- The assigned `*.trycloudflare.com` URL changes every time `cloudflared`
  restarts (inherent to quick-tunnel mode, not a bug) — a named tunnel (free
  Cloudflare account + a domain) would give a stable one instead.
- Set as the `InstrumentConfiguration__InstrumentUri` **Application Setting**
  on the `sync-service-webapi` Web App Azure App service appsettings.
- Verified end-to-end (not just "got a 200"): a request from outside the
  local network to the public tunnel URL came back with Kong-specific
  response headers (`via: 1.1 kong/3.9.3`, `x-kong-proxy-latency`,
  `x-kong-request-id`), and separately, Edge.WebAPI's own application log
  showed the request actually reaching controller code
  (`Received instrument update via sync: ...`). - So if you send request with cloudfared URL
  https://reel-leu-propecia-requires.trycloudflare.com/external/api/instruments and see the request comes to the Edge WebAPI in the logs
