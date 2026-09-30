# ACP protocol foundation (not an enabled execution provider)

ACP here means **Agent Client Protocol**, distinct from MCP tools and LSP language
analysis. The infrastructure adapter implements a narrow v1 stream-client contract.
It does not launch agents, obtain credentials, register a run provider or dispatch
the operational delivery flow. The full ACP story remains incomplete until a real
gateway is selected, connected to durable run state and accepted end to end.

## Pinned reference

Official schema revision inspected 2026-09-30:
[v1 schema at f05af18d9708f31c85fa62e172ac0968df042cf0](https://raw.githubusercontent.com/agentclientprotocol/agent-client-protocol/f05af18d9708f31c85fa62e172ac0968df042cf0/schema/v1/schema.json).
UTF-8 content SHA256: `3C17BD6385D90CF672D8A661FDDC359D73422CF8B8CE6865213D25CFD4C0ECA7`.
This is a manually implemented subset, not generated/full schema support.
[Initialization](https://agentclientprotocol.com/protocol/v1/initialization),
[session setup](https://agentclientprotocol.com/protocol/v1/session-setup),
[prompt/cancel](https://agentclientprotocol.com/protocol/v1/prompt-turn) and
[stdio transport](https://agentclientprotocol.com/protocol/v1/transports) define
the handshake. HTTP remains outside this adapter; do not confuse Neo's existing
HTTP harness contract with ACP or advertise ACP HTTP support from it.

## Implemented boundary

- Newline-delimited UTF-8 JSON-RPC, bounded 1 MiB messages; no LSP Content-Length.
- Single initialization with protocolVersion 1; mismatches fail closed.
- New session for an explicit local workspace; no MCP servers supplied implicitly.
- One explicitly authorized text prompt; images/audio/resources, session reload,
  dynamic config and authentication flows are not implemented.
- Streamed public message text is bounded to 64 KiB. Output remains untrusted.
- Correlation contains organization/workspace/work item/run/trace identifiers.
  These are metadata supplied by the caller, NOT proof of authorization or a new
  durable AgentRun mapping. An eventual gateway must validate its dispatch record.
- Trace callbacks carry only fixed event categories and correlation, never raw
  tool arguments/diffs, prompts, message text or internal reasoning.
- All permission requests return cancelled; no silent approval. Client filesystem
  and terminal capabilities are disabled and such requests return method-not-found.
- Cancellation sends session/cancel, keeps reading the same pending frame, denies
  late permissions and waits at most five seconds for the cancelled stop reason.
  A missing/contradictory acknowledgement is failure/unknown, never successful
  task cancellation. The connection becomes unusable on protocol failure.
- end_turn means prompt completion only, not a passed test, finished task or
  permission to advance a workflow. Token-limit/refusal/cancel reasons survive.

The caller owns stream/process lifetime, policy and OS isolation. Disabled client
capabilities do not sandbox the agent's own filesystem/tools/network. Do not hand
this adapter an unrestricted real agent until its launch isolation, account/model,
budgets and approval rules have been explicitly configured. It is sequential,
single-owner, single-turn; it does not manage shared concurrent sessions.

## Next acceptance gate

Select an installed agent/gateway and private credential reference, then implement
durable session-to-AgentRun mapping, authorized permission UI decisions, trace
persistence, retry/restart reconciliation and real prompt/cancel acceptance. Reuse
the existing outbox/callback/independent-approval gates; never synthesize successful
callbacks or enable ORCH-DELIVERY to bypass missing execution. Stream-fixture tests
prove protocol behavior only, not live model execution. No global client settings,
agent processes, model calls or database migrations are introduced by this stage.
