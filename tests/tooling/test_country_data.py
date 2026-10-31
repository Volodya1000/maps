"""Country acquisition and CI contracts, using only local synthetic bytes."""

import copy
import gzip
import hashlib
import http.client
import io
import json
import shutil
import subprocess
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import patch

import yaml

from tools.ci_policy import full_maps
from tools.GeodataPreparation import country_data as country


class CountryDataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "package"
        self.raw = b"synthetic PBF fixture\x00\x01"
        self.lock = {
            "source": {
                "version": "fixture-v1",
                "sha256": hashlib.sha256(self.raw).hexdigest(),
                "urls": ["https://primary.test/map.pbf", "https://backup.test/map.pbf"],
            },
            "runtime": {"python": "3.12.14", "uv": "0.12.23"},
        }
        for name in country.INPUTS:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture converter")
        licenses = self.root / country.LICENSES
        licenses.mkdir(parents=True)
        (licenses / "ODbL.txt").write_text("fixture attribution")

    def response(self, data=None):
        stream = io.BytesIO(self.raw if data is None else data)
        stream.url = "https://primary.test/map.pbf"
        return stream

    def fixture_package(self):
        self.output.mkdir()
        database = b"SQLite format 3\x00fixture"
        (self.output / "geodata.sqlite").write_bytes(database)
        (self.output / "geodata.sqlite.gz").write_bytes(
            gzip.compress(database, mtime=0)
        )
        (self.output / "manifest.json").write_text(
            json.dumps(
                {
                    "database_sha256": hashlib.sha256(database).hexdigest(),
                    "sources": [{"sha256": self.lock["source"]["sha256"]}],
                }
            )
        )
        country.seal(self.lock, self.output, self.root)
        files = {
            p.relative_to(self.output).as_posix(): {
                "sha256": country.sha256(p),
                "urls": ["https://primary.test/" + p.name],
            }
            for p in self.output.rglob("*")
            if p.is_file()
        }
        self.lock["prepared"] = {
            "key": country.cache_key(self.lock, self.root),
            "files": files,
        }

    def test_download_publishes_only_verified_bytes(self):
        target = self.root / "source.pbf"
        with patch.object(
            country.urllib.request, "urlopen", return_value=self.response()
        ):
            country.download(self.lock["source"], target)
        self.assertEqual(self.raw, target.read_bytes())
        self.assertEqual([], list(self.root.glob("*.partial")))

    def test_unavailable_primary_uses_identical_backup(self):
        missing = urllib.error.HTTPError(
            "https://primary.test", 404, "missing", {}, None
        )
        with patch.object(
            country.urllib.request, "urlopen", side_effect=[missing, self.response()]
        ):
            country.download(self.lock["source"], self.root / "source.pbf")
        self.assertEqual(self.raw, (self.root / "source.pbf").read_bytes())

    def test_incomplete_http_response_can_retry_verified_bytes(self):
        with patch.object(
            country.urllib.request,
            "urlopen",
            side_effect=[http.client.IncompleteRead(b"part"), self.response()],
        ):
            country.download(self.lock["source"], self.root / "source.pbf")
        self.assertEqual(self.raw, (self.root / "source.pbf").read_bytes())

    def test_non_https_source_is_rejected(self):
        self.lock["source"]["urls"] = ["http://primary.test/map.pbf"]
        with self.assertRaises(ValueError):
            country.download(self.lock["source"], self.root / "source.pbf")
        self.assertFalse((self.root / "source.pbf").exists())

    def test_no_available_source_leaves_no_file(self):
        with patch.object(
            country.urllib.request,
            "urlopen",
            side_effect=urllib.error.URLError("offline"),
        ):
            with self.assertRaises(RuntimeError):
                country.download(self.lock["source"], self.root / "source.pbf")
        self.assertEqual([], list(self.root.glob("*.pbf")))
        self.assertEqual([], list(self.root.glob("*.partial")))

    def test_wrong_checksum_truncated_file_and_corrupt_backup_are_rejected(self):
        for data in [b"wrong", self.raw[:-1], b""]:
            with self.subTest(data=data):
                with patch.object(
                    country.urllib.request,
                    "urlopen",
                    side_effect=lambda *a, data=data, **kw: self.response(data),
                ):
                    with self.assertRaises(RuntimeError):
                        country.download(self.lock["source"], self.root / "source.pbf")
                self.assertFalse((self.root / "source.pbf").exists())
                self.assertEqual([], list(self.root.glob("*.partial")))

    def test_bad_primary_checksum_can_fall_back_to_verified_bytes(self):
        with patch.object(
            country.urllib.request,
            "urlopen",
            side_effect=[self.response(b"wrong"), self.response()],
        ):
            country.download(self.lock["source"], self.root / "source.pbf")
        self.assertEqual(self.raw, (self.root / "source.pbf").read_bytes())

    def test_local_source_hit_needs_no_network(self):
        target = self.root / "source.pbf"
        target.write_bytes(self.raw)
        with patch.object(
            country.urllib.request,
            "urlopen",
            side_effect=AssertionError("unexpected network"),
        ):
            country.download(self.lock["source"], target)
        self.assertEqual(self.raw, target.read_bytes())

    def test_prepared_cache_hit_needs_no_network_or_conversion(self):
        self.fixture_package()
        with (
            patch.object(
                country, "download", side_effect=AssertionError("unexpected network")
            ),
            patch.object(
                country, "prepare", side_effect=AssertionError("unexpected conversion")
            ),
        ):
            country.ensure(self.lock, self.output, self.root)
        self.assertTrue((self.output / "geodata.sqlite.gz").is_file())

    def test_cache_miss_downloads_pinned_package_without_conversion(self):
        self.fixture_package()
        contents = {
            p.relative_to(self.output).as_posix(): p.read_bytes()
            for p in self.output.rglob("*")
            if p.is_file()
        }
        shutil.rmtree(self.output)

        def fetch(spec, destination):
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(
                contents[destination.relative_to(self.output).as_posix()]
            )

        with (
            patch.object(country, "download", side_effect=fetch),
            patch.object(
                country, "prepare", side_effect=AssertionError("unexpected conversion")
            ),
        ):
            country.ensure(self.lock, self.output, self.root)
        country.validate(
            self.lock, self.output, country.cache_key(self.lock, self.root)
        )

    def test_missing_or_corrupt_prepared_cache_fails_closed(self):
        self.fixture_package()
        path = self.output / "geodata.sqlite.gz"
        path.write_bytes(b"corrupt")
        with self.assertRaises(ValueError):
            country.ensure(self.lock, self.output, self.root)
        path.unlink()
        with self.assertRaises(OSError):
            country.ensure(self.lock, self.output, self.root)

    def test_prepared_source_failure_does_not_trigger_full_conversion(self):
        self.fixture_package()
        shutil.rmtree(self.output)
        with (
            patch.object(country, "download", side_effect=RuntimeError("offline")),
            patch.object(
                country, "prepare", side_effect=AssertionError("unexpected conversion")
            ),
        ):
            with self.assertRaises(RuntimeError):
                country.ensure(self.lock, self.output, self.root)
        self.assertFalse(self.output.exists())

    def test_osm_version_converter_dependency_and_runtime_changes_invalidate(self):
        original = country.cache_key(self.lock, self.root)
        for field, value in [("version", "fixture-v2"), ("sha256", "0" * 64)]:
            changed = copy.deepcopy(self.lock)
            changed["source"][field] = value
            self.assertNotEqual(original, country.cache_key(changed, self.root))
        for name in country.INPUTS:
            path = self.root / name
            path.write_text("changed converter input")
            self.assertNotEqual(original, country.cache_key(self.lock, self.root))
            path.write_text("fixture converter")
        changed = copy.deepcopy(self.lock)
        changed["runtime"]["python"] = "3.12.15"
        self.assertNotEqual(original, country.cache_key(changed, self.root))

    def test_unrelated_files_and_mirror_urls_do_not_invalidate(self):
        original = country.cache_key(self.lock, self.root)
        for name in ["README.md", "uv.lock", ".github/workflows/ci.yml", "src/app.cs"]:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("unrelated change")
        self.lock["source"]["urls"].reverse()
        self.assertEqual(original, country.cache_key(self.lock, self.root))

    def test_changed_converter_cannot_reuse_old_prepared_package(self):
        self.fixture_package()
        shutil.rmtree(self.output)
        (self.root / country.INPUTS[0]).write_text("new converter")

        # Deliberate converter change is the only cold path allowed to prepare.
        def converted(lock, output, root):
            output.mkdir()
            (output / "new-conversion").write_text(lock["source"]["version"])

        with patch.object(country, "prepare", side_effect=converted):
            country.ensure(self.lock, self.output, self.root)
        self.assertEqual("fixture-v1", (self.output / "new-conversion").read_text())

    def test_failed_conversion_does_not_publish_partial_package(self):
        def source(spec, destination):
            destination.parent.mkdir(parents=True)
            destination.write_bytes(self.raw)

        def command(args, **kwargs):
            if "--compress" in args:
                raise subprocess.CalledProcessError(1, args)

        with (
            patch.object(country, "download", side_effect=source),
            patch.object(country.subprocess, "check_output", return_value="uv 0.12.23"),
            patch.object(country.subprocess, "run", side_effect=command),
        ):
            with self.assertRaises(subprocess.CalledProcessError):
                country.prepare(self.lock, self.output, self.root)
        self.assertFalse(self.output.exists())
        self.assertEqual([], list(self.root.glob("country-*")))

    def test_modified_cache_receipt_cannot_override_committed_checksum(self):
        self.fixture_package()
        (self.output / "geodata.sqlite.gz").write_bytes(b"tampered")
        receipt_path = self.output / "package.json"
        receipt = json.loads(receipt_path.read_text())
        receipt["files"]["geodata.sqlite.gz"] = country.sha256(
            self.output / "geodata.sqlite.gz"
        )
        receipt_path.write_text(json.dumps(receipt))
        with self.assertRaises(ValueError):
            country.ensure(self.lock, self.output, self.root)

    def test_seal_rejects_invalid_gzip(self):
        self.output.mkdir()
        (self.output / "manifest.json").write_text('{"database_sha256": "invalid"}')
        (self.output / "geodata.sqlite.gz").write_bytes(b"not gzip")
        with self.assertRaises(gzip.BadGzipFile):
            country.seal(self.lock, self.output, self.root)


