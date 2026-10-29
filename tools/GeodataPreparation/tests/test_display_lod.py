import unittest

from tools.GeodataPreparation.display_lod import include_in_display_lod


class DisplayLodTests(unittest.TestCase):
    def test_gas_crossing_candidates_are_discoverable_at_overview_levels(self):
        self.assertTrue(
            include_in_display_lod("GasPipelineCrossingCandidate", {}, 0, 0)
        )
        self.assertTrue(
            include_in_display_lod("GasPipelineCrossingCandidate", {}, 0, 1)
        )

    def test_source_pipeline_stays_hidden(self):
        self.assertFalse(include_in_display_lod("OverheadGasPipeline", {}, 100, 0))

    def test_large_fields_keep_country_overview_from_becoming_blank(self):
        self.assertTrue(include_in_display_lod("Field", {}, 0.01, 0))
