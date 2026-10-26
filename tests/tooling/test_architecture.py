"""Check that removing optional references stays valid and forbidden edges fail."""

import importlib.util
from pathlib import Path
import unittest

SOURCE = Path(__file__).resolve().parents[2] / "tools/check_architecture.py"
SPEC = importlib.util.spec_from_file_location("check_architecture", SOURCE)
architecture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(architecture)


class ArchitectureChecks(unittest.TestCase):
    def test_optional_reference_can_be_removed(self):
        graph = {name: set() for name in architecture.PROJECTS}
        self.assertEqual([], architecture.validate_graph(graph))

    def test_forbidden_reference_is_rejected(self):
        graph = {name: set() for name in architecture.PROJECTS}
        graph["Application"].add("Infrastructure")
        self.assertIn("Application has forbidden project references: Infrastructure",
                      architecture.validate_graph(graph))
