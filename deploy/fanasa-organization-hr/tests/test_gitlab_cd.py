"""Exercise the trusted GitLab origin/branch/job boundary using synthetic API objects."""
import copy
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("hr_cd", Path(__file__).parents[1] / "gitlab-cd.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
SHA = "a" * 40
PIPELINE = {"id": 7, "project_id": 2, "sha": SHA, "ref": "develop", "status": "success", "source": "push",
            "web_url": "https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api/-/pipelines/7"}
JOB = {"id": 9, "name": "hr_verify_package", "status": "success", "allow_failure": False, "tag": False,
       "ref": "develop", "commit": {"id": SHA}, "pipeline": {"id": 7}, "runner": {"id": 3}}


class Api:
    def __init__(self):
        self.branch = {"name": "develop", "protected": True, "commit": {"id": SHA}}
        self.pipeline = copy.deepcopy(PIPELINE)
        self.job = copy.deepcopy(JOB)

    def json(self, path):
        if path == "repository/branches/develop": return self.branch
        if path.startswith("pipelines?"): return [{"id": 7}]
        if path == "pipelines/7": return self.pipeline
        if path.startswith("pipelines/7/jobs?"): return [self.job]
        raise AssertionError(path)


class CdBoundaryTests(unittest.TestCase):
    def test_exact_successful_protected_job_selected(self):
        self.assertEqual(MODULE.select(Api(), 3), (PIPELINE, JOB))

    def test_unprotected_and_mismatched_job_are_refused(self):
        for field, value in [("allow_failure", True), ("status", "failed"), ("runner", {"id": 4}),
                             ("commit", {"id": "b" * 40}), ("pipeline", {"id": 8}), ("tag", True)]:
            with self.subTest(field=field):
                api = Api(); api.job[field] = value
                with self.assertRaises(RuntimeError): MODULE.select(api, 3)
        api = Api(); api.branch["protected"] = False
        with self.assertRaises(RuntimeError): MODULE.select(api, 3)

    def test_other_source_pipeline_cannot_deploy(self):
        for field, value in [("project_id", 3), ("source", "merge_request_event"),
                             ("sha", "b" * 40), ("web_url", "https://other.example/pipeline/7")]:
            with self.subTest(field=field):
                api = Api(); api.pipeline[field] = value
                self.assertIsNone(MODULE.select(api, 3))

    def test_evidence_must_belong_to_exact_job(self):
        evidence = {"status": "passed", "source": {"repository": MODULE.REPOSITORY,
                    "gitBaseCommit": SHA, "dirty": False, "kind": "git-commit"},
                    "ciOrigin": {"engine": "GitLab CI", "projectId": "2", "pipelineId": "7", "jobId": "9",
                                 "commit": SHA, "ref": "develop", "protected": True}}
        MODULE.validate_evidence(evidence, PIPELINE, JOB)
        self.assertEqual(MODULE.deployed_origin(evidence, SHA), (7, 9),
                         "A later equivalent build must not replace the deployed artifact's original CI identity")
        for field, value in [("jobId", "10"), ("projectId", "3"), ("pipelineId", "8"), ("protected", False)]:
            changed = copy.deepcopy(evidence); changed["ciOrigin"][field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                MODULE.validate_evidence(changed, PIPELINE, JOB)


if __name__ == "__main__": unittest.main()
