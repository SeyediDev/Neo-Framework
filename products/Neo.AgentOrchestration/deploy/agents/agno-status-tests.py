"""Synthetic collector tests only; never call Docker, HTTP or a model."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

spec = importlib.util.spec_from_file_location('agno_status', Path(__file__).with_name('agno-status.py'))
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class Tests(unittest.TestCase):
    def test_installed_health_cannot_claim_execution_or_leak_configuration(self):
        with patch.object(collector, 'installed', return_value=True), patch.object(collector, 'healthy', return_value=True):
            data = collector.collect()
        self.assertEqual('advisory-only', data['mode'])
        self.assertEqual('advisory-healthy', data['reason'])
        self.assertNotIn('executionReady', data)
        self.assertNotIn(collector.BASE, json.dumps(data))
        self.assertNotIn(str(collector.TOKEN), json.dumps(data))

    def test_missing_installation_never_calls_health(self):
        with patch.object(collector, 'installed', return_value=False), patch.object(collector, 'healthy') as health:
            data = collector.collect()
        health.assert_not_called()
        self.assertEqual('not-installed', data['reason'])
        self.assertIsNone(data['version'])

    def test_network_error_retains_installation_without_exposing_error(self):
        with patch.object(collector, 'installed', return_value=True), patch.object(collector, 'healthy', side_effect=OSError('private-key')):
            data = collector.collect()
        self.assertTrue(data['installed'])
        self.assertFalse(data['transportHealthy'])
        self.assertEqual('advisory-unreachable', data['reason'])
        self.assertNotIn('private-key', json.dumps(data))

    def test_anonymous_success_is_not_healthy(self):
        with patch.object(collector, 'request', return_value=[]), patch.object(collector, 'read_token') as token:
            self.assertFalse(collector.healthy())
        token.assert_not_called()

    def test_private_read_uses_only_fixed_read_endpoints(self):
        denied = urllib.error.HTTPError('http://test', 401, 'denied', {}, None)
        with patch.object(collector, 'request', side_effect=[denied, [{'id': 'task-planner'}, {'id': 'quality-reviewer'}], [{'id': 'task-preparation'}]]) as request, patch.object(collector, 'read_token', return_value='synthetic-only'):
            self.assertTrue(collector.healthy())
            self.assertEqual([('/agents',), ('/agents', 'synthetic-only'), ('/workflows', 'synthetic-only')], [call.args for call in request.call_args_list])

    def test_wrong_version_or_image_cannot_be_installed(self):
        with patch.object(collector, 'command', return_value=json.dumps([{'Image': 'other', 'State': {'Running': True}}])) as command:
            self.assertFalse(collector.installed()); self.assertEqual(1, command.call_count)
        with patch.object(collector, 'command', side_effect=[json.dumps([{'Image': collector.EXPECTED_IMAGE, 'State': {'Running': True}}]), '9.9.9']):
            self.assertFalse(collector.installed())

    @unittest.skipIf(os.name == 'nt', 'Linux atomic report permissions are verified on target VPS')
    def test_atomic_private_report_rejects_links(self):
        with tempfile.TemporaryDirectory(prefix='fanasa-agno-observer-tests-') as temporary:
            path = Path(temporary) / 'status.json'
            collector.write_report({'mode': 'advisory-only'}, path)
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertEqual({'mode': 'advisory-only'}, json.loads(path.read_text()))
            link = Path(temporary) / 'linked.json'; link.symlink_to(path)
            with self.assertRaises(ValueError): collector.write_report({}, link)
            with self.assertRaises(ValueError): collector.write_report({}, Path(temporary) / 'absent' / 'file.json')
            with self.assertRaises(ValueError): collector.write_report({'text': 'x' * 8193}, path)
            self.assertEqual({'mode': 'advisory-only'}, json.loads(path.read_text()))


if __name__ == '__main__':
    unittest.main()
