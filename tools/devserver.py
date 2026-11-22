"""Local file server with HTTP Range support, for trying Fetchr by hand.

    python tools/devserver.py <folder> [--port 8800] [--kbps 2000]

Serves files from <folder> on 127.0.0.1 only. --kbps throttles each connection so you can
watch segments fill and test pause/resume. Standard library only.
"""
import argparse
import http.server
import os
import re
import time

RANGE = re.compile(r"bytes=(\d+)-(\d*)$")


class Handler(http.server.SimpleHTTPRequestHandler):
    kbps = 0

    def send_head(self):
        path = self.translate_path(self.path)
        if not os.path.isfile(path):
            self.send_error(404)
            return None
        size = os.path.getsize(path)
        start, end = 0, size - 1
        m = RANGE.match(self.headers.get("Range", ""))
        if m:
            start = int(m.group(1))
            if m.group(2):
                end = min(int(m.group(2)), size - 1)
            if start >= size:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.end_headers()
                return None
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        else:
            self.send_response(200)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("ETag", f'"{int(os.path.getmtime(path))}-{size}"')
        self.end_headers()
        f = open(path, "rb")
        f.seek(start)
        self._remaining = end - start + 1
        return f

    def copyfile(self, source, outputfile):
        chunk = 64 * 1024
        delay = chunk / (self.kbps * 1024) if self.kbps else 0
        while self._remaining > 0:
            data = source.read(min(chunk, self._remaining))
            if not data:
                break
            outputfile.write(data)
            self._remaining -= len(data)
            if delay:
                time.sleep(delay)

    def log_message(self, fmt, *args):
        print("%s %s" % (self.headers.get("Range", "-"), fmt % args), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--port", type=int, default=8800)
    ap.add_argument("--kbps", type=int, default=0, help="per-connection limit in KB/s (0 = unlimited)")
    a = ap.parse_args()
    Handler.kbps = a.kbps
    handler = lambda *args, **kw: Handler(*args, directory=a.folder, **kw)
    with http.server.ThreadingHTTPServer(("127.0.0.1", a.port), handler) as srv:
        print(f"serving {a.folder} on http://127.0.0.1:{a.port}/ ({a.kbps or 'unlimited'} KB/s per connection)", flush=True)
        srv.serve_forever()


if __name__ == "__main__":
    main()