class CountryWorkflowTests(unittest.TestCase):
    def test_existing_full_map_and_benchmark_label_policy(self):
        for labels, expected in [
            ([], True),
            (["ci:skip-full-maps"], False),
            (["ci:skip-full-maps", "ci:full-maps"], True),
            (["ci:skip-full-maps", "ci:routing-benchmark"], True),
        ]:
            with self.subTest(labels=labels):
                self.assertEqual(expected, full_maps("pull_request", labels))
        self.assertFalse(full_maps("push", ["ci:full-maps"]))

    def test_consumers_share_one_package_and_do_not_depend_on_cache_restore(self):
        for filename in ["pull-request-apks.yml", "offline-packages.yml"]:
            workflow = yaml.safe_load(
                (country.ROOT / ".github/workflows" / filename).read_text()
            )
            steps = workflow["jobs"]["geodata"]["steps"]
            self.assertTrue(
                any(s.get("uses") == "./.github/actions/country-data" for s in steps)
            )
            self.assertTrue(
                any(
                    s.get("uses", "").startswith("actions/upload-artifact@")
                    and "if" not in s
                    for s in steps
                )
            )
            for name in (
                ["android", "routing-benchmark"]
                if filename.startswith("pull-")
                else ["android", "windows"]
            ):
                consumer = workflow["jobs"][name]["steps"]
                self.assertTrue(
                    any(
                        s.get("uses", "").startswith("actions/download-artifact@")
                        for s in consumer
                    )
                )
                self.assertFalse(
                    any(s.get("uses", "").startswith("actions/cache") for s in consumer)
                )

    def test_regular_checks_keep_using_small_committed_fixtures(self):
        workflow = yaml.safe_load(
            (country.ROOT / ".github/workflows/ci.yml").read_text()
        )
        for name in ["quality", "tests", "data", "android", "terrain-spike"]:
            self.assertNotIn("geodata", workflow["jobs"][name].get("needs", []))
        self.assertIn(
            "needs.quality.outputs.full_maps == 'true'",
            workflow["jobs"]["pull-request-apks"]["if"],
        )

    def test_publication_is_an_explicit_manual_mode(self):
        workflow = yaml.safe_load(
            (country.ROOT / ".github/workflows/offline-packages.yml").read_text()
        )
        events = workflow.get("on", workflow.get(True))
        self.assertEqual({"workflow_dispatch"}, set(events))
        mode = events["workflow_dispatch"]["inputs"]["mode"]
        self.assertEqual("installers", mode["default"])
        self.assertEqual(["installers", "geodata"], mode["options"])
        self.assertEqual(
            "inputs.mode == 'geodata'", workflow["jobs"]["publish-geodata"]["if"]
        )
        self.assertEqual("inputs.mode != 'geodata'", workflow["jobs"]["geodata"]["if"])
        self.assertEqual(
            {"contents": "write"}, workflow["jobs"]["publish-geodata"]["permissions"]
        )
        self.assertEqual({"contents": "read"}, workflow["permissions"])
