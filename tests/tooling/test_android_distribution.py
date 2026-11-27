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
