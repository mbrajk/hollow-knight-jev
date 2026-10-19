# Source release checks

Only files listed in `tools/public-files.txt` belong in the initial source release. Changes to that list require review of the entire added file. Ignore rules are a convenience, not a security boundary.

1. Run `python tools/check_release.py --working-tree` before staging. Build outputs are ignored but must never be staged.
2. Run the offline controller self-tests and budget self-test. Compile the bridge and audit a staged patch against a locally owned supported vanilla game. Never upload those outputs.
3. Independently review credentials, personal information, save handling and execution boundaries. Synthetic fixtures are shareable; real captures, traces and save receipts require a separate review.
4. Stage the explicit allowlist, then run `python tools/check_release.py`. The checker requires the tracked set to match the list and checks staged bytes, not only working files.
5. Scan the staged source snapshot with a current secret scanner such as Gitleaks, with full redaction and no inline allow-comment bypass. Inspect all findings. Opaque provider keys also warrant an exact private comparison against known values without printing them.
6. Review commit author/committer identity. Prefer a GitHub noreply address. For a first public release from private experiments, start with a fresh source-only history; never import the old game folder, archives or Git objects.
7. After committing, scan **all** refs/history and Git metadata. Confirm that the remote remains private, push, then verify its tree and commit match the reviewed local versions.
8. Public visibility is a separate maintainer decision. Do not attach binaries or experiment artifacts to releases or CI artifacts by default.

Do not claim that automated scans prove the absence of every secret or personal detail. State the reviewed scope and test limitations. No CI task should invoke paid Jev calls, start the game, modify a save, or upload generated evidence.
