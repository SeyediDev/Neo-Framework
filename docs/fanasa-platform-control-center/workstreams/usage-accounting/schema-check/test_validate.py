"""Malformed-message and malformed-definition regression checks for v1."""

from copy import deepcopy
import unittest

from validate import (
    CheckError, DEFAULT_SCHEMA, DIALECT, EventValidator, PACKAGE,
    check_fixtures, check_profile_schema, load_json, load_json_text,
)


class UsageEventTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = load_json(DEFAULT_SCHEMA)
        cls.event = load_json(PACKAGE / "fixtures" / "usage-event-v1.json")
        cls.validator = EventValidator(cls.schema)

    def rejected(self, path, value):
        with self.subTest(path=path, value=value):
            event = deepcopy(self.event)
            target = event
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            with self.assertRaises(CheckError):
                self.validator.validate(event)

    def test_exact_standalone_and_reconciliation_events(self):
        count, _ = check_fixtures()
        self.assertEqual(count, 3)

    def test_root_and_nested_types(self):
        for value in (None, [], "event"):
            with self.subTest(value=value), self.assertRaises(CheckError):
                self.validator.validate(value)
        self.rejected(("producer",), [])
        self.rejected(("productKey",), 12)

    def test_all_required_properties_at_every_object_level(self):
        def visit(schema, event, path=()):
            for key in schema.get("required", []):
                candidate = deepcopy(self.event)
                target = candidate
                for segment in path:
                    target = target[segment]
                del target[key]
                with self.subTest(missing=path + (key,)), self.assertRaises(CheckError):
                    self.validator.validate(candidate)
            for key, child in schema.get("properties", {}).items():
                if key in event and isinstance(event[key], dict):
                    visit(child, event[key], path + (key,))
        visit(self.schema, self.event)

    def test_all_closed_objects_reject_extra_property(self):
        for path in ((), ("producer",), ("operation",), ("authorization",), ("meter",), ("rateSnapshot",), ("evidence",)):
            self.rejected(path + ("unexpected",), "ignored data must not pass")

    def test_versions_require_non_boolean_integer_within_range(self):
        for path in (("contractVersion",), ("rateSnapshot", "rateVersion")):
            for value in (True, "7", 7.1, 0, -1, 2147483648):
                self.rejected(path, value)

    def test_schema_and_meter_versions_are_exact(self):
        self.rejected(("schemaVersion",), "2.0")
        self.rejected(("schemaVersion",), 1.0)
        self.rejected(("meter", "version"), "2")
        self.rejected(("meter", "version"), 1)

    def test_uuid_fields_require_hyphenated_uuid(self):
        for path in (("eventId",), ("ownerTenantId",), ("consumerTenantId",), ("operation", "requestId"), ("correlationId",)):
            for value in ("not-a-uuid", "00000000000040008000000000000001", "00000000-0000-4000-8000-00000000000g"):
                self.rejected(path, value)

    def test_all_timestamps_require_valid_utc_calendar_timestamp(self):
        for path in (("occurredAt",), ("producedAt",), ("operation", "acceptedAt"), ("rateSnapshot", "effectiveFrom"), ("rateSnapshot", "effectiveTo")):
            for value in ("2026-10-10", "2026-10-10T06:31:00+00:00", "2026-10-10T06:31:00z", "2026-02-30T06:31:00Z", "2026-10-10T25:31:00Z", "fakeZ"):
                self.rejected(path, value)

    def test_exact_enum_and_boundary_values(self):
        for path, value in [(("environment",), "sandbox"), (("rateSnapshot", "currency"), "irr"), (("rateSnapshot", "billingMode"), "payg"), (("rateSnapshot", "rounding"), "bankers"), (("producer", "kind"), "gateway-log"), (("outcome",), "unknown"), (("operation", "billableBoundary"), "gateway-admitted/v1")]:
            self.rejected(path, value)

    def test_decimal_string_grammar_and_bounds(self):
        for value in (12, -12, "-1", "+1", "01", "1e3", "NaN", ".5", "1.", "1.1234567", "1000000000000000000", " 12"):
            self.rejected(("rateSnapshot", "unitPrice"), value)

    def test_pilot_quantity_is_exactly_one_string(self):
        for value in (0, 1, True, "0", "2", "1.0", "-1", "NaN"):
            self.rejected(("meter", "quantity"), value)

    def test_string_length_limits(self):
        self.rejected(("productKey",), "")
        self.rejected(("producer", "deploymentId"), "x" * 201)
        self.rejected(("evidence", "reference"), "x" * 1001)

    def test_hash_pattern_lengths_and_case(self):
        self.rejected(("operation", "requestFingerprint"), "a" * 63)
        self.rejected(("evidence", "resultDigest"), "A" * 64)
        self.rejected(("traceId",), "c" * 33)

    def test_scale_rejects_bool_fraction_and_outside_range(self):
        for value in (True, 0.5, -1, 3, "0"):
            self.rejected(("rateSnapshot", "minorUnitScale"), value)

    def test_allowed_decimal_uuid_and_fractional_timestamp(self):
        event = deepcopy(self.event)
        event["rateSnapshot"]["unitPrice"] = "0.123456"
        event["eventId"] = "ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF"
        event["producedAt"] = "2026-10-10T06:31:01.123Z"
        self.validator.validate(event)

    def test_errors_identify_the_invalid_property(self):
        event = deepcopy(self.event)
        event["rateSnapshot"]["rateVersion"] = False
        with self.assertRaisesRegex(CheckError, "/rateSnapshot/rateVersion"):
            self.validator.validate(event)


