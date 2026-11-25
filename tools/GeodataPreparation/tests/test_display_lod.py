import unittest

from tools.GeodataPreparation.display_lod import include_in_display_lod


class DisplayLodTests(unittest.TestCase):
    def test_railway_main_lines_survive_overview_and_local_lod(self):
        for lod in (0, 1, 2):
            for kind in ("Railway", "RailwayBridge", "RailwayTunnel"):
                self.assertTrue(
                    include_in_display_lod(kind, {"railway": "rail"}, 0, lod)
                )

    def test_secondary_railway_types_only_appear_locally(self):
        for kind in ("light_rail", "narrow_gauge", "tram"):
            self.assertFalse(include_in_display_lod("Railway", {"railway": kind}, 0, 0))
            self.assertFalse(include_in_display_lod("Railway", {"railway": kind}, 0, 1))
            self.assertTrue(include_in_display_lod("Railway", {"railway": kind}, 0, 2))

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
