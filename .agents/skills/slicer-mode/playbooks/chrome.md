# Playbook: chrome

Theme, ComboBox, dialog corners, sidebar density. Load `sidebar-chrome`.

1. Tokens: gray `#1b1b1b` / `#222` / `#2b2b2b`, lime `#71a72a`. No purple.
2. ComboBox: `Theme="{StaticResource MassiveComboBoxTheme}"` — ControlTheme beats Style. Do not stack another template.
3. Dialogs: transparent HWND + `DialogWindowChrome.Apply`. Inner CornerRadius alone stays square on Windows.
4. Density: Live I/O stays dense (18px rows). Do not ship spacious 30px and wait to be told to cut it.
5. Blast-radius: one control theme change can purple TOOL # again. Sample the pixel after shop **Release --no-incremental**.
6. Verify with screenshot via `verify-massiveslicer` (bridge `/screenshot`). Do not claim the ring is gone from Debug on this Mac only.
7. Docs-only / copy: skip mill commit rules; still don't stash.
