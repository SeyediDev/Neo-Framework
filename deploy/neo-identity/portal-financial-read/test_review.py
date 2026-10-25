import copy
import json
import unittest
from pathlib import Path
from review import validate

HERE = Path(__file__).resolve().parent


class ProposalTests(unittest.TestCase):
    def setUp(self):
        self.scope = json.loads((HERE / "client-scope.json").read_text(encoding="utf-8"))
        self.plan = json.loads((HERE / "review-plan.json").read_text(encoding="utf-8"))

    def test_minimal_proposal(self):
        self.assertEqual([], validate(self.scope, self.plan))

    def test_rejects_broadened_or_enabled_plan(self):
        mutations = [
            lambda p: p.update(runtimeChangesAuthorized=True), lambda p: p.update(createClient=True),
            lambda p: p.update(createTenant=True), lambda p: p.update(changeGrants=True),
            lambda p: p.update(changeTrust=True), lambda p: p["scopeAssignment"].update(type="default"),
            lambda p: p["scopeAssignment"].update(realmDefault=True),
            lambda p: p["scopeAssignment"]["assignedOnlyTo"].append("other-client"),
            lambda p: p["preserveClient"].update(serviceAccountsEnabled=True),
            lambda p: p["preserveClient"].update(publicClient=False),
            lambda p: p["portalEnvironmentProposal"].update(ConsumerBilling__Enabled="true"),
            lambda p: p["pccEnvironmentProposal"].update(ConsumerUsageReport__Enabled="true"),
            lambda p: p["accessTokenContract"].update(azp="service-client"),
            lambda p: p["accessTokenContract"].update(audContains="fanasa-api"),
            lambda p: p["accessTokenContract"].update(iss="https://other.example/realms/fanasa"),
            lambda p: p["portalEnvironmentProposal"].update(ConsumerBilling__Audience="fanasa-api"),
            lambda p: p["portalEnvironmentProposal"].update(ConsumerBilling__GatewayOrigin="https://platform.fanasa.net.local/"),
            lambda p: p["pccEnvironmentProposal"].update(ConsumerUsageReport__PortalClientId="service-client"),
            lambda p: p["centralRegistrationProposal"].update(actualProductId="invented-id"),
            lambda p: p["centralRegistrationProposal"].update(selectedConsumerTenantId="inferred-tenant"),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                plan = copy.deepcopy(self.plan)
                mutate(plan)
                self.assertTrue(validate(self.scope, plan))

    def test_rejects_identity_mapper(self):
        for mapper in ["oidc-hardcoded-claim-mapper", "oidc-usermodel-attribute-mapper"]:
            with self.subTest(mapper=mapper):
                scope = copy.deepcopy(self.scope)
                scope["protocolMappers"].append({"protocolMapper": mapper})
                self.assertTrue(validate(scope, self.plan))

    def test_rejects_id_token_or_wrong_audience(self):
        for key, value in [("id.token.claim", "true"), ("included.custom.audience", "fanasa-api"),
                           ("included.client.audience", "second-api")]:
            with self.subTest(key=key):
                scope = copy.deepcopy(self.scope)
                scope["protocolMappers"][0]["config"][key] = value
                self.assertTrue(validate(scope, self.plan))

    def test_rejects_nonemitted_or_different_scope(self):
        for key, value in [("name", "billing.write"), ("protocol", "saml")]:
            with self.subTest(key=key):
                scope = copy.deepcopy(self.scope)
                scope[key] = value
                self.assertTrue(validate(scope, self.plan))
        self.scope["attributes"]["include.in.token.scope"] = "false"
        self.assertTrue(validate(self.scope, self.plan))


if __name__ == "__main__":
    unittest.main()
