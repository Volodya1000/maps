import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.check_apk_elevation import verify_apk


class ApkElevationTests(unittest.TestCase):
    def test_actual_archive_bytes_and_complete_coverage_are_required(self):
        for scenario in ("complete", "missing", "corrupt", "gap"):
            with (
                self.subTest(scenario=scenario),
                tempfile.TemporaryDirectory() as directory,
            ):
                path = Path(directory) / "app.apk"
                tiles = [
                    {"directory": "west", "bounds": [0, 0, 0.5, 1]},
                    {"directory": "east", "bounds": [0.5, 0, 1, 1]},
                ]
                if scenario == "gap":
                    tiles[1]["bounds"][0] = 0.6
                with zipfile.ZipFile(path, "w") as archive:
                    prefix = "assets/Data/elevation/"
                    archive.writestr(
                        prefix + "index.json",
                        json.dumps({"schema_version": 1, "tiles": tiles}),
                    )
                    for tile in tiles:
                        database = prefix + tile["directory"] + "/elevation.sqlite"
                        data = tile["directory"].encode()
                        if scenario != "missing" or tile["directory"] != "east":
                            archive.writestr(
                                database,
                                data + (b"changed" if scenario == "corrupt" else b""),
                            )
                        archive.writestr(
                            database + ".sha256", hashlib.sha256(data).hexdigest()
                        )
                if scenario == "complete":
                    self.assertEqual(2, verify_apk(path, [0, 0, 1, 1]))
                else:
                    with self.assertRaises(
                        KeyError if scenario == "missing" else ValueError
                    ):
                        verify_apk(path, [0, 0, 1, 1])
