import gzip
from pathlib import Path
import tempfile
import unittest
from tools.split_android_map import split_package


class AndroidMapChunks(unittest.TestCase):
    def test_chunks_reconstruct_database_in_order_and_preserve_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'source'
            source.mkdir()
            database = bytes(range(256)) * 40
            (source / 'manifest.json').write_text('{"region_id":"belarus"}')
            with gzip.open(source / 'geodata.sqlite.gz', 'wb') as stream:
                stream.write(database)
            output = Path(directory) / 'output'

            split_package(source, output, chunk_bytes=1024)

            chunks = sorted(output.glob('geodata.*.gz'))
            self.assertGreater(len(chunks), 1)
            self.assertEqual(database, b''.join(gzip.decompress(path.read_bytes()) for path in chunks))
            self.assertEqual((source / 'manifest.json').read_bytes(), (output / 'manifest.json').read_bytes())
            with self.assertRaises(ValueError):
                split_package(source, output, chunk_bytes=1024)
