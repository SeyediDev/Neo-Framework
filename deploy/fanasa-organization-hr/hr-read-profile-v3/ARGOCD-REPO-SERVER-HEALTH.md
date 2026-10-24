# Argo CD repo-server health repair and source persistence

This is the operational record for the HR development CI/CD dependency. The live repair is complete. Source persistence is a **proposal**, because the checked-in values file has not been proved equal to the current Helm release user values.

## Verified live repair

Only Deployment `argocd/argocd-repo-server` container `repo-server` liveness probe timeout changed from 1 to 5 seconds. Deployment UID is `cdcb2c5e-7d2e-4a45-aff1-b618c8bc0b98`, generation 3; image is `quay.io/argoproj/argocd:v3.5.3`. No Helm upgrade/install was executed. Readiness remains 1 second.

The full liveness endpoint `/healthz?full=true` returned HTTP 200 in 4.013 seconds at `2026-10-14T21:31:34.426691Z`. The replacement Pod `argocd-repo-server-d57f598b5-rtwjd` was Ready with restart count 0 after 217.484 seconds / 21 complete normal probe periods. Its UID was `7b9abece-86ce-4b12-ae13-ed801e657c03`; no Unhealthy, Killing or BackOff events were observed for that Pod. HR was Healthy/Synced/Succeeded and all five other observed Applications were Healthy/Synced. These are point-in-time observations, not a continuous monitor.

Evidence: `argocd-repair-diagnosis.json`, `argocd-repair-observation.json`, and `argocd-repair-final.json`. The original mutation wrapper could not save a local full backup; the prior full spec in the later evidence is explicitly reconstructed by reversing the sole verified delta. Do not describe it as a persisted pre-change backup.

The later read-only `argocd-install-owner.json` confirms generation 3, liveness timeout 5 and readiness timeout 1. It also confirms Helm ownership and the installed release metadata below. It does not recheck the entire HR acceptance boundary.

## Installed release and candidate source

- Helm release: `argocd`, namespace `argocd`, revision `2`, status `deployed`.
- Installed chart: `argo-cd-10.9.6`; application version `v3.5.3`.
- Stored Helm manifest still contains liveness timeout 1. The live Deployment repair is therefore not yet recorded in Helm release values.
- Candidate installation source: `E:/SJVS/Projects/NeoCluster/argocd-values.yaml`.
- Tracked Git blob: `97ed4d2737063c7035431fc508eae400fe866e96`.
- Last file commit: `9c1ee51381b8da2aca2b9a1f63aa03dc84280000`, author/committer `javadsayedi`, 2026-10-09.
- File SHA256: `e171de8a0cb467b68c32a04e5d69c6a317d38575d23f44db76b58a2f31ecf4db`.
- VPS candidate source `/opt/neocluster/argocd-values.yaml` has the exact same SHA256.
- The local file is tracked and clean. Its current `repoServer` section contains only replicas and resource limits/requests.
- `NeoCluster/README.md:160` identifies this file as Argo resource limits and NodePort settings; `install-core.sh` configures the Helm repository but does not contain an Argo release installation command. There is no proved automatic source-reconciliation owner.
- Helm release user values and the candidate source were parsed by Helm and compared in memory. The repoServer subtree is equal. Full equality failed at `/configs/cm/url` and `/global/domain`; their values were not printed. This blocks asserting that the file is the complete authoritative source for a future upgrade. See `argocd-source-comparison.json` for sanitized differing paths.

The installed chart's [pinned values](https://raw.githubusercontent.com/argoproj/argo-helm/argo-cd-10.9.6/charts/argo-cd/values.yaml) define `repoServer.livenessProbe.timeoutSeconds` with default 1 (line 3158). Its [pinned Deployment template](https://raw.githubusercontent.com/argoproj/argo-helm/argo-cd-10.9.6/charts/argo-cd/templates/argocd-repo-server/deployment.yaml) renders that key at line 398. These confirm the supported key and explain the original timeout. A successful whole-chart render comparison was not completed after full source/release equality failed.

## Proposed source change — not applied

After reconciling the full source with the current Helm user values, the owning infrastructure change should add only:

```yaml
repoServer:
  replicas: 1
  livenessProbe:
    timeoutSeconds: 5
  # existing resources remain here
```

Use the exact pinned chart version 10.9.6 for the preflight rendering. Render the repo-server Deployment before and after the proposed value, parse both manifests and require the only semantic difference to be `/spec/template/spec/containers/0/livenessProbe/timeoutSeconds: 1 -> 5`. Review any additional difference instead of applying it. Record before/after SHA256 for both the versioned source and the remote installation file. A source write must check the expected current SHA above and fail if it changed.

No source file was changed, no external repository was committed, and no Helm release update was executed by this permanence investigation. An eventual upgrade must consume the reconciled values file explicitly; an upgrade based only on the stored release user values can restore timeout 1. Do not claim the live fix survives all future upgrades until that owning workflow is resolved.

## Scoped verification and rollback

Do not repeat `repair-argocd-liveness.sh` or its mutation wrapper: the live fix already ran once. For a read-only follow-up, inspect the one repo-server Deployment and Pod, verify the full health endpoint, and compare HR sync/health plus the known HR source/spec/Pod/PVC hashes. Do not sync/prune unrelated Applications or change their source, credentials or tracking.

If a confirmed adverse effect requires rollback, inspect the unexecuted rollback argv captured in `argocd-repair-final.json`. It is scoped to the same Deployment and changes only 5 to 1 after checking deployment UID, container name, image and current timeout. The captured argv has no resourceVersion test: fetch the current Deployment and add that test before an eventual execution. Do not remove the existing compare-and-swap tests. Returning to 1 restores the timeout that caused the observed failure; it is not a routine completion step.

The HR source revision, Application spec, HR image/Pod identities, retained PVC, APISIX include and accepted journals remained unchanged during the repair. The existing OrphanedResourceWarning was not hidden or pruned; the exact single orphan node was not identified by the Application condition.
