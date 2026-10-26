# MassiveDRIVE job on the status bar

After a successful Send to MassiveDRIVE, the job id stays at the bottom-right of the status bar. The validation counts stay to its left.

## Sub-features

- The label is `Job` plus the id Drive returned (`package_id`, or `job_id` when that is what the upload answered with).
- A later validation pass, a path recalc, or a slice status does not clear it.
- A failed upload leaves the last successful id in place.
- The next successful upload replaces it.
- The robot is not started.

## How to get to it (user POV)

Select a toolpath. Send to MassiveDRIVE. When the upload finishes, the bottom-right of the window reads `Job` and the id.

## Driving it with the bridge

The label is set inside the send that uploads the open path. Do not send the curtain or any unsaved operator job to prove this. A unit test covers the label itself: `DriveJobStatusTest`.

## Gotchas

- The open app keeps the binary it launched with until quit and a rebuild.
- The id is the one in the upload response, not the local folder name, when Drive returns its own.
