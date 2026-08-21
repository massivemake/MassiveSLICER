# Replit AI Prompt: MassiveSLICER password login

**Paste everything below the line into the Replit AI / agent.**
This is a build request for `lab.massivemake.com` (MassiveMAKE ERP). MassiveSLICER already implements the client.

---

## Who you are building for

- **Product:** MassiveMAKE ERP at **https://lab.massivemake.com**
- **Consumer:** **MassiveSLICER** desktop (Windows shop PCs + macOS)
- **Existing auth:** every `/api/slicer/v1/*` call uses `Authorization: Bearer <token>` from **ERP Settings → Slicer Access** (`msl_…` tokens)
- **Goal:** let a user sign in with their **Lab email + password** and get that same slicer bearer back, so nobody pastes an Auth / Access token on each PC

## Problem

Shop machines currently store a long-lived Slicer Access token in Preferences. That is easy to lose and annoying to rotate. Users already have a Lab account. MassiveSLICER now has Email + Password fields and will `POST /api/slicer/v1/login` on Connect / launch. That route **does not exist yet** (the client treats HTTP 404 as "paste a token instead").

Do **not** break existing bearer tokens. Login only **issues** one.

## Build this one route

```
POST /api/slicer/v1/login
```

**No** `Authorization` header. JSON body (slicer sends both keys; accept either):

```json
{
  "email": "thom@massivemake.com",
  "username": "thom@massivemake.com",
  "password": "the-lab-password"
}
```

Lookup the Lab user by email (case-insensitive) **or** username. Verify the password with the **same hasher** the website login uses. Do not invent a second password store.

### 200 — success

Return a **slicer API token** in the same family as Settings → Slicer Access (`msl_…` or whatever you already mint). Reuse an existing active slicer token for this user if you have one; otherwise create one (label e.g. `MassiveSLICER · <email>`).

```json
{
  "token": "msl_…",
  "email": "thom@massivemake.com",
  "name": "Thom Boessel",
  "expiresAt": null
}
```

- `token` is required (also accepted as `accessToken` / `apiToken`)
- `expiresAt` may be null (preferred: long-lived, same as today's Slicer Access tokens). If you expire tokens, use ISO-8601 UTC.
- Optional: nest under `data` or `user`; the slicer already unwraps those.

### Errors

| Status | When |
|--------|------|
| 400 | missing email/username or password |
| 401 | unknown user or wrong password (generic message, do not leak which) |
| 403 | user exists but is not allowed to use the Slicer API (disabled / no shop role) |
| 429 | optional rate limit after repeated failures |

JSON error body with a human `error` or `message` string. Example:

```json
{ "error": "invalid_credentials", "message": "Email or password is incorrect." }
```

### Do not

- Do not require a second factor for v1
- Do not return the website session cookie as the slicer token
- Do not accept this route with a bearer token instead of a password
- Do not log the password
- Do not change `/search`, `/pricing`, `/quote`, `/elements`, `/presets-*`

## Verify after deploy

```bash
export BASE="https://lab.massivemake.com/api/slicer/v1"

# 400
curl -sS -o /tmp/login400.json -w "%{http_code}\n" \
  -H "Content-Type: application/json" \
  -d '{"email":""}' "$BASE/login"

# 401
curl -sS -o /tmp/login401.json -w "%{http_code}\n" \
  -H "Content-Type: application/json" \
  -d '{"email":"nobody@example.com","password":"nope"}' "$BASE/login"

# 200 — use a real Lab user
curl -sS -o /tmp/login200.json -w "%{http_code}\n" \
  -H "Content-Type: application/json" \
  -d '{"email":"REAL@massivemake.com","password":"REAL"}' "$BASE/login"

# token from 200 must work on an existing route
TOKEN=$(python3 -c "import json; print(json.load(open('/tmp/login200.json'))['token'])")
curl -sS -o /tmp/search.json -w "%{http_code}\n" \
  -H "Authorization: Bearer $TOKEN" \
  "$BASE/search?q="
# expect 400 "q must be at least 2 characters" (that is the slicer health check)
```

Reply with: the live route, example 200 JSON (redact the token), and confirm `/search?q=` with that bearer still returns the usual 400.
