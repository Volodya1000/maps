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
    def test_manual_demo_does_not_require_signing_secrets(self):
        parent = yaml.safe_load(
            (ROOT / ".github/workflows/offline-packages.yml").read_text()
        )
        demo = yaml.safe_load(
            (ROOT / ".github/workflows/demo-packages.yml").read_text()
        )
        events = demo.get("on", demo.get(True))
        self.assertNotIn("secrets", events["workflow_call"])
        self.assertNotIn("secrets", parent["jobs"]["demo"])
        android = demo["jobs"]["android"]
        application_id = android["env"]["ANDROID_APPLICATION_ID"]
        self.assertTrue(application_id.startswith("by.cablerouteplanner.demo."))
        self.assertIn("github.run_id", application_id)
        self.assertIn("github.run_attempt", application_id)
        publish = next(
            step
            for step in android["steps"]
            if "dotnet publish " in step.get("run", "")
        )
        self.assertIn('-p:ApplicationId="$ANDROID_APPLICATION_ID"', publish["run"])
        emulator = next(
            step
            for step in android["steps"]
            if step.get("uses", "").startswith(
                "reactivecircus/android-emulator-runner@"
            )
        )
        self.assertIn('--package "$ANDROID_APPLICATION_ID"', emulator["with"]["script"])

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
