import json
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

from tools.windows7_result import ResultValidationError, validate_result


def png_image(width: int = 640, height: int = 480) -> bytes:
    signature = b"\x89PNG\r\n\x1a\n"
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)

    def chunk(kind: bytes, value: bytes) -> bytes:
        return (
            struct.pack(">I", len(value))
            + kind
            + value
            + struct.pack(">I", zlib.crc32(kind + value) & 0xFFFFFFFF)
        )

    pixels = b"".join(b"\x00" + bytes(width * 4) for _ in range(height))
    return (
        signature
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(pixels))
        + chunk(b"IEND", b"")
    )


class Windows7ResultTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.result = self.root / "result.json"
        self.screenshot = self.root / "screen.png"
        self.screenshot.write_bytes(png_image())

    def tearDown(self) -> None:
        self.directory.cleanup()

    def valid(self) -> dict[str, object]:
        return {
            "schema": "maps.win7.smoke.v1",
            "status": "PASS",
            "commit": "abc123",
            "guest_shutdown": True,
            "checks": {f"W7-{number:02d}": True for number in range(1, 10)},
        }

    def test_accepts_complete_result_and_valid_png(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        self.assertEqual(
            "PASS", validate_result(self.result, self.screenshot, "abc123")["status"]
        )

    def test_rejects_wrong_commit(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        with self.assertRaisesRegex(ResultValidationError, "commit"):
            validate_result(self.result, self.screenshot, "different")

    def test_rejects_missing_check_even_with_guest_shutdown(self) -> None:
        value = self.valid()
        checks = value["checks"]
        assert isinstance(checks, dict)
        del checks["W7-06"]
        self.result.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(ResultValidationError, "W7-06"):
            validate_result(self.result, self.screenshot, "abc123")

    def test_rejects_false_pass_and_corrupt_png(self) -> None:
        value = self.valid()
        value["status"] = "PASS"
        self.result.write_text(json.dumps(value), encoding="utf-8")
        self.screenshot.write_bytes(b"WIN7_SMOKE_PASS")
        with self.assertRaisesRegex(ResultValidationError, "PNG"):
            validate_result(self.result, self.screenshot, "abc123")

    def test_rejects_truncated_png(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        self.screenshot.write_bytes(png_image()[:-10])
        with self.assertRaisesRegex(ResultValidationError, "PNG"):
            validate_result(self.result, self.screenshot, "abc123")

    def test_rejects_png_below_minimum_dimensions(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        self.screenshot.write_bytes(png_image(320, 200))
        with self.assertRaisesRegex(ResultValidationError, "PNG"):
            validate_result(self.result, self.screenshot, "abc123")

    def test_rejects_png_without_image_data(self) -> None:
        self.result.write_text(json.dumps(self.valid()), encoding="utf-8")
        image = png_image()
        idat_start = image.index(b"IDAT") - 4
        idat_length = struct.unpack(">I", image[idat_start : idat_start + 4])[0]
        idat_end = idat_start + idat_length + 12
        self.screenshot.write_bytes(image[:idat_start] + image[idat_end:])
        with self.assertRaisesRegex(ResultValidationError, "PNG"):
            validate_result(self.result, self.screenshot, "abc123")

    def test_rejects_malformed_json(self) -> None:
        self.result.write_text("{", encoding="utf-8")
        with self.assertRaises(ResultValidationError):
            validate_result(self.result, self.screenshot, "abc123")


if __name__ == "__main__":
    unittest.main()
