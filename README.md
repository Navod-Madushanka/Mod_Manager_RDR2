# Saddlebag

An RDR2 mod manager whose headline is a **verified Online mode**. Mods live in a library outside the game folder and are hard-linked in. Before Red Dead Online, the whole folder is checked against a snapshot of your own clean install.

Green means "the folder matches your baseline". It never means "safe from bans".

## Layout

```text
src/Saddlebag.Core    rules, archives, library, deployer, ledger, baseline, checks (no UI)
src/Saddlebag.Cli     Spectre.Console commands (Phase 1)
tests/Saddlebag.Tests xUnit: routing rules, deploy/online round trips, crash recovery
```

Runtime state (keep it on the game's drive, so hard links work):

```text
D:\Saddlebag\
├─ library\<mod-id>\files\   extracted mod, original layout
├─ library\<mod-id>\mod.json metadata, your overrides, routing decisions
├─ backups\                  originals a mod overwrote
├─ rescued\                  edits you made to deployed files of linked (dev) mods
└─ state.db                  SQLite: ledger, baseline, profile, journal
```

## Build

Requires the .NET 10 SDK.

```bash
dotnet test
```

```bash
dotnet publish src/Saddlebag.Cli -c Release -r win-x64 --self-contained -o publish
```

That gives one `publish\saddlebag.exe`.

## Phase 0: ground truth (on your PC)

1. Move every current mod out of the game folder into a holding folder (not inside the game folder).
2. Run your launcher's **Verify game files**.
3. Point Saddlebag at the game:
   `saddlebag init --game "D:\Games\RDR2" --home "D:\Saddlebag"`
   It tells you whether hard links work between the two folders.
4. `saddlebag baseline capture` hashes the whole install (a few minutes on an SSD). It refuses if it spots loaders or `lml/`/`scripts/`.
5. Collect about 15 real archives into `tests/fixtures/` (install.xml mods, loose stream, replace, ASI, .NET script, Script Hook, LML itself, a wrapper-folder zip, a multi-option zip). Run `saddlebag import` on each and note any destination that looks wrong.

## Everyday use

| Command | What it does |
| --- | --- |
| `install <archive>` | Import, ask about anything it can't route, enable, deploy |
| `import <archive>` / `route <mod>` | Add to the library and preview; answer or change routing (`--set SRC=DEST`, `--skip`, `--variant`) |
| `link <build folder> --name "Horse Personalities"` | Dev mode: each `deploy` copies your latest build in, only changed files |
| `plan` | Dry run of the next deploy (`--purge` for a purge) |
| `deploy` / `purge` | Sync the game folder to the enabled mods / remove every tracked file |
| `online` | Guard, purge, restore originals, verify. Exit code 0 green, 1 amber, 2 red |
| `story` | Put enabled mods back (diff only) |
| `verify [--deep]` | Compare with the baseline; `--deep` hashes every file |
| `allow <path>` / `expect <path>` | Allowed extra (amber), e.g. `x64/data/startup.meta`; file the launcher rewrites, e.g. a DLSS DLL |
| `status`, `list`, `enable`, `disable`, `remove` | Housekeeping |

## How safety works

- **Guard**: every file operation refuses while `RDR2`, `PlayRDR2`, the Rockstar launcher or its services are running.
- **Journal**: each deploy or purge is written to `state.db` as steps before anything moves. Each step's file operation and its ledger update commit together; a crash leaves a pending journal that the next command finishes. Steps are idempotent, so recovery rolls forward. Tests kill the process after every single step and check it recovers.
- **Never destroy**: a file a mod would overwrite is moved to `backups\` first and restored on purge. If you edited a deployed file (e.g. a mod's `.ini`), purge keeps your version in the library.
- **Baseline, not whitelist**: verify walks the whole tree, so files added inside vanilla folders are caught, not just root files.

## Routing

Rule ids match the build plan's table and are shown in every preview. Overrides beat every rule and are saved in `mod.json`.

| # | Signal | Destination |
| --- | --- | --- |
| 1 | Top-level `lml/` or `scripts/` | 1:1 onto the game root |
| 2 | `install.xml` at the package root | `lml/<ModName>/`, intact |
| 3 | `ScriptHookRDR2.dll`, `dinput8.dll`, `version.dll`, `vfs.asi`, `ScriptHookRDRDotNet.asi`, `ScriptHookRDRNetAPI.dll` | Root (shared component) |
| 4 | `.asi`, and files beside it | Root |
| 5 | Managed DLL referencing `ScriptHookRDR*` (read from the PE header), and files beside it | `scripts/` |
| 6 | Loose `.ytd .ydr .ydd .yft` | `lml/stream/` |
| 7 | Inside a `replace/` folder | `lml/replace/<game path>` |
| 8 | Loose `.ymt .meta .xml .dat` | Asks |
| 9 | Several option folders | Asks which variant |
| 10 | Docs and images | Not deployed |
| 11 | Anything else | Asks |

Wrapper folders are stripped first. Archive entries that point outside the mod folder (zip-slip) are rejected.

## Status against the plan

Phase 1 is in place except the items below.

Not done yet:

- Rules live in `Router.cs`, not `rules.json`.
- Rule 7's "files with a known game path" isn't implemented (needs a game-path index).
- `Documents\Rockstar Games\Red Dead Redemption 2\system.xml` isn't in the baseline yet.
- Only one profile ("default"); priority is enable order. Profiles and the full conflict and dependency checks are Phase 2.
- Only tested on Linux so far. The Windows paths (`CreateHardLinkW`, case-insensitive NTFS, launcher process names) need a run on the real install in Phase 0.

Open questions from the plan still to settle: LML Mod Manager's load-order storage, install.xml paths for RDR2, the V2 .NET API assembly name (rule 5 matches any `ScriptHookRDR*` reference, so either name works), and which store your install is from.
