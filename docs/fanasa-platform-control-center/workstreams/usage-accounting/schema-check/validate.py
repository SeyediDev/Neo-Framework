"""Strict offline structural checks for the shipped UsageEvent schema profile.

This is a limited keyword profile, NOT a general JSON Schema validator. Unknown
keywords, unsupported forms and malformed definitions fail before validation.
No network resolution or package installation is performed.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
from pathlib import Path
import re
import sys
import uuid
from typing import Any

DIALECT = "https://json-schema.org/draft/2020-12/schema"
PACKAGE = Path(__file__).resolve().parent.parent
DEFAULT_SCHEMA = PACKAGE / "schemas" / "usage-event-v1.schema.json"
KEYWORDS = {
    "$schema", "$id", "title", "description", "type", "additionalProperties",
    "required", "properties", "const", "format", "pattern", "minLength",
    "maxLength", "enum", "minimum", "maximum",
}
TYPES = {"object", "string", "integer"}
UUID_PATTERN = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
DATETIME_PATTERN = re.compile(
    r"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}"
    r"(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})$"
)


class CheckError(ValueError):
    """Invalid schema definition, JSON encoding, or event structure."""


def _pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise CheckError(f"duplicate JSON object key: {key!r}")
        result[key] = value
    return result


def _constant(value: str) -> None:
    raise CheckError(f"non-JSON numeric constant: {value}")


def load_json_text(source: str) -> Any:
    try:
        return json.loads(source, object_pairs_hook=_pairs, parse_constant=_constant)
    except json.JSONDecodeError as error:
        raise CheckError(f"invalid JSON: {error}") from error


def load_json(path: Path) -> Any:
    return load_json_text(path.read_text(encoding="utf-8"))


def _number(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and (
        not isinstance(value, float) or math.isfinite(value)
    )


def _equal(left: Any, right: Any) -> bool:
    # JSON equality separates bool from numbers, unlike Python's True == 1.
    if isinstance(left, bool) or isinstance(right, bool):
        return type(left) is type(right) and left == right
    if _number(left) and _number(right):
        return left == right
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(_equal(left[key], right[key]) for key in left)
    if isinstance(left, list):
        return len(left) == len(right) and all(_equal(a, b) for a, b in zip(left, right))
    return left == right


def _pointer(path: str, key: Any) -> str:
    escaped = str(key).replace("~", "~0").replace("/", "~1")
    return f"{path}/{escaped}"


def _fail(path: str, reason: str) -> None:
    raise CheckError(f"{path or '/'}: {reason}")


def _format(value: str, kind: str) -> bool:
    if kind == "uuid":
        if not UUID_PATTERN.fullmatch(value):
            return False
        try:
            uuid.UUID(value)
            return True
        except ValueError:
            return False
    if not DATETIME_PATTERN.fullmatch(value):
        return False
    # The profile supports ordinary Gregorian timestamps, not leap seconds.
    try:
        timestamp = dt.datetime.fromisoformat(value.replace("t", "T").replace("z", "+00:00").replace("Z", "+00:00"))
        return timestamp.tzinfo is not None
    except ValueError:
        return False


def check_profile_schema(schema: Any, path: str = "") -> None:
    """Validate the supported schema-definition profile, recursively."""
    if not isinstance(schema, dict):
        _fail(path, "limited profile requires an object schema")
    unsupported = set(schema) - KEYWORDS
    if unsupported:
        _fail(path, f"unsupported schema keywords in limited profile: {sorted(unsupported)}")
    if "$schema" in schema and schema["$schema"] != DIALECT:
        _fail(path, "unsupported schema dialect")
    for key in ("$id", "title", "description"):
        if key in schema and not isinstance(schema[key], str):
            _fail(_pointer(path, key), "schema annotation must be a string")
    if "type" in schema and (not isinstance(schema["type"], str) or schema["type"] not in TYPES):
        _fail(_pointer(path, "type"), "unsupported schema type (profile accepts object/string/integer)")
    if "additionalProperties" in schema and not isinstance(schema["additionalProperties"], bool):
        _fail(_pointer(path, "additionalProperties"), "profile accepts only Boolean additionalProperties")
    if "required" in schema:
        required = schema["required"]
        if not isinstance(required, list) or any(not isinstance(key, str) for key in required) or len(set(required)) != len(required):
            _fail(_pointer(path, "required"), "required must be an array of distinct strings")
    if "properties" in schema:
        if not isinstance(schema["properties"], dict):
            _fail(_pointer(path, "properties"), "properties must be an object")
        for key, child in schema["properties"].items():
            check_profile_schema(child, _pointer(_pointer(path, "properties"), key))
    for key in ("minLength", "maxLength"):
        if key in schema and (type(schema[key]) is not int or schema[key] < 0):
            _fail(_pointer(path, key), "length limit must be a nonnegative integer")
    for key in ("minimum", "maximum"):
        if key in schema and not _number(schema[key]):
            _fail(_pointer(path, key), "numeric bound must be finite and numeric")
    if "pattern" in schema:
        if not isinstance(schema["pattern"], str):
            _fail(_pointer(path, "pattern"), "pattern must be a string")
        try:
            re.compile(schema["pattern"])
        except re.error as error:
            _fail(_pointer(path, "pattern"), f"invalid regular expression: {error}")
    if "format" in schema and schema["format"] not in ("uuid", "date-time"):
        _fail(_pointer(path, "format"), "unsupported schema format in limited profile")
    if "enum" in schema:
        values = schema["enum"]
        if not isinstance(values, list) or not values:
            _fail(_pointer(path, "enum"), "enum must be a nonempty array")
        if any(_equal(value, other) for index, value in enumerate(values) for other in values[index + 1:]):
            _fail(_pointer(path, "enum"), "enum members must be distinct")


def _profile_validate(instance: Any, schema: dict[str, Any], path: str = "") -> None:
    checks = {
        "object": lambda item: isinstance(item, dict),
        "string": lambda item: isinstance(item, str),
        "integer": lambda item: _number(item) and (isinstance(item, int) or item.is_integer()),
    }
    if "type" in schema and not checks[schema["type"]](instance):
        _fail(path, f"expected type {schema['type']}")
    if "const" in schema and not _equal(instance, schema["const"]):
        _fail(path, f"does not match const {schema['const']!r}")
    if "enum" in schema and not any(_equal(instance, member) for member in schema["enum"]):
        _fail(path, "not an allowed enum value")
    if isinstance(instance, dict):
        for key in schema.get("required", []):
            if key not in instance:
                _fail(_pointer(path, key), "required property is missing")
        properties = schema.get("properties", {})
        if schema.get("additionalProperties") is False:
            for key in instance:
                if key not in properties:
                    _fail(_pointer(path, key), "additional property is not allowed")
        for key, child in properties.items():
            if key in instance:
                _profile_validate(instance[key], child, _pointer(path, key))
    if isinstance(instance, str):
        if len(instance) < schema.get("minLength", 0):
            _fail(path, "string is too short")
        if "maxLength" in schema and len(instance) > schema["maxLength"]:
            _fail(path, "string is too long")
        if "pattern" in schema and re.search(schema["pattern"], instance) is None:
            _fail(path, "string does not match pattern")
        if "format" in schema and not _format(instance, schema["format"]):
            _fail(path, f"invalid {schema['format']} format")
    if _number(instance):
        if "minimum" in schema and instance < schema["minimum"]:
            _fail(path, "number is below minimum")
        if "maximum" in schema and instance > schema["maximum"]:
            _fail(path, "number exceeds maximum")


class EventValidator:
    def __init__(self, schema: dict[str, Any]):
        if not isinstance(schema, dict) or schema.get("$schema") != DIALECT:
            raise CheckError("root schema must identify Draft 2020-12")
        check_profile_schema(schema)
        self.schema = schema
        self.engine = "limited offline keyword profile (not general JSON Schema)"

    def validate(self, event: Any) -> None:
        _profile_validate(event, self.schema)


def check_fixtures(schema_path: Path = DEFAULT_SCHEMA) -> tuple[int, str]:
    validator = EventValidator(load_json(schema_path))
    validator.validate(load_json(PACKAGE / "fixtures" / "usage-event-v1.json"))
    reconciliation = load_json(PACKAGE / "fixtures" / "reconciliation-v1.json")
    if not isinstance(reconciliation, dict) or not isinstance(reconciliation.get("events"), list) or not reconciliation["events"]:
        raise CheckError("reconciliation fixture requires a nonempty events array")
    for index, event in enumerate(reconciliation["events"]):
        try:
            validator.validate(event)
        except CheckError as error:
            raise CheckError(f"reconciliation events[{index}]: {error}") from error
    return 1 + len(reconciliation["events"]), validator.engine


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--schema", type=Path, default=DEFAULT_SCHEMA)
    parser.add_argument("--event", type=Path, help="Validate one generated UsageEvent instead of the historical fixture set.")
    args = parser.parse_args(argv)
    try:
        if args.event is not None:
            validator = EventValidator(load_json(args.schema))
            validator.validate(load_json(args.event))
            count, engine = 1, validator.engine
            print(f"PASS: schema definition and generated UsageEvent {args.event}")
        else:
            count, engine = check_fixtures(args.schema)
            print(f"PASS: schema definition and {count} UsageEvent instances (standalone + reconciliation.events)")
        print(f"Validator: {engine}")
        print("Scope: structural validation only; reconciliation wrapper, trusted bindings and billability need separate checks.")
        return 0
    except (CheckError, OSError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
