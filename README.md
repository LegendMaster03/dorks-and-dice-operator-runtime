# Dorks & Dice Operator Runtime

The Operator Runtime is the external browser-control service between an agent and the normal Dorks & Dice Site.

```text
ChatGPT / local model
        ↓
Operator Runtime
        ↓
Playwright / Chromium
        ↓
Dorks & Dice Site
        ↓
normal Site + Tool Host
```

The runtime is deliberately thin. It owns an authenticated Chromium session and exposes a small browser-control API. Site authorization, Tool authorization, Rules Core behavior, campaign behavior, and other Dorks & Dice domain logic remain where they already live.

## What this service is not

It is not another Rules API, a Tool proxy, a Site authorization system, a general remote shell, or a replacement for Tool Host. It does not call Tool backends directly and it does not create Tool Host tickets.

The trust boundaries remain separate:

```text
Operator credential
!= browser cookie
!= runtime API credential
!= Tool credential
```

```text
agent authorization
    ↓
runtime control authorization

Site authorization
    ↓
service-principal Site identity

Tool authorization
    ↓
existing Tool Host + Tool authorization
```

## Runtime protocol

The first version exposes a small versioned JSON HTTP API rather than coupling the browser layer to a particular model or an uncertain MCP implementation.

```text
agent
  ↓
JSON HTTP /api/v1
  ↓
IBrowserOperations
  ↓
BrowserSessionManager
  ↓
Playwright / Chromium
```

An MCP adapter can be added later without changing the Site client or browser/session classes.

The API operations are:

| Operation | HTTP endpoint |
| --- | --- |
| `browser.status` | `GET /api/v1/browser/status` |
| `browser.navigate` | `POST /api/v1/browser/navigate` |
| `browser.snapshot` | `GET /api/v1/browser/snapshot` |
| `browser.click` | `POST /api/v1/browser/click` |
| `browser.fill` | `POST /api/v1/browser/fill` |
| `browser.press` | `POST /api/v1/browser/press` |
| `browser.screenshot` | `GET /api/v1/browser/screenshot` |
| `browser.console` | `GET /api/v1/browser/console` |
| `browser.network_errors` | `GET /api/v1/browser/network-errors` |

All `/api/v1` endpoints require:

```http
Authorization: Bearer <DORKS_RUNTIME_API_TOKEN>
```

This token is distinct from the Site Operator credential and is never sent into Chromium.

## Configuration

Required for service mode:

```text
DORKS_SITE_URL=https://dorks-and-dice.com
DORKS_OPERATOR_TOKEN=<secret>
DORKS_RUNTIME_API_TOKEN=<separate-secret>
```

Optional:

```text
DORKS_BROWSER_HEADLESS=true
DORKS_BROWSER_TIMEOUT_SECONDS=30
ASPNETCORE_URLS=http://0.0.0.0:8080
```

`DORKS_SITE_URL` must be an HTTPS origin. Loopback HTTP is accepted only for local development and deterministic integration tests.

Do not place either credential in source control, command-line arguments, browser storage, browser URLs, screenshots, or logs. Supply them through deployment secrets or another normal .NET configuration source.

## Site bootstrap flow

At startup the runtime performs this sequence:

1. `GET /operator/v1/me` with `DORKS_OPERATOR_TOKEN`.
2. Start Playwright and Chromium.
3. `POST /operator/v1/browser-bootstrap` with `DORKS_OPERATOR_TOKEN`.
4. Resolve the exact relative `bootstrapUrl` returned by the Site.
5. Create a fresh isolated browser context.
6. Navigate Chromium to the bootstrap URL.
7. Let the Site consume the one-use token and issue its normal Identity cookie.
8. Verify authentication by opening the normal authenticated `/account` Site route in the same browser context.
9. Keep that context for subsequent browser commands.

The Operator bearer credential is used only by the typed Operator API client. It is not configured as a browser header, cookie, local-storage value, or Tool credential.

## Browser lifecycle and recovery

One Chromium browser and one authenticated context are used for the MVP. Browser commands that interact with the page are serialized so two agent commands can not race the same page.

If the context or browser is lost, the runtime discards the unusable context and requests a new Site bootstrap. If a failure happens during an operation that may already have caused a mutation, the runtime restores the session but returns an error with `actionReplayed: false`. It never silently repeats the original action.

Snapshot element references are ephemeral. Navigation, click, key presses that may change page state, and session recovery invalidate them. Request a new snapshot after those operations.

## Navigation policy

`browser.navigate` accepts Site-relative paths such as:

```text
/tools/rules-core
/tools/block-initiative
/tools/hex-crawl
/admin
/articles
```

Absolute URLs are accepted only when their scheme, host, and port exactly match `DORKS_SITE_URL`. Scheme-relative URLs, credential-bearing URLs, and unrelated origins are rejected.

