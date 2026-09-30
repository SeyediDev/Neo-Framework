# Agentic / vibe-coding tools: review shortlist

Reviewed 2026-09-30 using official documentation. This is a representative shortlist,
not an exhaustive market survey or hands-on benchmark. Capabilities and availability
vary by release/account. No subscriptions, agents or external integrations activated.

| Tool | Documented capability examined | Neo candidate for review |
|---|---|---|
| Codex | Remote-host work, worktrees, review and steering from mobile | Host selection, safe parallel workspaces, mobile approvals |
| Cursor Cloud Agents | Isolated cloud VMs with repository/dependencies and team context | Enrolled runner pools and reproducible environments |
| Claude Code | Session token reporting, organization cost controls and context management | Usage provenance, budget/reservation, coverage and cost attribution |
| Cascade (Windsurf documentation now redirects to Devin Desktop) | Named checkpoints and rollback | Checkpoints linked to commits; explicit data-safe recovery |
| GitHub Copilot cloud agent | Issue/branch/PR workflow, ephemeral Actions environment and review | Task-to-PR binding, CI evidence and reviewer gates |
| Replit Agent | Natural-language application building | Guided intake, environment setup and preview |
| Lovable | Versioned editable plans and approval before Build | Versioned plan approval, traceable changes and preview |

Primary sources (one short paraphrase per source; no pricing claims):
- [Codex remote engineering](https://developers.openai.com/blog/mastering-codex-remote-for-engineering)
- [Cursor Cloud Agents](https://cursor.com/docs/cloud-agent)
- [Claude Code costs](https://code.claude.com/docs/en/costs)
- [Cascade](https://docs.devin.ai/desktop/cascade/cascade)
- [Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
- [Replit Agent](https://docs.replit.com/features/agent/overview)
- [Lovable Plan mode](https://docs.lovable.dev/features/plan-mode)

## Capability checklist for product decisions

P0: scoped task ownership; token/time estimates and observed usage; approvals;
tenant/team isolation; runner enrollment; audit; secret handling; retry/recovery;
repository/worktree isolation; test/commit evidence; protected deployment/rollback.

P1: approved plan versions; task-to-PR status sync; preview environments; mobile
command/approval inbox; reusable project templates; capability-aware model routing;
context handoff; configurable LSP/MCP/ACP adapters; scheduled/event-triggered flows.

P2: visual selection-to-change, multimodal intake, experiment comparison,
checkpoint UX, quality/cost trend dashboards and reusable workflow marketplace.

These are proposals to prioritize, not claims that all are implemented in Neo.
Neo already implements task/time/history, role/workflow configuration, templates
and protocol foundations. Live ACP/provider execution, full bidirectional GitHub
sync, fleet management, SaaS membership and billing remain separate acceptance gates.
Do not recreate an IDE: prioritize an interoperable work/execution control plane.
