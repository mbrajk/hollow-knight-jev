# Build and operate locally

This is an experimental Windows/Steam integration, not a universal mod loader. The supported vanilla game is 1.5.12620, with `Assembly-CSharp.dll` SHA-256:

```text
E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD
```

Other versions or patched assemblies are rejected. Do not replace this check to force an incompatible build. No existing mod stack is claimed compatible. The source release's native slot-ownership lifecycle has been reviewed and compiled, but still requires fresh in-game validation; fixture tests cannot substitute for it.

## Build and install

Close Hollow Knight. Assign your actual Steam game directory and Steam executable to `$game` and `$steam` in PowerShell; these are local configuration variables, not committed settings.

```powershell
./scripts/Build.ps1 -GameDirectory $game
dotnet build ./controller/JevController.csproj -c Release
./scripts/Install.ps1 -GameDirectory $game
```

Build compiles against DLLs in your own game installation. The patcher restores pinned Mono.Cecil 0.11.6 from NuGet. If necessary, supply `-CecilAssemblyPath` pointing to a trusted local compatible Mono.Cecil DLL; nothing is bundled. `-Offline` skips NuGet restore after dependencies have already been restored. `-OriginalAssemblyPath` supports staging against a locally owned, hash-matching vanilla original; installation still requires the actual game's assembly to match vanilla.

Build only writes ignored repository `build/`, `bin/` and `obj/` output. Install verifies hashes and backs up the original assembly under `build/backups/`, then installs the bridge and two audited method prefixes. It leaves STOP latched. Keep that backup and repository checkout available until you restore. Build output includes game-derived code and must not be redistributed.

To exercise install/restore checks in an ignored scratch directory without touching the actual installed game:

```powershell
./scripts/Test-Bridge.ps1 -GameDirectory $game -OriginalAssemblyPath "$game/hollow_knight_Data/Managed/Assembly-CSharp.dll"
```

Run this after Build and before Install, while the assembly is vanilla. It creates a non-executable game fixture; it never launches it or accesses saves.

## Create an owned test save

Slot 4 must be empty according to the game's native save API **and** have no local `user4*` files. No existing save is downloaded, adopted, deleted or replaced by these scripts. Preserve your own saves outside this repository before experiments.

```powershell
./scripts/Setup-Trial.ps1 -GameDirectory $game -SteamExecutable $steam -Action Create
```

The game launches through Steam with bridge observation enabled and native new-slot setup. Wait for the title setup and Godhome gameplay/entrance scene. Setup intentionally leaves normal first-run display/menu preferences alone; dismiss any such game prompts yourself if present.

Then save through the harness and quit:

```powershell
./scripts/Setup-Trial.ps1 -GameDirectory $game -Action Finish
```

Check `$game/.jev/runtime/trial-save-result.json`: both `saved` and `ownership_receipt_saved` must be true. The resulting receipt binds the verified-empty-slot origin and mode to the hash of the current `user4.dat`. This initial Finish is required for later Resume. If the native setup refuses the slot, do not bypass it by creating a receipt yourself.

## Prepare a fight

```powershell
./scripts/Setup-Trial.ps1 -GameDirectory $game -SteamExecutable $steam -Action Resume -Control -ClearStop
./scripts/Setup-Trial.ps1 -GameDirectory $game -Action EnterArena
```

Wait for the owned save to load before EnterArena. The harness enters Gruz Mother's arena and pauses when ready. Inspect fresh `state.json`: expected scene `GG_Gruz_Mother`, owned slot 4, paused, episode `running`. Then use the bounded paid wrapper described in the README.

After a run, use `Finish` to save the owned slot, refresh its ownership receipt and quit. Resume refuses a missing or mismatched receipt, including after an interrupted save or external save replacement. There is no automatic repair/adoption path; preserve the files and investigate rather than weakening the guard.

Optional longer training requires `Launch.ps1 -ResumeTrialSlot4 -Godseeker -GruzMother -BossTraining -Control -ClearStop` with the same explicit game and Steam paths, plus `Run-Trial.ps1 -AllowTraining`. Training counts hits and may replenish health. It cannot verify a hitless win. Optional `-Capture` captures only game screenshots; no desktop video recorder is included.

## Stop and restore

`Setup-Trial.ps1 -Action Stop` latches STOP and requests pause. It makes no provider request. Input leases expire even if the controller dies. Ctrl+C ends a controller or dashboard; do not leave background processes running after an experiment.

To remove the integration, close Hollow Knight and run:

```powershell
./scripts/Restore.ps1 -GameDirectory $game
```

Restore checks the installed assembly, helper, receipt and vanilla backup before changing anything. It restores the exact original assembly and removes only the matching helper/receipt. It does not delete runtime logs, screenshots or save files. If another mod or update has changed the installed files, restore refuses; handle that installation separately rather than overwriting unknown contents.