The runtime does not expose arbitrary JavaScript evaluation, shell execution, filesystem access, generic HTTP proxying, external-origin browsing, host Docker access, SSH, or unrestricted upload/download operations.

## Snapshot model

Snapshots return a concise semantic representation:

- current URL and title;
- headings;
- bounded visible body text;
- up to 500 interactive elements;
- runtime-issued references such as `e1`, `e2`, and `e3`;
- role, accessible-ish name, input type, and disabled state.

The runtime keeps the element handle behind each reference. Agents use those references with click, fill, or press rather than supplying JavaScript.

## Diagnostics

Console messages and network failures are held in bounded in-memory buffers. Network diagnostics contain method, sanitized URL path, status when available, and failure kind. Query strings and request headers are not returned, so Authorization and Cookie values are not exposed.

Screenshots are returned as an in-memory base64 PNG result. The runtime does not persist every screenshot to disk.

## Health

- `GET /health/live` reports that the process is serving requests.
- `GET /health/ready` reports both browser initialization and Site-session availability.

Health endpoints do not return credentials or cookies.

## Deployment sequence

1. Deploy the Dorks & Dice Site version that contains the Operator Interface.
2. In the Site, open **Admin → Agents**.
3. Create a service principal with the roles and campaign access it actually needs.
4. Copy the one-time `ddop_v1_...` credential.
5. Deploy this runtime separately from the Site.
6. Inject `DORKS_SITE_URL` securely.
7. Inject `DORKS_OPERATOR_TOKEN` securely.
8. Generate and inject a separate `DORKS_RUNTIME_API_TOKEN`.
9. Keep the runtime API on an internal/Tailnet-facing deployment boundary; do not publish it as an unauthenticated Internet API.
10. Start the runtime and verify `/health/ready`.
11. Configure the agent-side client to call `/api/v1/browser/*` with the runtime API credential.

The runtime needs normal HTTPS access to the Site. It does not need the Site source tree, Site database, Docker socket, host browser profile, or host filesystem mounts.

## Docker

Build:

```bash
docker build -t dorks-and-dice-operator-runtime .
```

Run it with secrets supplied by the deployment environment. The final container runs as the Playwright image's unprivileged `pwuser` and uses the Playwright-matched Chromium image.

The project pins Microsoft.Playwright and the Playwright container to the same version.

## Credential rotation

To rotate the Site Operator credential:

1. Create a new Operator credential in **Admin → Agents**.
2. Update the runtime's `DORKS_OPERATOR_TOKEN` secret.
3. Restart the runtime so startup verifies the replacement credential and establishes a new browser session.
4. Verify `/health/ready`.
5. Revoke the previous Operator credential.

Revoking an Operator credential also invalidates Site browser sessions bound to that credential. The runtime will not turn a revoked credential into a valid browser session; recovery requires a currently valid Operator credential.

Rotate `DORKS_RUNTIME_API_TOKEN` independently when the runtime-control trust boundary changes.

## Local validation

Normal CI does not require a real Dorks & Dice Site or production credential. Tests start a deterministic loopback fake Site that verifies the complete bootstrap and browser interaction flow.

```bash
dotnet restore DorksAndDice.OperatorRuntime.slnx
dotnet build DorksAndDice.OperatorRuntime.slnx --configuration Release --no-restore
dotnet test DorksAndDice.OperatorRuntime.slnx --configuration Release --no-build
```

GitHub Actions runs those commands once inside the Playwright .NET image, avoiding repeated browser downloads across separate jobs.

## Optional real-Site smoke test

The smoke mode requires only the real Site URL and Operator credential. It does not start the runtime HTTP control API and therefore does not require `DORKS_RUNTIME_API_TOKEN`.

```bash
DORKS_SITE_URL=https://dorks-and-dice.com \
DORKS_OPERATOR_TOKEN='<secret>' \
dotnet run --project src/DorksAndDice.OperatorRuntime -- --smoke
```

To include Rules Core navigation:

```bash
DORKS_SITE_URL=https://dorks-and-dice.com \
DORKS_OPERATOR_TOKEN='<secret>' \
dotnet run --project src/DorksAndDice.OperatorRuntime -- --smoke --rules-core
```

The smoke command prints only safe service-principal metadata and success/failure state. It does not print the Operator credential, bootstrap URL, bootstrap token, or browser cookies.

## Connecting ChatGPT

This branch intentionally stops at the model-neutral versioned HTTP boundary. For ChatGPT to invoke the runtime directly, the deployment still needs an agent-side integration that can reach the internal runtime and translate ChatGPT tool calls to these operations. The clean next step is an MCP or ChatGPT connector adapter over `IBrowserOperations`; no Site or Tool authorization changes are required for that adapter.