class StrictJsonTests(unittest.TestCase):
    def test_duplicate_root_key_is_rejected_before_validation(self):
        with self.assertRaisesRegex(CheckError, "duplicate JSON object key"):
            load_json_text('{"schemaVersion":"1.0","schemaVersion":"2.0"}')

    def test_duplicate_nested_key_is_rejected(self):
        with self.assertRaisesRegex(CheckError, "duplicate JSON object key"):
            load_json_text('{"rateSnapshot":{"unitPrice":"12","unitPrice":"18"}}')

    def test_non_json_numbers_are_rejected(self):
        for value in ("NaN", "Infinity", "-Infinity"):
            with self.subTest(value=value), self.assertRaises(CheckError):
                load_json_text('{"quantity":' + value + '}')

    def test_invalid_json_is_rejected(self):
        with self.assertRaises(CheckError):
            load_json_text('{"unfinished":')


class SchemaDefinitionTests(unittest.TestCase):
    def test_shipped_definition_is_supported(self):
        check_profile_schema(load_json(DEFAULT_SCHEMA))

    def test_unsupported_keyword_is_rejected_at_any_depth(self):
        for keyword in ("$ref", "allOf", "if", "items", "multipleOf", "x-misspelled"):
            schema = {"type": "object", "properties": {"value": {keyword: {}}}}
            with self.subTest(keyword=keyword), self.assertRaisesRegex(CheckError, "unsupported schema keywords"):
                check_profile_schema(schema)

    def test_invalid_keyword_values_are_rejected(self):
        for schema in ({"type": "decimal"}, {"type": ["string", "null"]}, {"required": "key"}, {"required": ["key", "key"]}, {"required": [1]}, {"properties": []}, {"properties": {"key": False}}, {"additionalProperties": 0}, {"minLength": True}, {"maxLength": -1}, {"minimum": "1"}, {"maximum": True}, {"pattern": "["}, {"pattern": 12}, {"format": "email"}, {"enum": []}, {"enum": [1, 1.0]}, {"title": 12}):
            with self.subTest(schema=schema), self.assertRaises(CheckError):
                check_profile_schema(schema)

    def test_dialect_required_and_unknown_dialect_rejected(self):
        for schema in ({"type": "object"}, {"$schema": "http://json-schema.org/draft-07/schema#", "type": "object"}):
            with self.subTest(schema=schema), self.assertRaises(CheckError):
                EventValidator(schema)

    def test_json_boolean_is_not_equal_to_numeric_const_or_enum(self):
        for schema in ({"$schema": DIALECT, "const": 1}, {"$schema": DIALECT, "enum": [1]}):
            validator = EventValidator(schema)
            validator.validate(1.0)
            with self.assertRaises(CheckError):
                validator.validate(True)

    def test_duplicate_keys_in_schema_definition_rejected(self):
        with self.assertRaises(CheckError):
            load_json_text('{"type":"object","type":"string"}')


if __name__ == "__main__":
    unittest.main()
