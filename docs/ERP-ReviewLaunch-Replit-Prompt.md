# Replit AI Prompt: "Open in MassiveSLICER" review launch — desktop status + test client

**Paste everything below the line into the Replit AI / agent.**
This answers your `massiveslicer-review` launch contract (proposed v1) from the desktop side.

---

## Where the desktop is today (2026-10-22)

Be precise about this in the ERP UI and in any acceptance record:

- **MassiveSLICER does not implement `massiveslicer-review://` yet.** Work has started on branch
  `feature/erp-review-launch`. No build registers that scheme. Clicking a launch link today will
  hit "no registered handler" on both platforms. Keep the gate OFF in production.
- **The manual path works today:** download the SRC or MASS from Review files, then
  - `.src` → drag onto MassiveSLICER (or Import → KRL). It imports as a print/mill toolpath.
  - `.mass` → File → Open (or double-click).
  - **DAT is not used.** The desktop SRC importer reads only the SRC text. A paired DAT is
    harmless but has no effect today.
- **First step already landed on the branch:** the desktop's localhost control bridge
  (`127.0.0.1:8723`) now refuses browser-originated requests to everything except `/ping`,
  `/reveal`, `/open`. The review launch will **not** go through that bridge from the browser.
  Do not have ERP pages call `127.0.0.1:8723` for review.

So right now you can test your **server** half against a stand-in desktop client (below), but
not the real OS handoff.

## Facts about the desktop you need for the contract

1. **The "existing Slicer connection"** is `AppPreferences.ErpBaseUrl`
   (default `https://lab.massivemake.com/api/slicer/v1`) plus a bearer token (`msl_…`), either
   pasted from ERP Settings → Slicer Access or issued by `POST /api/slicer/v1/login`.
   The desktop will treat the **origin of `ErpBaseUrl`** as the only approved ERP origin.
   So production `SLICER_REVIEW_ERP_ORIGIN` must be exactly `https://lab.massivemake.com`.
   A link with any other `origin` will be rejected with "sign into that ERP first" — no fetch.
2. **Windows registration is done by the app, not an installer.** There is no installer in the
   desktop repo. On every launch the app writes `HKCU\Software\Classes\massiveslicer` with
   `"<exe>" "%1"` (per-user, no `cmd`/PowerShell). `massiveslicer-review` will be registered the
   same way. Please accept per-user self-registration as meeting your "Installer registration"
   section, or tell us what you need instead. Uninstall-cleanup will not happen automatically.
3. **The existing `massiveslicer://open?path=<.mass on MassiveFILES>` scheme stays as-is** for
   MassiveBOARD. It is a different scheme. Do not use it for review — it carries a path, which
   your contract forbids, and it does not verify hash.
4. **Already-running instance:** cold start gets the URL as argv (Windows) or a URL-open event
   (macOS — not wired yet). A second launch forwards the URL to the running app over loopback
   from a non-browser process, and the running app re-parses it strictly. It never executes it.
5. **Strict URL parse the desktop will apply** (tell us if any of this is wrong):
   - scheme `massiveslicer-review`, host `review`, empty path, no fragment
   - exactly `v`, `origin`, `handoff`, each once, nothing else
   - `v` = `1`
   - `origin` = exact HTTPS origin, no path/query/credentials/trailing slash
   - `handoff` = `^[A-Za-z0-9_-]{43}$` (32 random bytes, base64url, no padding)

## Things we need from you

1. **A test ERP with the gate ON.** Your gate is only to be enabled after both platforms pass,
   but we cannot pass without a gate that issues handoffs. Please stand up a staging deployment
   (not production) with `SLICER_REVIEW_PROTOCOL_VERSION=1` and its own HTTPS origin, plus a
   test project/element with an SRC, an SRC+DAT pair and a MASS attached, and a staging `msl_`
   token. Send us the origin and how to get the token.
2. **Handle in the download path.** `downloadPath` puts the handle in the URL
   (`/review/handoffs/<handle>/files/<id>`), so it will appear in your reverse-proxy and app
   access logs. Your contract says not to log handles. Either redact that path segment in logs
   or move the handle to a header/body. We will follow whatever path `resolve` returns.
3. **Confirm the 43-char handle format** above.
4. **Is "DAT downloaded and verified but not used for display" acceptable for v1?**

## Test it now with a stand-in desktop client

This script does exactly what the desktop will do after the URL arrives: strict parse,
origin match, `resolve`, download every file with redirects disabled and byte limits, verify
size + SHA-256. Run it against your dev/staging server to prove the server half. It prints
no token, handle or file contents.

```bash
SLICER_ERP_BASE=https://<staging-origin>/api/slicer/v1 \
SLICER_TOKEN=msl_xxx \
python3 review_probe.py 'massiveslicer-review://review?v=1&origin=https%3A%2F%2F<staging-origin>&handoff=<handle>'
```

