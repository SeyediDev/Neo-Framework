"""Check the published and pilot HR contracts without contacting a service."""
import copy
import json
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator, FormatChecker

root = Path(__file__).resolve().parents[4]
published = json.loads((root / "products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/wwwroot/contracts/organization-hr-v1.openapi.json").read_text(encoding="utf-8"))
pilot = yaml.safe_load((root / "docs/fanasa-platform-control-center/pilot/hr-api.openapi.yaml").read_text(encoding="utf-8"))
assert published["openapi"] == pilot["openapi"] == "3.1.0"
assert published["components"]["schemas"] == pilot["components"]["schemas"], "Published/pilot schema drift"
prefix = "/api/organization-hr/v1"
assert {prefix + path: value for path, value in published["paths"].items()} == pilot["paths"], "Public/backend route contract drift"
assert len(pilot["paths"]) == 3
assert len(published["components"]["schemas"]["OrgCommand"]["properties"]["operation"]["enum"]) == 7
assert any(server["url"].endswith(prefix) for server in published["servers"])
assert published["servers"][0]["url"] == "https://api.fanasa.net.local/development/organization-hr/v1"

refs = 0
def check_refs(value):
    global refs
    if isinstance(value, dict):
        for name, child in value.items():
            if name == "$ref":
                assert child.startswith("#/")
                target = published
                for part in child[2:].split("/"):
                    target = target[part.replace("~1", "/").replace("~0", "~")]
                refs += 1
            else:
                check_refs(child)
    elif isinstance(value, list):
        for child in value:
            check_refs(child)
check_refs(published)
for schema in published["components"]["schemas"].values():
    Draft202012Validator.check_schema(schema)

validator = Draft202012Validator(
    {"$ref": "#/components/schemas/OrgCommand", "components": published["components"]},
    format_checker=FormatChecker(),
)
command = {
    "expectedRevision": 0, "operation": "unit.save", "reason": "contract validation",
    "requestId": "11111111-1111-1111-1111-111111111111", "capacity": 0,
    "from": "2026-10-13T10:00:00+03:30",
}
validator.validate(command)
negative = [
    {"capacity": "1"}, {"expectedRevision": "0"}, {"expectedRevision": -1},
    {"requestId": "00000000-0000-0000-0000-000000000000"},
    {"requestId": "A1111111-1111-1111-1111-111111111111"},
    {"from": "2026-10-13T10:00:00"}, {"from": "2026-10-13t10:00:00z"},
    {"from": "2026-10-13T10:00:00.12345678Z"},
    {"operation": "appointment.transfer"}, {"unknown": True},
]
for values in negative:
    invalid = copy.deepcopy(command)
    invalid.update(values)
    assert not validator.is_valid(invalid), f"Invalid schema example accepted: {list(values)}"
print(f"PASS: pilot YAML and published JSON parse; route/schema agreement; {refs} local refs; Draft 2020-12 schemas; valid offset + {len(negative)} invalid command examples.")
print("LIMIT: these are contract checks, not full OpenAPI validation, deployment, or business authorization.")
