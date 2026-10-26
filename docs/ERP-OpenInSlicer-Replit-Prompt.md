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

## Part 2 — STL → new MASS in the Production folder

> **Status (2026-10-26):** built on desktop branch `feature/stl-open-link`, verified on macOS
> (new project from STL, first Save creates the .mass in the chosen folder with your project/
> element ids, re-click opens the existing .mass). Not yet on `main` or tested on Windows.
> Older builds ignore these links (the app opens with no file), so nothing breaks — but don't
> announce the button until we confirm the desktop build is out.

**Goal:** a model file in LAB (STL etc.) gets an **Open in MassiveSLICER** button. The click
starts a MassiveSLICER project from that model, and Save writes the `.mass` straight into the
folder LAB chose, with no Save As dialog.

### The link

```
massiveslicer://open?path=<model, share-relative>&saveTo=<new .mass, share-relative>&project=<id>&element=<id>
```

| Param | Required | Rules |
|---|---|---|
| `path` | yes | Share-relative model file: `.stl`, `.obj`, `.3mf`, `.glb`, `.gltf`, `.step`, `.stp` |
| `saveTo` | yes for model files | Share-relative path ending `.mass`. **LAB decides the folder** (e.g. `<project folder>/06-Production Documents/<model name>.mass`) — the slicer never guesses folder conventions |
| `project` | optional | LAB project id — links the new `.mass` to the project |
| `element` | optional | LAB element id — saves then report against that element |

- `encodeURIComponent` every value. Each param at most once. Same NAS rules as `.mass` links:
  both paths must be on the share, no `..`, each machine maps them to `Z:\` or
  `/Volumes/MassiveFILES`.
- Example:
  ```js
  const q = new URLSearchParams({
    path:    "Projects/26-284 Head/03-Design/part.stl",
    saveTo:  "Projects/26-284 Head/06-Production Documents/part.mass",
    project: "42",
    element: "99",
  });
  const href = "massiveslicer://open?" + q.toString().replace(/\+/g, "%20");
  ```

### What MassiveSLICER will do

1. **If `saveTo` already exists → open that `.mass` and ignore the model.** Clicking the same STL
   twice never wipes slicing work. So the button is safe to click repeatedly.
2. Otherwise: new project, import the model (placed on the bed like Import does), set the save
   location to `saveTo`, link to `project`/`element` if given.
3. **Nothing is written until the user presses Save.** Looking and closing leaves no stray file.
4. On that first Save the `.mass` is created at `saveTo`, and LAB receives the usual
   `POST /api/slicer/v1/workspace-saves` with `path` = `saveTo` (plus `projectId`/`elementId`
   when linked). That record is LAB's signal that the MASS now exists — show it with its own
   Open button from then on.
5. The model file itself is never modified.
6. **Unsaved work is protected.** If the user has unsaved changes in another project, MassiveSLICER
   asks before discarding them (Cancel keeps them). Clicking the button for the project that is
   already open just brings the window forward.
7. Bad links (model without `saveTo`, `saveTo` not `.mass`, a parameter given twice, either path
   off the NAS) are ignored — nothing opens. Unknown extra parameters are ignored.

### Questions for you (answer when you build)

- Which folder should `saveTo` use for each project type — `06-Production Documents`, a
  `mass Files` subfolder, something else? It's entirely your call; tell us so our docs match.
- Name collisions: if two different STLs would produce the same `saveTo` name, choose distinct
  names on the LAB side (e.g. append the element name or revision).

## Known limitation

Two people can open the same `.mass` and the later Save wins. MassiveSLICER will add a
"this file changed since you opened it" warning; LAB does not need to do anything for it.

## Why not the review contract

`massiveslicer-review` (handles, resolve, immutable snapshots) is for files stored privately in
LAB that users may not be able to reach on the NAS. That is a different feature. Park it; this
button covers "open the NAS file, edit, save back".
