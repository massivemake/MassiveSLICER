# Replit AI Prompt: "Open in MassiveSLICER" button for .mass files on the NAS

**Paste everything below the line into the Replit AI / agent.**
This replaces the `massiveslicer-review` handoff contract for this use case — see "Why not the
review contract" at the end.

---

## Goal

Next to any `.mass` file that lives on the MassiveFILES NAS, show an **Open in MassiveSLICER**
button. Clicking it opens that exact NAS file in the user's installed MassiveSLICER. They edit,
press Save, and it is written straight back to the NAS. No download, no upload.

Everyone who uses this is on the NAS already (in the shop or over VPN): Windows PCs at `Z:\`,
Macs at `/Volumes/MassiveFILES`. MassiveSLICER translates the path for the machine it runs on,
so **LAB sends one link that works on both**.

## The link

```
massiveslicer://open?path=<share-relative path, URL-encoded>
```

- **Share-relative path** = the path inside the MassiveFILES share, forward slashes, no drive
  or mount: `Projects/26-284 Head/mass Files/part.mass`.
- Encode it with `encodeURIComponent`:
  ```js
  const href = "massiveslicer://open?path=" + encodeURIComponent(sharePath);
  // massiveslicer://open?path=Projects%2F26-284%20Head%2Fmass%20Files%2Fpart.mass
  ```
- Only for `.mass` files. Only for paths on the share (`Projects/…`, `Research/…`).
  MassiveSLICER refuses `..`, absolute local paths and anything off the share.

**Where the path comes from:** you already receive it. Every save in MassiveSLICER posts
`POST /api/slicer/v1/workspace-saves` with `path` = this exact share-relative form (plus
`projectId`, `elementId`, `file`, `at`). Use the latest `path` per project/element. Files you
list from the mapped project folder can use the same form (project folder + relative path).

## Button behavior

- Render it as a plain `<a href="massiveslicer://…">Open in MassiveSLICER</a>`. No fetch,
  no API call, no token, no localhost request.
- Clicking it, the browser shows its own "Open MassiveSLICER?" prompt. If the app is not
  installed, nothing happens (the browser may show nothing at all). So:
  - Keep the existing **Download** next to it, always.
  - Small helper text: "Requires MassiveSLICER on this computer and the NAS connected."
  - Never say "opened" — you cannot know. At most "Launch requested".
- Do **not** call `127.0.0.1:8723` (MassiveSLICER's local bridge) from LAB pages. It now
  refuses browser requests except `/ping`, `/reveal`, `/open`, and this feature does not need it.

## What you do NOT need to build

No new API routes, no handoff handles, no tokens in the link, no deployment gate, no hash
checks, no file storage. The file never passes through LAB.

## How to test

1. Pick a `.mass` you can see on the NAS, e.g. `Projects/<job>/…/part.mass`.
2. Build the link above and click it on:
   - a Windows shop PC with MassiveSLICER (closed, then again while it is open);
   - a Mac with MassiveSLICER (closed, then open).
3. In MassiveSLICER: the file opens. Change something, Save. The file's modified time on the NAS
   changes, and a Save record arrives at `workspace-saves` with the same `path`.

Desktop status: macOS support for these links is on branch `feature/erp-review-launch`, verified
on a Mac (cold start, already-open, save-back). Windows has handled `massiveslicer://open` since
August; share-relative paths are new on the same branch.

## Known limitation

Two people can open the same `.mass` and the later Save wins. MassiveSLICER will add a
"this file changed since you opened it" warning; LAB does not need to do anything for it.

## Why not the review contract

`massiveslicer-review` (handles, resolve, immutable snapshots) is for files stored privately in
LAB that users may not be able to reach on the NAS. That is a different feature. Park it; this
button covers "open the NAS file, edit, save back".
