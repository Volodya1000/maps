"""Public APK publication is a workflow contract, distinct from internal inputs."""

import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


def apk_uploads(job):
    return [
        step
        for step in job["steps"]
        if step.get("uses", "").startswith("actions/upload-artifact@")
        and step.get("with", {}).get("archive") is False
        and ".apk" in step["with"].get("path", "")
    ]


class AndroidDistributionTests(unittest.TestCase):
    def test_country_apk_checks_use_project_python_before_running_scripts(self):
        for workflow in ("pull-request-apks.yml", "offline-packages.yml"):
            with self.subTest(workflow=workflow):
                configuration = yaml.safe_load(
                    (ROOT / ".github/workflows" / workflow).read_text()
                )
                steps = configuration["jobs"]["android"]["steps"]
                python_steps = [
                    index
                    for index, step in enumerate(steps)
                    if step.get("uses", "").startswith("actions/setup-python@")
                    and step.get("with", {}).get("python-version") == "3.12"
                ]
                self.assertEqual(1, len(python_steps))
                script_steps = [
                    index
                    for index, step in enumerate(steps)
                    if "python3 " in step.get("run", "")
                ]
                self.assertTrue(script_steps)
                self.assertLess(python_steps[0], min(script_steps))

    def test_pr_publishes_minsk_once_and_belarus_once(self):
        parent = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        country = yaml.safe_load(
            (ROOT / ".github/workflows/pull-request-apks.yml").read_text()
        )
        early = apk_uploads(parent["jobs"]["android"])
        late = apk_uploads(country["jobs"]["android"])
        self.assertEqual(1, len(early))
        self.assertEqual(1, len(late))
        self.assertIn("-Minsk.apk", early[0]["with"]["path"])
        self.assertIn("-Belarus.apk", late[0]["with"]["path"])
        self.assertNotIn("geodata", parent["jobs"]["android"]["needs"])
        self.assertEqual("always()", early[0]["if"])

    def test_country_preparation_overlaps_android_but_consumers_require_all_gates(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        jobs = workflow["jobs"]
        preparation = jobs["geodata"]
        self.assertNotIn("android", preparation["needs"])
        self.assertTrue(
            {"quality", "tests", "data", "terrain-spike"}.issubset(preparation["needs"])
        )
        consumers = jobs["pull-request-apks"]
        self.assertTrue({"android", "geodata"}.issubset(consumers["needs"]))
        self.assertNotIn("always()", consumers["if"])
        self.assertIn("needs.quality.outputs.full_maps == 'true'", preparation["if"])
        self.assertIn("github.event_name == 'pull_request'", preparation["if"])
        country = yaml.safe_load(
            (ROOT / ".github/workflows/pull-request-apks.yml").read_text()
        )
        for name in ("android", "routing-benchmark"):
            self.assertIn("verify-source", country["jobs"][name]["needs"])
        self.assertIn(
            "ci:routing-benchmark", country["jobs"]["routing-benchmark"]["if"]
        )

    def test_full_apk_exports_version_before_publishing(self):
        workflow = yaml.safe_load(
            (ROOT / ".github/workflows/pull-request-apks.yml").read_text()
        )
        steps = workflow["jobs"]["android"]["steps"]
        version_steps = [
            i
            for i, step in enumerate(steps)
            if "release_version.py" in step.get("run", "")
            and "GITHUB_ENV" in step["run"]
        ]
        self.assertEqual(1, len(version_steps))
        publish = next(
            i for i, step in enumerate(steps) if "dotnet publish" in step.get("run", "")
        )
        self.assertLess(version_steps[0], publish)

    def test_startup_probe_can_run_without_repeating_compression_matrix(self):
        parent = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        country = yaml.safe_load(
            (ROOT / ".github/workflows/pull-request-apks.yml").read_text()
        )
        events = country.get("on", country.get(True))
        option = events["workflow_call"]["inputs"]["startup_benchmark"]
        self.assertEqual("boolean", option["type"])
        self.assertIs(False, option["default"])
        selection = parent["jobs"]["pull-request-apks"]["with"]["startup_benchmark"]
        self.assertIn("ci:apk-startup-benchmark", selection)
        self.assertIn("ci:performance-benchmark", selection)
        self.assertNotIn(
            "ci:apk-startup-benchmark", parent["jobs"]["ci-performance"]["if"]
        )
        self.assertIn(
            "inputs.startup_benchmark",
            country["jobs"]["android"]["env"]["ANDROID_FULL_STARTUP_BENCHMARK"],
        )
