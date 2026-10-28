import hashlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from shapely.geometry import LineString

from tools.GeodataPreparation.feature_classification import classify
from tools.GeodataPreparation.pipeline_crossings import crossing_spans


class PipelineCrossingTests(unittest.TestCase):
    def test_only_explicit_overhead_gas_is_imported(self):
        tags = dict(man_made="pipeline", substance="gas", location="overhead")
        self.assertEqual("OverheadGasPipeline", classify(tags))
        for change in [
            dict(location="underground"),
            dict(location="overground"),
            dict(substance="oil"),
            dict(location=""),
        ]:
            self.assertIsNone(classify(tags | change))

    def test_transverse_crossing_keeps_pipe_axis_and_both_approaches(self):
        pipeline = LineString([(27, 54), (27, 54.01)])
        road = LineString([(26.99, 54.005), (27.01, 54.005)])
        (span,) = crossing_spans(pipeline, road)
        self.assertLess(span.distance(road), 1e-12)
        self.assertLess(span.difference(pipeline.buffer(1e-12)).length, 1e-12)
        self.assertAlmostEqual(24, span.length * 111195.0802335, places=5)
        self.assertEqual(3, len(span.coords))

    def test_parallel_shared_line_and_endpoint_touch_are_not_overhead_crossings(self):
        pipeline = LineString([(27, 54), (27, 54.01)])
        self.assertEqual([], crossing_spans(pipeline, pipeline))
        self.assertEqual(
            [], crossing_spans(pipeline, LineString([(26.99, 54), (27, 54)]))
        )
        self.assertEqual(
            [], crossing_spans(pipeline, LineString([(27.001, 54), (27.001, 54.01)]))
        )

    def test_bounded_import_produces_candidate_and_excludes_pedestrian_and_underground(
        self,
    ):
        osm = """<osm version="0.6">
        <node id="1" lon="27" lat="54"/><node id="2" lon="27" lat="54.01"/>
        <node id="3" lon="26.99" lat="54.005"/><node id="4" lon="27.01" lat="54.005"/>
        <way id="1"><nd ref="1"/><nd ref="2"/><tag k="man_made" v="pipeline"/><tag k="substance" v="gas"/><tag k="location" v="overhead"/></way>
        <way id="2"><nd ref="3"/><nd ref="4"/><tag k="highway" v="service"/></way>
        <way id="3"><nd ref="3"/><nd ref="4"/><tag k="highway" v="path"/></way>
        <way id="4"><nd ref="1"/><nd ref="2"/><tag k="man_made" v="pipeline"/><tag k="substance" v="gas"/><tag k="location" v="underground"/></way>
        </osm>"""
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.osm"
            source.write_text(osm)
            output = Path(directory) / "data"
            subprocess.run(
                [
                    sys.executable,
                    str(Path(__file__).resolve().parents[1] / "prepare.py"),
                    "--osm",
                    str(source),
                    "--bounds",
                    "26.98",
                    "53.99",
                    "27.02",
                    "54.02",
                    "--output",
                    str(output),
                    "--expected-sha256",
                    hashlib.sha256(source.read_bytes()).hexdigest(),
                ],
                check=True,
                capture_output=True,
            )
            with sqlite3.connect(output / "geodata.sqlite") as connection:
                counts = dict(
                    connection.execute(
                        "SELECT kind,count(*) FROM features GROUP BY kind"
                    )
                )
                self.assertEqual(1, counts["OverheadGasPipeline"])
                self.assertEqual(1, counts["GasPipelineCrossingCandidate"])
                self.assertEqual(1, counts["Road"])
                self.assertNotIn("ApprovedCrossing", counts)
