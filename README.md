# Dorks & Dice Operator Runtime

The Operator Runtime is the browser-control and security boundary between an external reasoning agent and the normal authenticated Dorks & Dice Site.

~~~text
AI agent / hosted model / local model
        |
        v
agent protocol adapter
        |
        v
Operator Runtime
        |
        v
IBrowserOperations
        |
        v
Playwright / authenticated Chromium
        |
        v
normal Dorks & Dice Site
        |
        v
Tool Host
        |
        v
Rules Core and other Tools
~~~

The runtime is deliberately thin. It owns an authenticated Chromium session and exposes semantic browser operations. Site authorization, Tool authorization, Rules Core adjudication logic, campaign behavior, publication policy, and other Dorks & Dice domain logic remain in their existing services.

## What this service is not

It is not another Rules API, a Rules Core backend client, a Tool proxy, a Site authorization system, a general remote shell, or a replacement for Tool Host.

The runtime does not:

- call Rules Core or another Tool backend directly;
- connect to a Dorks & Dice database;
- create Tool Host tickets directly;
- decide whether rules are equivalent or safe to merge;
- decide when a human should approve a ruling;
- decide what a house rule means;
- decide what should be published;
- expose arbitrary browser JavaScript;
- expose shell or filesystem access;
- expose a generic HTTP proxy;
- expose Docker, SSH, or unrestricted upload/download capabilities.

## Trust boundaries

The credentials are intentionally separate.

~~~text
runtime-control credential
        !=
Operator service-principal credential
        !=
normal Site browser cookie
        !=
Tool authorization or source grants
        !=
model-provider credential
~~~

The responsibilities are:

| Credential or identity | Used by | Purpose |
| --- | --- | --- |
| DORKS_RUNTIME_API_TOKEN | external runtime client | Authenticates the JSON API and MCP endpoint |
| DORKS_OPERATOR_TOKEN | Operator Runtime server process | Authenticates only to the Site Operator Interface and obtains one-use browser bootstrap URLs |
| normal Site Identity cookie | isolated Chromium context | Authenticates ordinary Site UI requests |
| Site roles and Tool/source grants | normal Site and Tool Host authorization | Determines what the service principal may actually see and do |
| hosted/local model credential | external agent host | Remains outside Operator Runtime |

DORKS_RUNTIME_API_TOKEN and DORKS_OPERATOR_TOKEN are never configured as browser headers, cookies, local-storage values, or page content.

## Supported agent interfaces

The runtime exposes two interfaces over the same IBrowserOperations abstraction.

### MCP

The supported model-facing interface is MCP over Streamable HTTP at:

~~~text
/mcp
~~~

The implementation uses the official ModelContextProtocol.AspNetCore package pinned to version 2.2.0. The server is stateless because the runtime does not need MCP server-to-client requests or protocol session state; browser state remains owned by BrowserSessionManager.

Every MCP request requires:

~~~http
Authorization: Bearer <DORKS_RUNTIME_API_TOKEN>
~~~

The MCP endpoint is intended for an internal or Tailnet-facing deployment. It must not be exposed as an unauthenticated public browser-control endpoint.

### Versioned JSON API

The existing versioned JSON interface remains available under:

~~~text
/api/v1/browser
~~~

It uses the same DORKS_RUNTIME_API_TOKEN boundary. It is useful for deterministic clients, diagnostics, and integrations that do not speak MCP.

## MCP browser tools

The MCP adapter is in the AgentProtocol namespace and contains no Rules Core domain logic.

| Tool | Purpose |
| --- | --- |
| browser.status | Browser and authenticated Site-session status |
| browser.navigate | Navigate only within the configured Dorks & Dice Site origin |
| browser.snapshot | Semantic page snapshot and current ephemeral element references |
| browser.click | Click a referenced semantic element |
| browser.fill | Fill text-like input and textarea controls |
| browser.press | Send a keyboard key to a referenced element or the page |
| browser.select | Select one native select option by value or visible label |
| browser.set_checked | Set native checkbox or radio state |
| browser.screenshot | Return an in-memory PNG screenshot |
| browser.console | Read the bounded console diagnostic buffer |
| browser.network_errors | Read bounded sanitized network failures |

Mutation-capable tool descriptions explicitly tell the agent that:

- element references come from the latest browser.snapshot;
- DOM-changing operations invalidate all prior references;
- the agent must obtain a new snapshot after such an operation;
- a mutation may have succeeded before a browser or Site-session failure becomes visible;
- the runtime never automatically replays the original mutation;
- actionReplayed is false;
- only the configured Dorks & Dice Site origin can be navigated.

## Browser-control completeness

The runtime intentionally exposes semantic, generally useful browser operations rather than application-specific selectors.

Ordinary accessible form controls are handled as follows:

| Control | Operation |
| --- | --- |
| text input | browser.fill |
| textarea | browser.fill |
| native select | browser.select |
| checkbox | browser.set_checked |
| radio | browser.set_checked |
| submit button | browser.click |
| keyboard-driven form submission | browser.press with Enter |

No Rules Core-specific browser command is required.

