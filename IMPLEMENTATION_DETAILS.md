# Implementation Details

This is the companion to [ARCHITECTURE.md](ARCHITECTURE.md) — that file describes
*what the system is and why it's shaped this way*; this one tracks *how it was
actually built*: concrete commands, config, choices between options, and the
gotchas hit along the way. Update this as implementation continues; keep
`ARCHITECTURE.md` stable unless the design itself changes.

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

- DI wiring lives in `Program.cs`: `InMemoryDataStore` is `Singleton` (state
  must survive across requests), `IInstrumentService` is `Scoped`,
  `ISyncToInstrument`/`SyncToInstrument` is a typed `HttpClient`
  (`AddHttpClient<TInterface, TImpl>`) whose `BaseAddress` is set from
  `InstrumentConfiguration.InstrumentUri` at resolution time.
- `InstrumentService.UpdateInstrument` pushes the update to the edge
  **fire-and-forget**, not awaited — the local write shouldn't block on the
  edge/tunnel being reachable. It resolves `ISyncToInstrument` from a **new**
  DI scope via `IServiceScopeFactory` inside the detached `Task.Run`, rather
  than reusing the scoped instance from the original request — the request's
  scope may already be disposed by the time the background work runs. All
  failures are caught and logged (`ILogger`), since an unobserved exception
  in a detached task would otherwise just vanish.
- Swagger (`AddOpenApi()` + `Swashbuckle.AspNetCore.SwaggerUI`) is registered
  **unconditionally**, not gated behind `IsDevelopment()`. Azure App Service
  defaults `ASPNETCORE_ENVIRONMENT` to `Production`, so the original
  Development-only gate made Swagger unreachable once deployed. Same fix
  applied to `Edge.WebAPI`.

## Edge Sync Service (Edge.WebAPI)

- `InstrumentsController` — `POST api/instruments`, deserializes into a local
  `InstrumentUpdateRequest` record (not the cloud side's `Instrument` type —
  no shared assembly on purpose), logs the payload at `Information`, returns
  `200 OK`.
- Route mismatch, reconciled in Kong rather than in code: `SyncToInstrument`
  posts to `/external/api/instruments`; `InstrumentsController` actually
  listens on `api/instruments`. Kong's `strip_path: true` + the Service's
  `path` bridges the two (see Kong section below) — a deliberate example of
  Kong doing path translation rather than a bug to fix in the app.

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

## Azure deployment pipeline (SyncService.WebAPI → ACR → App Service)

### Provisioning (`scripts/provision-azure.sh`)

Resource Group → ACR (Basic SKU) → Linux App Service Plan (B1 — Free/F1
doesn't support custom containers) → Web App → system-assigned managed
identity on the Web App granted `AcrPull` on the ACR → App Registration with
a federated OIDC credential (no stored client secret) → that app granted
`AcrPush` on the ACR and `Website Contributor` on the resource group.

### GitHub Actions workflow (`.github/workflows/deploy-sync-service.yml`)

- OIDC login via `azure/login@v2`, authenticating as the App Registration —
  needs `permissions: id-token: write` at the job/workflow level or the
  token request fails outright.
- **ACR login workaround:** plain `az acr login` is flaky on GitHub-hosted
  runners ([Azure/azure-cli#26371](https://github.com/Azure/azure-cli/issues/26371)).
  Fixed by pulling a raw token via `--expose-token` and piping it straight
  into `docker login` instead of letting `az acr login` do its own
  AAD-to-Docker exchange.
- **Deploying the image — the App Service is on the newer "sitecontainers"
  (sidecar/multi-container) model, not the classic single-container one.**
  `az webapp config container set` (the classic command) silently doesn't
  apply to this model — using it left the site falling back to admin
  credentials (disabled on the ACR) and returning 503s. The correct command
  is `az webapp sitecontainers update`, which has its own
  `--system-assigned-identity` flag.
- The site container's actual **name** isn't assumed to be fixed — it's
  looked up dynamically at the start of every run
  (`az webapp sitecontainers list | jq ...`) rather than hardcoded, because
  it drifted between `main` and `sync-service-webapi` depending on Portal
  state (see gotchas below). Looking it up avoids hardcoding a name that can
  change out from under the pipeline.
- The `push` trigger is path-filtered to `SyncService.WebAPI/**` and the
  workflow file itself, so edge-side/Kong changes don't trigger a cloud
  redeploy. `workflow_dispatch` is also enabled as a manual escape hatch.

### Gotchas hit along the way (worth knowing if reproducing this)

- **New Azure subscriptions can have a 0 default vCPU quota** for a given
  region/SKU family (`SubscriptionIsOverQuotaForSku`). Fastest fix: try
  creating the App Service Plan in a different region; otherwise request a
  quota increase via the Portal's "Quotas" page.
- **GitHub's OIDC subject claim format changed.** Repos created after
  2026-07-15 use immutable subject claims —
  `repo:org@ownerID/repo@repoID:ref:refs/heads/branch` instead of the classic
  `repo:org/repo:ref:refs/heads/branch`. The federated credential's Subject
  in Azure has to match exactly what GitHub actually sends, which for a new
  repo is the ID-based form.
  ([GitHub changelog](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens/))
- **Azure Portal's Deployment Center has its own "Connect to GitHub" flow**
  that — separately from any hand-written workflow — auto-generates and
  pushes its own competing workflow file, creates its own GitHub secrets, and
  configures the site container itself. Having both running against the same
  Web App caused the site container to end up with two entries both flagged
  `isMain=true` (`main` and `sync-service-webapi`), which made
  `sitecontainers update` fail no matter which name was used until the extra
  one was deleted. Fix: disconnect Deployment Center's GitHub source if
  you're maintaining your own workflow, and clean up any duplicate
  `isMain=true` containers via `az webapp sitecontainers list` /`delete`.
- Local reproduction is a fast way to separate "code bug" from "Azure-specific
  problem": building and running the exact same `Dockerfile` locally
  (`docker build` + `docker run` + `curl`) confirmed the Swagger 404 was not
  a code issue before spending more time on the Azure side.

## Cloudflare Tunnel (the "Instrument URL")

- Installed via `brew install cloudflared`.
- Quick tunnel (free, no account): `cloudflared tunnel --url http://localhost:8000`
  — points at exactly one destination, Kong's proxy port. There's no
  discovery of other local services; a port not named in that flag (or an
  `ingress` rule, for a named tunnel) has no path from the public URL to it.
- The assigned `*.trycloudflare.com` URL changes every time `cloudflared`
  restarts (inherent to quick-tunnel mode, not a bug) — a named tunnel (free
  Cloudflare account + a domain) would give a stable one instead.
- Set as the `InstrumentConfiguration__InstrumentUri` **Application Setting**
  on the `sync-service-webapi` Web App (Environment variables /
  Configuration blade), not in `appsettings.json` — this is exactly why that
  value was left blank in the checked-in config: so it can be updated without
  a code change or redeploy whenever the tunnel URL changes.
- Verified end-to-end (not just "got a 200"): a request from outside the
  local network to the public tunnel URL came back with Kong-specific
  response headers (`via: 1.1 kong/3.9.3`, `x-kong-proxy-latency`,
  `x-kong-request-id`), and separately, Edge.WebAPI's own application log
  showed the request actually reaching controller code
  (`Received instrument update via sync: ...`).
