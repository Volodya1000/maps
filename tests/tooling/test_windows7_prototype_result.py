import json
import tempfile
import unittest
from pathlib import Path

from tests.tooling.test_windows7_result import png_image
from tools.windows7_prototype_result import (
    ResultValidationError,
    validate_prototype_result,
)


class Windows7PrototypeResultTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        root = Path(self.directory.name)
        self.result = root / "result.json"
        self.screenshot = root / "screen.png"
        self.screenshot.write_bytes(png_image())

    def tearDown(self) -> None:
        self.directory.cleanup()

    def valid(self) -> dict[str, object]:
        return {
            "schema": "maps.win7.prototype.v1",
            "status": "PASS",
            "commit": "abc123",
            "guest_shutdown": True,
            "checks": {
                "P-01-process-started": True,
                "P-02-window-visible": True,
                "P-03-sqlite-read": True,
                "P-04-mapsui-layer": True,
                "P-05-native-window": True,
                "P-06-screenshot": True,
                "P-07-gui-point-assertion": True,
                "P-08-no-exception": True,
            },
        }

    def test_accepts_prototype_pass(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        self.assertEqual(
            "PASS",
            validate_prototype_result(self.result, self.screenshot, "abc123")["status"],
        )

    def test_rejects_production_schema(self) -> None:
        value = self.valid()
        value["schema"] = "maps.win7.smoke.v1"
        self.result.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaises(ResultValidationError):
            validate_prototype_result(self.result, self.screenshot, "abc123")

    def test_rejects_missing_gui_assertion(self) -> None:
        value = self.valid()
        checks = value["checks"]
        assert isinstance(checks, dict)
        checks["P-07-gui-point-assertion"] = False
        self.result.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaises(ResultValidationError):
            validate_prototype_result(self.result, self.screenshot, "abc123")


if __name__ == "__main__":
    unittest.main()