Snapshots include visible attached interactive controls, accessible names, input type, disabled state, current control value, checked state where relevant, and bounded select-option metadata. Password input values are not returned in snapshot metadata.

## Configuration

Required for service mode:

~~~text
DORKS_SITE_URL=https://dorks-and-dice.com
DORKS_OPERATOR_TOKEN=<secret supplied by deployment secret storage>
DORKS_RUNTIME_API_TOKEN=<separate secret supplied by deployment secret storage>
~~~

Optional:

~~~text
DORKS_BROWSER_HEADLESS=true
DORKS_BROWSER_TIMEOUT_SECONDS=30
ASPNETCORE_URLS=http://0.0.0.0:8080
~~~

DORKS_SITE_URL must be an HTTPS origin. Loopback HTTP is accepted only for local development and deterministic integration tests.

Do not place either runtime credential in source control, browser storage, browser URLs, screenshots, model prompts, or logs. Prefer an orchestrator secret store, service manager environment file with appropriate filesystem permissions, or another normal .NET configuration provider. Do not put secret values directly in process command-line arguments.

## Site bootstrap flow

At startup the runtime performs this sequence:

1. Calls the Site Operator Interface with DORKS_OPERATOR_TOKEN to read the service-principal identity.
2. Starts Playwright and Chromium.
3. Requests a one-use browser bootstrap from the Site with DORKS_OPERATOR_TOKEN.
4. Verifies that the returned bootstrap URL is on the configured Site origin.
5. Creates a fresh isolated Chromium context with service workers disabled.
6. Navigates Chromium to the one-use bootstrap URL.
7. Lets the Site consume the bootstrap and issue its normal Identity cookie.
8. Verifies authentication against the ordinary authenticated account route.
9. Keeps that browser context for later semantic browser operations.

The Operator bearer credential is used only by the typed Operator Interface client.

## Browser lifecycle and recovery

Page-interacting browser commands are serialized so two agent commands can not race the same page.

If the browser context or Site session is lost, the runtime can establish a fresh authenticated context. If failure occurs during an action that may already have mutated application state, the runtime recovers the session when possible but returns a failure for the original action. It never silently repeats that action.

Snapshot element references are ephemeral. Navigation, click, fill, press, select, checked-state changes, and session recovery invalidate the current reference set.

A correct agent therefore follows:

~~~text
snapshot
  |
  v
one explicit action
  |
  v
new snapshot
~~~

rather than caching element references across UI changes.

## Navigation and browser boundary

browser.navigate accepts Site-relative paths such as:

~~~text
/tools/rules-core
/tools/block-initiative
/tools/hex-crawl
/admin
/articles
~~~

Absolute URLs are accepted only when scheme, host, and port exactly match DORKS_SITE_URL. Scheme-relative URLs, credential-bearing URLs, unrelated origins, foreign redirects, foreign links, foreign form submissions, foreign script navigation, and uncontrolled popups are blocked.

If a foreign-origin page is reached despite interception, the affected context is discarded and a fresh authenticated Site session is established before later browser commands are accepted.

Cross-origin subresources are not globally blocked because normal Site pages may need external images, fonts, styles, or scripts. Service workers are disabled so they can not bypass the top-level navigation boundary.

## Snapshots and diagnostics

A semantic snapshot includes:

- sanitized current URL and title;
- headings;
- bounded visible body text;
- up to 500 visible attached interactive elements;
- runtime-issued references such as e1, e2, and e3;
- common HTML/ARIA accessible names;
- role and input type;
- disabled state;
- current non-password control value;
- checked state for checkbox/radio-style controls;
- up to 100 options for each native select.

The runtime keeps the actual element handle behind each reference. Agents do not supply selectors or JavaScript.

Console and network diagnostics are held in bounded in-memory buffers. Network diagnostic URLs omit query strings, and request headers are not returned. Authorization and Cookie values therefore do not appear in model-visible network diagnostics.

Screenshots are returned in memory and are not automatically written to disk.

## Intended Rules Core agent loop

Rules Core remains a normal Site/Tool workflow. The Operator Runtime does not know what a valid ruling is.

The intended loop is:

~~~text
navigate to Rules Core
        |
        v
snapshot
        |
        v
open Adjudication Queue
        |
        v
snapshot current work item
        |
        v
inspect evidence through normal UI
        |
        v
take one explicit UI action
        |
        v
new snapshot
        |
        v
continue
~~~

The reasoning agent has three expected outcomes for an adjudication item:

1. Make a normal audited ruling through the UI.
2. Ask a human one focused question and stop processing that item until the answer is available.
3. Escalate the item for manual resolution.

Publication is a separate explicit UI operation. Neither Operator Runtime nor its MCP adapter decides to publish.

See docs/agent-protocol.md for detailed operating and connection guidance.

## Service-principal prerequisites

Before the runtime can automate a task:

1. The Site Operator Interface must be deployed.
2. An administrator creates a service principal in the normal Site administration UI.
3. The service principal receives only the ordinary Site roles and campaign access needed for its task.
4. Any Rules Core source grants remain configured independently through the existing Rules Core/Tool authorization model.
5. The one-time Operator credential is stored as DORKS_OPERATOR_TOKEN in the runtime deployment.
6. A separate DORKS_RUNTIME_API_TOKEN is generated for clients allowed to control this runtime.

