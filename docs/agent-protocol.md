# Agent Protocol and Rules Core Operating Guide

This document describes how an external reasoning agent drives the Dorks & Dice Operator Runtime without moving application logic into the runtime.

## Protocol choice

Operator Runtime exposes the official C# MCP SDK over Streamable HTTP at /mcp.

The server uses:

- ModelContextProtocol.AspNetCore 2.2.0;
- stateless MCP HTTP transport;
- the existing DORKS_RUNTIME_API_TOKEN trust boundary;
- semantic browser tools backed only by IBrowserOperations.

MCP transport state is stateless. Browser state is intentionally not protocol state; it remains inside BrowserSessionManager.

The versioned /api/v1/browser JSON surface remains supported for non-MCP clients.

## Authentication

Every /mcp request must contain the runtime-control Bearer credential.

~~~http
Authorization: Bearer <DORKS_RUNTIME_API_TOKEN>
~~~

This token authorizes control of Operator Runtime. It is not a Site credential and is never sent into Chromium.

DORKS_OPERATOR_TOKEN is independently held by the runtime server and is used only to obtain and renew the authenticated Site browser session. The resulting normal Site cookie stays inside the isolated Chromium context.

The external model or agent provider should never receive DORKS_OPERATOR_TOKEN or browser cookies. It only needs the MCP endpoint and DORKS_RUNTIME_API_TOKEN.

## Network boundary

Treat the MCP endpoint as an internal control surface.

Recommended deployment:

~~~text
agent host
   |
   | internal/Tailnet HTTPS
   v
Operator Runtime /mcp
   |
   | normal Site HTTPS
   v
Dorks & Dice Site
~~~

Do not expose /mcp anonymously on the public Internet.

If a hosted model can not reach the private endpoint directly, use an approved connector or network bridge that terminates on the trusted side of the runtime-control boundary. Keep that bridge outside BrowserSessionManager and do not add model-provider credentials to Operator Runtime.

## Tool contract

### browser.status

Use before beginning work and after recovery-sensitive failures. It reports whether Chromium is running and whether an authenticated normal Site session is available.

### browser.navigate

Accepts only a Site-relative path or an absolute URL on the configured DORKS_SITE_URL origin.

Foreign origins are rejected. Foreign redirects, links, forms, scripts, and popups are also enforced at the browser-context boundary.

### browser.snapshot

This is the primary observation operation.

It returns:

- sanitized URL and page title;
- headings and bounded visible text;
- semantic interactive elements;
- ephemeral runtime refs;
- accessible names;
- roles and input types;
- disabled state;
- current non-password values;
- checkbox/radio state;
- bounded native select options.

A ref belongs only to the latest snapshot generation.

### browser.click, browser.fill, browser.press, browser.select, browser.set_checked

These are explicit UI actions.

After any one of them, discard every previously observed ref and request a fresh browser.snapshot before another referenced action.

A mutation may have reached the Site before a browser/session failure is detected. The runtime therefore never retries the original operation automatically.

For action results:

~~~text
actionReplayed = false
~~~

If a result reports a recovered session after a failed mutation, inspect the fresh Site state through a new snapshot before deciding what to do next.

### browser.screenshot

Use when semantic text is insufficient to understand visible layout. Screenshots remain in memory and are returned as PNG data.

### browser.console and browser.network_errors

Use for bounded diagnostics. Network diagnostic URLs are sanitized to paths and omit query strings. Request headers are not returned.

## Rules Core adjudication loop

The runtime does not understand D&D rules. The reasoning agent and Rules Core own adjudication decisions.

A conservative agent loop is:

~~~text
browser.status
      |
      v
browser.navigate -> /tools/rules-core
      |
      v
browser.snapshot
      |
      v
find and open Adjudication Queue
      |
      v
browser.snapshot
      |
      v
read one item and visible Rules Core evidence
      |
      v
decide one next UI action
      |
      v
perform exactly one mutation-capable browser tool
      |
      v
browser.snapshot
      |
      v
verify resulting state
~~~

Do not cache refs between steps.

The queue UI may evolve. The runtime intentionally has no hard-coded Rules Core selectors, backend endpoints, or rules_core-prefixed commands. If the future Rules Core UI uses ordinary accessible controls, the same protocol continues to work without a runtime change.

## Three expected adjudication outcomes

For one queue item, the reasoning layer should reach one of three application-level outcomes.

### 1. Normal audited ruling

The agent has enough evidence to make the ruling through the ordinary Rules Core UI.

It should:

