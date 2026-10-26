from pathlib import Path
import tempfile
import unittest

from tools.release_version import android_version_code, read_release_version


class ReleaseVersionTests(unittest.TestCase):
    def test_reads_canonical_semantic_version(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "Directory.Build.props"
            props.write_text(
                "<Project><PropertyGroup><ReleaseVersion>1.2.3</ReleaseVersion></PropertyGroup></Project>",
                encoding="utf-8",
            )

            self.assertEqual("1.2.3", read_release_version(props))

    def test_rejects_missing_or_malformed_version(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "Directory.Build.props"
            props.write_text(
                "<Project><PropertyGroup><ReleaseVersion>next</ReleaseVersion></PropertyGroup></Project>",
                encoding="utf-8",
            )

            with self.assertRaises(ValueError):
                read_release_version(props)

    def test_android_version_code_tracks_release_version(self):
        self.assertEqual(1_002_003, android_version_code("1.2.3"))
        with self.assertRaises(ValueError):
            android_version_code("1.1000.0")


if __name__ == "__main__":
    unittest.main()