```python
#!/usr/bin/env python3
"""Stand-in MassiveSLICER client for the review-launch contract v1."""
import hashlib, json, os, re, sys, urllib.error, urllib.parse, urllib.request

MAX_FILE = 50 * 1024 * 1024
HANDLE = re.compile(r"^[A-Za-z0-9_-]{43}$")

def fail(msg):
    print("FAIL:", msg); sys.exit(1)

def parse(url):
    u = urllib.parse.urlsplit(url)
    if u.scheme != "massiveslicer-review" or u.netloc != "review" or u.path not in ("", "/") or u.fragment:
        fail("bad scheme/host/path/fragment")
    pairs = urllib.parse.parse_qsl(u.query, keep_blank_values=True, strict_parsing=True)
    keys = [k for k, _ in pairs]
    if sorted(keys) != ["handoff", "origin", "v"]:
        fail(f"params must be exactly v, origin, handoff (got {keys})")
    q = dict(pairs)
    if q["v"] != "1": fail("unsupported version")
    o = urllib.parse.urlsplit(q["origin"])
    if o.scheme != "https" or not o.netloc or o.path or o.query or o.fragment or "@" in o.netloc:
        fail("origin is not an exact https origin")
    if not HANDLE.match(q["handoff"]): fail("malformed handle")
    return q["origin"], q["handoff"]

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k): return None

opener = urllib.request.build_opener(NoRedirect)

def call(method, url, token, body=None, limit=1024 * 1024):
    req = urllib.request.Request(url, method=method, data=body)
    req.add_header("Authorization", "Bearer " + token)
    if body is not None: req.add_header("Content-Type", "application/json")
    try:
        with opener.open(req, timeout=60) as r:
            data = r.read(limit + 1)
            if len(data) > limit: fail("response over byte limit")
            return r.status, data
    except urllib.error.HTTPError as e:
        return e.code, e.read(4096)

def main():
    origin, handle = parse(sys.argv[1])
    base = os.environ["SLICER_ERP_BASE"].rstrip("/")
    token = os.environ["SLICER_TOKEN"]
    b = urllib.parse.urlsplit(base)
    if f"{b.scheme}://{b.netloc}" != origin:
        fail("link origin does not match the configured ERP — would ask the user to sign in")

    body = json.dumps({"protocolVersion": 1, "handoff": handle,
                       "supportedFormats": ["src", "dat", "mass"]}).encode()
    status, data = call("POST", origin + "/api/slicer/v1/review/resolve", token, body)
    print("resolve:", status)
    if status != 200: print(data.decode(errors="replace")[:500]); sys.exit(1)
    m = json.loads(data)
    if m.get("protocolVersion") != 1 or m.get("origin") != origin: fail("manifest version/origin mismatch")
    print(f"project {m['project']['id']} / element {m['element']['id']} / primary {m['primaryFileId']}")

    for f in m["files"]:
        p = f["downloadPath"]
        if not p.startswith("/api/slicer/v1/review/handoffs/") or "//" in p or ".." in p:
            fail("downloadPath not a same-origin contract path")
        if f["format"] not in ("src", "dat", "mass"): fail("unsupported format " + f["format"])
        if f["size"] > MAX_FILE: fail("file over 50 MiB")
        status, data = call("GET", origin + p, token, limit=f["size"])
        if status != 200: fail(f"download {f['id']} -> {status}")
        ok = len(data) == f["size"] and hashlib.sha256(data).hexdigest() == f["sha256"]
        print(f"  {f['format']:4} {f['filename']!r} size={len(data)} companionOf={f['companionOf']} "
              f"{'VERIFIED' if ok else 'HASH/SIZE MISMATCH'}")
        if not ok: sys.exit(1)
    print("PASS")

main()
```

**Cases to run with it** (expected result from your contract):

| Case | Expect |
|---|---|
| Fresh handle, SRC only | `PASS`, one file |
| Fresh handle, SRC + DAT | `PASS`, DAT `companionOf` = SRC id |
| Fresh handle, MASS | `PASS` |
| Same handle after 5 min | resolve `410` |
| Handle revoked via `DELETE /api/slicer-review/handoffs/{handle}` | `410` |
| Token of a different user | `404` |
| Revoked/blank token | `401` |
| Gate off | `409` |
| Link `origin` ≠ `SLICER_ERP_BASE` origin | `FAIL` locally, **no request sent** |
| Extra/duplicate query param, `v=2`, 42-char handle | `FAIL` locally, no request sent |
| NAS bytes changed after selection | still `PASS` with original hash |

When the desktop build with the real handler is ready we will send you a build and run the
external acceptance checklist on both platforms.