1. fill or select only the intended fields;
2. submit the ruling through the normal UI;
3. request a new snapshot;
4. verify that the resulting page reflects the completed action.

The runtime does not determine whether the ruling itself was correct.

### 2. Focused human clarification

The item contains a specific ambiguity that should be answered by a human.

The agent should:

1. use the Rules Core UI to record or request the focused clarification when that surface is available;
2. stop processing that item;
3. continue only after the answer becomes available through the normal application workflow.

The agent should not invent an answer merely to clear the queue.

### 3. Manual escalation

The item is an edge case or otherwise unsuitable for automated adjudication.

The agent should use the normal Rules Core escalation/manual-resolution control and stop automated work on that item.

The runtime does not decide the escalation criterion.

## Publication is separate

A completed adjudication does not imply publication.

Publication must remain a separate, explicit UI operation. The agent must not infer that a successful ruling authorizes publication. Operator Runtime never publishes on its own and contains no automatic publication policy.

## Form-control guidance

Use semantic operations rather than selectors.

- Text input or textarea: browser.fill.
- Native select: inspect options from browser.snapshot, then browser.select by exact value or label.
- Checkbox or radio: browser.set_checked.
- Button or link: browser.click.
- Keyboard-driven submission: browser.press with Enter when appropriate.
- Standard submit button: browser.click.

If a control is absent from the semantic snapshot because it is hidden or detached, do not try to bypass the UI with JavaScript. Navigate or interact with the page normally until the control is actually visible and usable.

## Failure handling

### Invalid/stale ref

Request a new snapshot. Do not guess a ref.

### Foreign-origin failure

Treat the operation as blocked. The runtime may replace the affected browser context with a fresh authenticated Site session.

### Authentication or browser loss during a mutation

The mutation may already have succeeded. The runtime will not replay it.

Request browser.status and a new browser.snapshot, then verify current Site state before issuing any follow-up mutation.

### Diagnostics show an application failure

Use the normal Site UI and bounded diagnostics to understand the failure. Do not add a generic HTTP fetch, arbitrary JavaScript, shell command, or direct Tool backend call to bypass the application boundary.

## Service-principal authorization

The service principal is an ordinary authenticated Site identity for authorization purposes.

It needs whatever normal Site roles its task requires. Rules Core source grants remain independent of Operator Runtime and must be configured through the normal authorization model.

Operator Runtime does not create an AI superuser and does not grant itself broader Tool access.

## Provider independence

The model or reasoning layer can be:

- a hosted MCP-capable model;
- ChatGPT through an appropriate private-network-capable integration;
- a local model;
- a custom deterministic agent;
- another future MCP client.

Operator Runtime does not choose the model and does not hold model API keys.

## Generic client configuration

Configure an MCP client with:

~~~text
Transport: Streamable HTTP
Endpoint:  https://<internal-runtime-host>/mcp
Header:    Authorization: Bearer <value supplied from the client secret store>
~~~

The client secret should be loaded through its own secret store or environment configuration, not copied into prompts or source files.

A C# client using the same SDK can load the token from its environment:

~~~csharp
using ModelContextProtocol.Client;

var token =
    Environment.GetEnvironmentVariable("DORKS_RUNTIME_API_TOKEN")
    ?? throw new InvalidOperationException("Runtime token is missing.");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://operator-runtime.internal.example/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    EnableStandaloneGetStream = false,
    OwnsSession = false,
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {token}"
    }
});

await using var client = await McpClient.CreateAsync(transport);

foreach (var tool in await client.ListToolsAsync())
{
    Console.WriteLine($"{tool.Name}: {tool.Description}");
}
~~~

No OpenAI, Ollama, llama.cpp, or other provider credential is required by this code path.

## Deterministic validation

CI uses a loopback fake Site rather than the production service. The fake Rules Core-style page uses accessible select, radio, checkbox, textarea, and submit controls.

The MCP integration test validates that a protocol client can:

1. authenticate to /mcp;
2. discover the browser tools;
3. read browser status;
4. navigate to a Rules Core-style page;
5. snapshot semantic controls;
6. select an adjudication outcome;
7. observe stale-ref rejection;
8. fill a textarea;
9. set checkbox and radio state;
10. submit through a normal UI control;
11. submit a form through Enter;
12. recover after context loss;
13. observe mutation non-replay after authentication loss;
14. reject foreign-origin navigation;
15. retrieve screenshot and bounded diagnostics;
16. confirm runtime credentials, Operator credentials, browser cookies, and query secrets are absent from returned diagnostics.

Production-site smoke testing remains optional and credential-safe.
