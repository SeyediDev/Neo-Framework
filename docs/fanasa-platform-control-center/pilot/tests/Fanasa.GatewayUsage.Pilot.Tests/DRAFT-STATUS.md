# Shared Lua/PCC/Usage test draft

The latest direct user instruction in the Neo chat prioritizes UI/UX across chats until further notice. This isolated test draft was stopped before fixture generation, build or execution. Its planned twelve cases have no run result and no acceptance claim.

The project reuses a hash-pinned extraction of the owner Gateway test fixture. The bridge is intended to execute packaged `adapters/pep.lua` and `adapters/context.lua` against a captured authenticated PCC Decision, a deterministic admission double and the existing real HTTPS/mTLS receiver and Fabric stores. It does not execute native APISIX/OIDC/Redis and does not establish commercial provenance, quota deduplication or external outbox delivery.

Source drafts and the temporary Lua dependency lock are retained for a future authorized continuation. The installed test-only `.tools` directory is disposable and has been removed after the priority change. No backend runtime source was changed for this draft.
