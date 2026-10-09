# Approved model routes for the dual-agent gateway

The user selected free models and the model connection configured on their
computer, then explicitly identified a custom proxy credential. Treat that as
authorization for the specified connection, not permission to copy Codex login
caches, use unrelated endpoints or share a personal credential across all tenants.
Private endpoint/account/key values stay out of this public repository, task
prompts, evidence and release bundles. Models are selected by operator profiles,
never by task prose or untrusted native output.

## Verified connection, not a deployed runtime

On 2026-10-09 the approved proxy accepted a small synthetic Responses request
for the locally configured `gpt-6.1-sol`. HTTPS 200, `text/event-stream` and a
`response.completed` with completed status were observed. Provider-reported usage:
22 input + 5 output = 27 tokens. No repository code was sent, no agent tool was
executed and no redirect was followed. This is one compatibility probe; it is not
the total Codex chat consumption, a zero-cost/billing guarantee or Hermes/OpenCode
installation acceptance. The model catalog returned an empty `models` array;
it does not establish availability of any other model. Keep unknown usage/pricing/
entitlements unknown rather than synthesizing a complete catalog.

The local provider uses `wire_api=responses`. OpenAI's [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)
separates endpoint, model and credential settings; the requested custom endpoint
is not claimed to be an official OpenAI service or official model entitlement.

## Operator templates — disabled, no credentials

- [OpenCode template](../deploy/agents/opencode.proxy.template.json): Responses
  provider package `@ai-sdk/openai`, env credential reference and one allowed
  provider. It remains disabled; automatic runtime updates are off and permissions
  ask by default. These settings do not replace the isolated sandbox or native
  approval latch. See [provider](https://opencode.ai/docs/providers/),
  [configuration](https://opencode.ai/docs/config/) and
  [permission](https://opencode.ai/docs/permissions/) contracts.
- [Hermes template](../deploy/agents/hermes.proxy.template.yaml): named custom
  provider, `key_env` and `transport: codex_responses`, with discovery/activation
  disabled. Verify these fields against the installed pinned runtime's
  [provider contract](https://hermes-agent.nousresearch.com/docs/integrations/providers/)
  before enabling. No automatic provider fallback or built-in credential cache
  is supplied by these templates.

Replace placeholders only from approved operator settings, not from task text.
Set the model key in a private, project-bound runtime secret/environment, separate
from dispatch/callback/native transport credentials. Do not embed the key in JSON/
YAML, process arguments or Docker image layers. Do not mount the operator's full
Codex configuration, home/auth cache, SQL/SSO credentials or host Docker socket in
an agent sandbox. Never pass the model key through the durable harness payload.

The gateway routes to a native provider/model configured on the same reserved
org/workspace/project binding. The primary proxy is not a universal SaaS account:
other tenants need their own authorized connection and budgets. Adding a new
model/provider or failing over after a 429/error requires an approved binding and
availability/capability test; it must not silently send private code elsewhere.

## Free routes

Free model routing is requested but **not enabled** by these templates. An
external free endpoint still needs explicit destination/data policy, current
quota/privacy/tool capability checks and a scoped account when required. A local
open-weight model still consumes CPU/RAM/storage and requires suitable resources;
the current VPS has no confirmed GPU. Do not reserve scarce VPS memory for a large
local model or call the primary proxy free solely because its key already exists.

## Acceptance still required

Pinned native runtime install, non-root sandbox lifecycle/trusted evidence,
request-matched permission replies/reconciliation, trusted run metering ingestion
and one real small task per engine through callback to Review remain separate
gates. Synthetic proxy success does not satisfy those gates or justify marking
the gateway/installation/pilot tasks Done. No provider was installed or activated
on the VPS by adding these files.
