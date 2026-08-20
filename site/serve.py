#!/usr/bin/env python3
"""The local preview server.

    python3 site/serve.py [port]

`python3 -m http.server` would do, except for one thing: it sends no
`Cache-Control` and no `ETag`, only `Last-Modified`. Browsers then fall back to
*heuristic freshness* and reuse a file without asking whether it changed — which
is invisible for a page of HTML and fatal for `/try/`.

The WebAssembly bundle carries subresource integrity hashes: `dotnet.boot.js`
lists a sha256 for every `.wasm` it loads. Republish the bundle and a browser
holding the previous `dotnet.boot.js` will check the new `.wasm` files against
the old hashes, fail every one, and refuse to load them. What you see is a blank
page and a console full of integrity errors — with nothing wrong on disk.

So: no-store, everywhere. This is a preview server; there is nothing here worth
caching, and the failure it prevents costs half an hour to diagnose.

**The same hazard is real in production.** Whatever serves warp11.org must
revalidate `/try/_framework/*` — `Cache-Control: no-cache` on `dotnet.boot.js`
at the very least — or the first deploy after a bundle change breaks the page
for everyone who has visited before.
"""

import http.server
import os
import sys

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
ROOT = __file__.rsplit("/", 1)[0] + "/out"


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    def translate_path(self, path):
        """Resolve extensionless URLs to the `.html` on disk.

        `build.fsx` links pages as `/guides/architecture`, not
        `/guides/architecture.html`, because that is the URL the host serves
        them at — Cloudflare's asset handling maps one to the other, and
        emitting the extension cost a redirect on every internal link. Nothing
        does that mapping locally, so this does, and preview matches production.

        Directories still fall through to the base class, which finds
        `index.html` — that is how `/` and `/try/` work in both places.
        """
        resolved = super().translate_path(path)

        if not os.path.exists(resolved) and os.path.isfile(resolved + ".html"):
            return resolved + ".html"

        return resolved

    def end_headers(self):
        self.send_header("Cache-Control", "no-store, must-revalidate")
        super().end_headers()

    def log_message(self, format, *args):
        # Every asset of the wasm bundle is a line; the interesting ones are the
        # failures, which the base class logs through the same call.
        if not args or "200" not in str(args[1] if len(args) > 1 else ""):
            super().log_message(format, *args)


if __name__ == "__main__":
    print(f"serving {ROOT} on http://127.0.0.1:{PORT} — no-store")
    http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
