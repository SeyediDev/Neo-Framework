#!/usr/bin/env python3
"""Offline source/proposal checks. No network, runtime changes or token input."""
import argparse
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent


def validate(scope, plan):
    errors = []
    def check(condition, label):
        if not condition:
            errors.append(label)
    check(scope.get("name") == "consumer.financial.read", "dedicated read scope")
    check(scope.get("protocol") == "openid-connect", "OIDC scope")
    check(scope.get("attributes", {}).get("include.in.token.scope") == "true", "scope emitted")
    mappers = scope.get("protocolMappers", [])
    check(len(mappers) == 1, "one mapper only; no identity or grant mapper")
    if len(mappers) == 1:
        mapper = mappers[0]
        check(mapper.get("protocolMapper") == "oidc-audience-mapper", "supported audience mapper")
        check(mapper.get("protocol") == "openid-connect", "OIDC mapper")
        check(mapper.get("config") == {
            "included.custom.audience": "fanasa-access-management-web",
            "access.token.claim": "true", "id.token.claim": "false", "introspection.token.claim": "false"
        }, "access-only exact API audience")
    check(plan.get("status") == "proposal-not-applied", "not operational")
    for key in ("runtimeChangesAuthorized", "createClient", "createTenant", "changeGrants", "changeTrust"):
        check(plan.get(key) is False, key + " disabled")
    check(plan.get("existingClientId") == "fanasa-unified-portal-web", "existing client")
    check(plan.get("realm") == "fanasa", "existing realm")
    check(plan.get("scopeAssignment") == {
        "name": scope.get("name"), "type": "optional", "realmDefault": False,
        "assignedOnlyTo": ["fanasa-unified-portal-web"]
    }, "single optional assignment")
    check(plan.get("preserveClient") == {
        "enabled": True, "publicClient": True, "standardFlowEnabled": True, "serviceAccountsEnabled": False
    }, "preserve observed mode")
    for section in ("portalEnvironmentProposal", "pccEnvironmentProposal"):
        settings = plan.get(section, {})
        check(next((v for k, v in settings.items() if k.endswith("__Enabled")), None) == "false", section + " disabled")
        check(next((v for k, v in settings.items() if k.endswith("__Scope")), None) == scope.get("name"), section + " scope aligned")
    contract = plan.get("accessTokenContract", {})
    check(contract.get("azp") == plan.get("existingClientId"), "azp aligned")
    check(contract.get("scopeContains") == scope.get("name"), "scope aligned")
    check(contract.get("audContains") == "fanasa-access-management-web", "audience aligned")
    check(contract.get("iss") == "https://sso.fanasa.net.local/realms/fanasa", "existing issuer")
    portal = plan.get("portalEnvironmentProposal", {})
    check(portal.get("ConsumerBilling__Audience") == contract.get("audContains"), "Portal API audience")
    check(portal.get("ConsumerBilling__GatewayOrigin") == "https://api.fanasa.net.local/", "approved Gateway origin")
    check(plan.get("pccEnvironmentProposal", {}).get("ConsumerUsageReport__PortalClientId") == plan.get("existingClientId"), "PCC Portal client")
    registration = plan.get("centralRegistrationProposal", {})
    check(registration.get("state") == "proposal-awaiting-approved-binding", "registration remains proposed")
    for key in ("ownerTenantId", "primaryCapabilityCenter", "actualProductId", "selectedConsumerTenantId"):
        check(key in registration and registration[key] is None, key + " unresolved; do not invent binding")
    return errors


def source_review(root):
    paths = {
        "portal": "products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/Program.cs",
        "vault": "products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/ConsumerBillingSession.cs",
        "bearer": "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Program.cs",
        "boundary": "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/ConsumerBillingAuthorization.cs",
        "form": "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Pages/ProductRegistration.cshtml.cs",
        "clientApi": "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccessApi.cs"
    }
    code = {key: (root / path).read_text(encoding="utf-8-sig") for key, path in paths.items()}
    findings = []
    if 'options.Scope.Add("profile")' in code["portal"] and 'options.Scope.Add(consumerBilling.Scope)' not in code["portal"]:
        findings.append("REVIEW_REQUIRED: no reviewed configured-financial-scope request in Portal Program.cs; inspect before activation.")
    if 'RegisterClient(' not in code["form"]:
        findings.append("GAP: ProductRegistration creates product only, not client metadata or Keycloak scopes.")
    if '[HttpPost("catalog/products/{productId:guid}/clients")]' in code["clientApi"]:
        findings.append("SECURITY_REVIEW: client metadata POST lacks local PlatformAdmin/antiforgery annotations; check global filters and approved admin route before use.")
    findings.append("UNVERIFIED: effective Keycloak mappers, deployed revision, approved tenant/product binding and human OIDC acceptance.")
    boundary = code["boundary"]
    return {
        "mode": "offline-source-review", "runtimeMutations": False, "humanTokenInspected": False,
        "sourceSha256": {paths[key]: hashlib.sha256((root / paths[key]).read_bytes()).hexdigest() for key in paths},
        "sourceChecks": {
            "apiAudience": 'options.Audience = "fanasa-access-management-web"' in code["bearer"],
            "noTokensInPortalCookie": 'options.SaveTokens = false' in code["portal"],
            "portalPkce": 'options.UsePkce = true' in code["portal"],
            "vaultFiveMinuteCap": 'now.AddMinutes(5)' in code["vault"],
            "tenantBillingGrant": 'x.Permission == "billing.read"' in boundary,
            "activeClientScope": 'clients[0].AllowedScopes?.Contains(scope' in boundary,
            "humanClaims": all(('Single("' + name + '")') in boundary for name in ("sub", "sid", "azp"))
        }, "findings": findings
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=HERE.parents[2])
    args = parser.parse_args()
    scope = json.loads((HERE / "client-scope.json").read_text(encoding="utf-8"))
    plan = json.loads((HERE / "review-plan.json").read_text(encoding="utf-8"))
    result = source_review(args.repo_root)
    result["proposalErrors"] = validate(scope, plan)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(1 if result["proposalErrors"] or not all(result["sourceChecks"].values()) else 0)


if __name__ == "__main__":
    main()
