"""Validation of the structured result produced by the Windows 7 guest agent."""

from __future__ import annotations

import json
import struct
import zlib
from pathlib import Path
from typing import Any

REQUIRED_CHECKS = tuple(f"W7-{number:02d}" for number in range(1, 10))
SCHEMA = "maps.win7.smoke.v1"


class ResultValidationError(ValueError):
    """Raised when a guest result cannot prove a successful smoke test."""


def _valid_png(path: Path) -> bool:
    try:
        data = path.read_bytes()
    except OSError:
        return False
    signature = b"\x89PNG\r\n\x1a\n"
    if not data.startswith(signature):
        return False

    position = len(signature)
    saw_header = False
    saw_data = False
    saw_end = False
    width = height = bit_depth = color_type = interlace = 0
    compressed = bytearray()
    while position + 12 <= len(data):
        length = struct.unpack(">I", data[position : position + 4])[0]
        end = position + 12 + length
        if end > len(data):
            return False
        chunk_type = data[position + 4 : position + 8]
        chunk_data = data[position + 8 : position + 8 + length]
        expected_crc = struct.unpack(">I", data[position + 8 + length : end])[0]
        if zlib.crc32(chunk_type + chunk_data) & 0xFFFFFFFF != expected_crc:
            return False
        if chunk_type == b"IHDR":
            if saw_header or length != 13:
                return False
            width, height, bit_depth, color_type, _, _, interlace = struct.unpack(
                ">IIBBBBB", chunk_data
            )
            saw_header = width >= 640 and height >= 480
            if bit_depth not in (8, 16) or color_type not in (0, 2, 4, 6):
                return False
            if interlace != 0:
                return False
        elif chunk_type == b"IDAT":
            if not saw_header or saw_end:
                return False
            saw_data = True
            compressed.extend(chunk_data)
        elif chunk_type == b"IEND":
            saw_end = length == 0 and end == len(data)
            break
        position = end
    if not (saw_header and saw_data and saw_end):
        return False
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    bytes_per_pixel = channels * (bit_depth // 8)
    row_size = (width * channels * bit_depth + 7) // 8
    try:
        decoded = zlib.decompress(bytes(compressed))
    except zlib.error:
        return False
    expected_size = height * (row_size + 1)
    if len(decoded) != expected_size:
        return False
    return (
        all(decoded[offset] <= 4 for offset in range(0, expected_size, row_size + 1))
        and bytes_per_pixel > 0
    )


def validate_result(
    result_path: Path, screenshot_path: Path, commit: str
) -> dict[str, Any]:
    try:
        result = json.loads(result_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ResultValidationError(f"invalid guest result JSON: {error}") from error
    if not isinstance(result, dict):
        raise ResultValidationError("guest result must be a JSON object")
    if result.get("schema") != SCHEMA:
        raise ResultValidationError("unsupported or missing result schema")
    if result.get("status") != "PASS":
        raise ResultValidationError("guest result is not PASS")
    if result.get("commit") != commit:
        raise ResultValidationError("guest result commit does not match checked commit")
    if result.get("guest_shutdown") is not True:
        raise ResultValidationError("guest did not report a clean shutdown")
    checks = result.get("checks")
    if not isinstance(checks, dict):
        raise ResultValidationError("guest checks are missing")
    for check in REQUIRED_CHECKS:
        if checks.get(check) is not True:
            raise ResultValidationError(f"required check {check} is not true")
    if not screenshot_path.is_file() or screenshot_path.stat().st_size == 0:
        raise ResultValidationError("screenshot is missing or empty")
    if not _valid_png(screenshot_path):
        raise ResultValidationError("screenshot is not a valid non-empty PNG")
    return result
