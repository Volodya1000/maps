"""Compatibility spike only. Bind strictly to loopback. Ctrl-C closes the server."""
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
if __name__ == '__main__':
    handler=partial(SimpleHTTPRequestHandler,directory=str(Path(__file__).resolve().parent))
    with ThreadingHTTPServer(('127.0.0.1',8765),handler) as server:
        print('MapLibre spike: http://127.0.0.1:8765',flush=True)
        try:server.serve_forever()
        except KeyboardInterrupt:pass