The runtime does not provide an AI superuser and does not bypass ordinary authorization.

## Connecting an external agent

An MCP-capable hosted or local agent connects to the Streamable HTTP endpoint at /mcp and supplies DORKS_RUNTIME_API_TOKEN as the Bearer Authorization header.

The runtime does not contain OpenAI, ChatGPT, Ollama, llama.cpp, or another model-provider SDK. Model credentials stay with the external agent host.

A generic C# MCP client can use the same official SDK:

~~~csharp
using ModelContextProtocol.Client;

var runtimeToken =
    Environment.GetEnvironmentVariable("DORKS_RUNTIME_API_TOKEN")
    ?? throw new InvalidOperationException("DORKS_RUNTIME_API_TOKEN is not configured.");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://operator-runtime.internal.example/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    EnableStandaloneGetStream = false,
    OwnsSession = false,
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {runtimeToken}"
    }
});

await using var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
~~~

Use an endpoint reachable from the agent host without making the runtime public. For a hosted agent that can not directly reach the Tailnet/internal service, place the necessary approved connector or network bridge outside Operator Runtime. Do not solve reachability by putting model-provider credentials into this service or by making /mcp anonymous.

## Health

- GET /health/live reports that the process is serving requests.
- GET /health/ready reports browser initialization and authenticated Site-session availability.

Health endpoints do not return credentials or cookies.

## Deployment sequence

1. Deploy the Dorks & Dice Site version containing the Operator Interface.
2. Create the service principal under the Site administration surface.
3. Give it only the ordinary Site roles and Tool/source grants required by the intended workflow.
4. Store the one-time Operator credential as DORKS_OPERATOR_TOKEN in deployment secret storage.
5. Generate a separate DORKS_RUNTIME_API_TOKEN and store it independently.
6. Deploy Operator Runtime separately from the Site.
7. Set DORKS_SITE_URL.
8. Keep the runtime listener on an internal or Tailnet-facing network boundary.
9. Start the runtime and verify /health/ready.
10. Configure the external MCP client for the /mcp Streamable HTTP endpoint and Bearer runtime-control token.
11. Verify browser.status, browser.navigate, and browser.snapshot before allowing mutation-capable calls.

The runtime needs normal HTTPS access to the Site. It does not need the Site source tree, Site database, Docker socket, host browser profile, or arbitrary host filesystem mounts.

## Docker

Build:

~~~bash
docker build -t dorks-and-dice-operator-runtime .
~~~

Run it with secrets supplied by the deployment environment or orchestrator rather than literal secret values in the command line.

The final container runs as the Playwright image's unprivileged pwuser and uses the Playwright-matched Chromium image. Microsoft.Playwright and the Playwright container remain pinned to the same browser version.

## Credential rotation

To rotate the Site Operator credential:

1. Create a replacement Operator credential in the Site administration UI.
2. Update the DORKS_OPERATOR_TOKEN deployment secret.
3. Restart the runtime so it verifies the replacement and establishes a fresh browser session.
4. Verify /health/ready.
5. Revoke the previous Operator credential.

Rotate DORKS_RUNTIME_API_TOKEN independently when the runtime-control trust boundary changes.

Revoking an Operator credential also invalidates Site browser sessions bound to that credential. Recovery requires a currently valid Operator credential.

## Local deterministic validation

Normal CI does not require the production Dorks & Dice Site or production credentials. The tests start loopback fake Site and MCP servers and exercise the same browser/session and protocol layers.

~~~bash
dotnet restore DorksAndDice.OperatorRuntime.slnx
dotnet build DorksAndDice.OperatorRuntime.slnx --configuration Release --no-restore
dotnet test DorksAndDice.OperatorRuntime.slnx --configuration Release --no-build
~~~

The protocol integration test covers:

- runtime/MCP authentication;
- MCP tool discovery;
- browser status;
- same-origin navigation;
- semantic snapshots;
- select, checkbox, radio, textarea, click, and press behavior;
- reference invalidation;
- ordinary form submission;
- browser/session recovery;
- mutation non-replay;
- foreign-origin rejection;
- screenshot access;
- bounded sanitized diagnostics;
- credential and cookie non-disclosure.

GitHub Actions runs restore, build, and test inside the Playwright .NET image.

## Optional real-Site smoke path

The smoke mode uses the real Site URL and Operator credential already supplied through the process environment or deployment secret provider. It does not start the runtime-control HTTP interfaces and therefore does not require DORKS_RUNTIME_API_TOKEN.

~~~bash
dotnet run --project src/DorksAndDice.OperatorRuntime -- --smoke
~~~

To include ordinary Rules Core navigation:

~~~bash
dotnet run --project src/DorksAndDice.OperatorRuntime -- --smoke --rules-core
~~~

The smoke path prints only safe service-principal metadata and success/failure state. It does not print the Operator credential, bootstrap URL, bootstrap token, or browser cookies.
