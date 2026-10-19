# Data and execution boundaries

## Credentials and outbound requests

The controller reads only `TYPESAFE_API_KEY` from its process environment and sends it as an Authorization header to `https://api.typesafe.ai/v1/systemone`. The native bridge never receives the key. Request payloads contain selected game-state fields, forecast estimates and task instructions. The project does not upload saves, screenshots, arbitrary files, usernames or host telemetry to Jev.

The wrapper does not read `.env` files. Keep keys in your secret manager or local environment; do not place them on command lines or in scripts. Dependencies may require NuGet access during build. Offline controller tests, previews, replays and exports make no provider requests.

Local JSONL traces contain full observations, requests, responses, times and session identifiers; diagnostics can include local paths. Save ownership receipts contain save hashes, slot/mode and timestamps. Screenshots are opt-in. These files are private by default and excluded from the publication allowlist. Offline replay HTML still embeds decision traces even though automatic screenshot embedding is disabled.

## Spending limits

Live calls are pinned to `jev-1.13.0`. The ledger estimates input cost using $0.042 per million input tokens, reserving 65,536 tokens before each request. It settles only valid reported usage from the pinned model; unknown charges keep their reservation and stop execution. The code does not request extended context. Review these assumptions against current TypeSafe billing before enabling paid runs: this is an application estimate, not an account-level hard limit or guarantee about future prices, taxes or provider behavior.

Each wrapper run creates a unique ledger with a positive cap of at most $1. An exclusive persistent claim rejects reuse. Time and call limits also apply; automatic HTTP retries are disabled. Direct CLI trials without a ledger are restricted to 10 seconds and 24 calls and should be used only after reviewing the code. Prefer the wrapper's explicit ledger. Never launch unattended loops around paid trials.

## Local trust

The dashboard binds only to `127.0.0.1` on an OS-assigned port and checks Host and Origin. It has no mutation routes, no remote login and no public hosting support. Do not proxy or expose it to a network. Local processes running as your user can access your game/runtime files; the bridge and ledger are not a sandbox against malicious local software.

Build/installation scripts verify the supported vanilla input, audit the patch, back up the original assembly, and verify receipts before restore. They do not download game content. Native controls require an owned slot-4 session. Creation refuses occupied slot 4 or local `user4*` leftovers; resume requires a matching receipt and save hash. There is no save-adoption, save-deletion, cloud-deletion or migration workflow in the release.

Use a separate test installation and retain your own save backups. Review code changes before running them. Do not install the bridge over another mod's assembly or weaken a hash check to force compatibility. Restore refuses unexpected installed contents rather than overwriting unknown modifications.

## Reporting a problem

Report a minimal sanitized description to the repository maintainer. Do not attach credentials, saves, raw logs or captures to a public issue. If a key has been exposed, revoke it at the provider; deleting a file from the latest Git tree does not remove it from history.
