"""Parallel local evidence keeps inputs isolated and report ordering stable."""

import importlib
import sqlite3
import sys
import tempfile
import threading
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))


class RoutingParallelTests(unittest.TestCase):
    def test_parallel_cases_keep_input_order_and_read_only_database_isolation(self):
        runner = importlib.import_module("routing_regression")
        with tempfile.TemporaryDirectory() as temporary:
            data = Path(temporary)
            with closing(sqlite3.connect(data / "geodata.sqlite")) as connection:
                connection.execute("CREATE TABLE facts(value)")
                connection.execute("INSERT INTO facts VALUES(42)")
                connection.commit()
            barrier = threading.Barrier(2)

            def verify(case, connection, *_args, **_kwargs):
                barrier.wait(timeout=10)
                value = connection.execute("SELECT value FROM facts").fetchone()[0]
                with self.assertRaises(sqlite3.OperationalError):
                    connection.execute("INSERT INTO facts VALUES(43)")
                return {"id": case["id"], "value": value}

            cases = [{"id": "first"}, {"id": "second"}]
            with patch.object(runner, "run_case", side_effect=verify):
                results = list(
                    runner.run_cases(
                        cases,
                        data,
                        data / "verify.dll",
                        data / "output",
                        None,
                        default_underpasses=False,
                        timeout_seconds=120,
                        workers=2,
                    )
                )
            self.assertEqual(
                [{"id": "first", "value": 42}, {"id": "second", "value": 42}], results
            )

    def test_worker_failure_propagates_instead_of_becoming_partial_success(self):
        runner = importlib.import_module("routing_regression")
        with tempfile.TemporaryDirectory() as temporary:
            data = Path(temporary)
            with closing(sqlite3.connect(data / "geodata.sqlite")) as connection:
                connection.execute("CREATE TABLE facts(value)")
            with patch.object(runner, "run_case", side_effect=ValueError(123)):
                with self.assertRaises(ValueError) as error:
                    list(
                        runner.run_cases(
                            [{"id": "broken"}],
                            data,
                            data / "verify.dll",
                            data / "output",
                            None,
                            default_underpasses=False,
                            timeout_seconds=120,
                            workers=2,
                        )
                    )
            self.assertEqual((123,), error.exception.args)
