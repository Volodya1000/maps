"""Exercise the runtime audit CLI with representative strace destinations."""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "tools/check_network.py"


class RuntimeNetworkTests(unittest.TestCase):
    def audit(self, trace: str) -> tuple[int, dict]:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "runtime.strace"
            output = root / "reports/network.json"
            source.write_text(trace, encoding="utf-8")
            process = subprocess.run(
                [sys.executable, str(SCRIPT), str(source), "--output", str(output)],
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertTrue(output.is_file(), process.stderr)
            result = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(json.loads(process.stdout), result)
            return process.returncode, result

    def test_ipv4_ipv6_and_mapped_loopback_are_allowed(self):
        trace = "\n".join(
            [
                '12 connect(3, {sa_family=AF_INET, sin_addr=inet_addr("127.0.0.2")}, 16) = 0',
                '12 connect(3, {sa_family=AF_INET6, inet_pton(AF_INET6, "::1", &sin6_addr)}, 28) = 0',
                '12 sendto(3, "x", 1, 0, {sa_family=AF_INET6, inet_pton(AF_INET6, "::ffff:127.0.0.1", &sin6_addr)}, 28) = 1',
            ]
        )
        code, result = self.audit(trace)
        self.assertEqual(code, 0)
        self.assertEqual(
            result["destinations"], ["127.0.0.2", "::1", "::ffff:127.0.0.1"]
        )
        self.assertEqual(result["non_loopback_calls"], [])

    def test_external_ipv4_and_ipv6_are_reported_even_when_connect_fails(self):
        for destination in (
            'sa_family=AF_INET, sin_addr=inet_addr("192.0.2.1")',
            'sa_family=AF_INET6, inet_pton(AF_INET6, "2001:db8::1", &sin6_addr)',
            'sa_family=AF_INET6, inet_pton(AF_INET6, "::ffff:192.0.2.1", &sin6_addr)',
        ):
            with self.subTest(destination=destination):
                line = f"12 connect(3, {{{destination}}}, 28) = -1 ENETUNREACH"
                code, result = self.audit(line)
                self.assertEqual(code, 1)
                self.assertEqual(result["non_loopback_calls"], [line])
                self.assertEqual(len(result["destinations"]), 1)

    def test_malformed_internet_addresses_fail_closed_with_report(self):
        for destination in (
            'sa_family=AF_INET, sin_addr=inet_addr("999.1.2.3")',
            'sa_family=AF_INET6, inet_pton(AF_INET6, "not-ipv6", &sin6_addr)',
            "sa_family=AF_INET, sin_addr=0x1234",
        ):
            with self.subTest(destination=destination):
                line = f"12 connect(3, {{{destination}}}, 28) = 0"
                code, result = self.audit(line)
                self.assertEqual(code, 1)
                self.assertEqual(result["non_loopback_calls"], [line])

    def test_empty_and_unix_traces_have_no_internet_destinations(self):
        for trace in (
            "",
            '12 connect(3, {sa_family=AF_UNIX, sun_path="/tmp/socket"}, 20) = 0',
        ):
            with self.subTest(trace=trace):
                code, result = self.audit(trace)
                self.assertEqual(code, 0)
                self.assertEqual(result["destinations"], [])
                self.assertEqual(result["non_loopback_calls"], [])


if __name__ == "__main__":
    unittest.main()
