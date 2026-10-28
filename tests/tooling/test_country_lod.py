import importlib.util
import sys
import unittest
from pathlib import Path

MODULE_PATH = (
    Path(__file__).parents[2] / "tools" / "GeodataPreparation" / "display_lod.py"
)
sys.path.insert(0, str(MODULE_PATH.parent))
SPEC = importlib.util.spec_from_file_location("display_lod", MODULE_PATH)
display_lod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(display_lod)


class CountryLodTests(unittest.TestCase):
    def test_country_overview_keeps_only_city_labels(self):
        include = display_lod.include_in_display_lod

        self.assertTrue(include("Settlement", {"place": "city"}, 0, 0))
        self.assertFalse(include("Settlement", {"place": "town"}, 0, 0))
        self.assertFalse(include("Settlement", {"place": "village"}, 0, 0))

    def test_regional_lod_keeps_only_city_and_town_labels(self):
        include = display_lod.include_in_display_lod

        self.assertTrue(include("Settlement", {"place": "city"}, 0, 1))
        self.assertTrue(include("Settlement", {"place": "town"}, 0, 1))
        self.assertFalse(include("Settlement", {"place": "village"}, 0, 1))
        self.assertFalse(include("Settlement", {"place": "hamlet"}, 0, 1))

    def test_local_lod_retains_smaller_settlements(self):
        include = display_lod.include_in_display_lod

        self.assertTrue(include("Settlement", {"place": "village"}, 0, 2))
        self.assertTrue(include("Settlement", {"place": "hamlet"}, 0, 2))
