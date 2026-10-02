# Silent takeover of Windows-run startup entries (reverses D2)

Status: planned (planner, 2026-10-02), branch `code-review-fixes` at 2555ac4. Q1-Q3, Q-T4 and Q-T5 answered by the user on 2026-10-02 (see Decisions). Implemented (uncommitted), reviewed in two fix rounds; the "Review fixes" sections supersede earlier text where they conflict (e.g. the prompt shows at UILevel 3-5). Manual checks M-T1 to M-T9 pending.

## Goal

Make StartupController the single control point for per-user startup. Whenever the app loads the startup list (normal UI start and `--launch` at logon), every HKCU `Run` entry that Windows would start itself is silently taken over. The app disables it in Windows (`StartupApproved\Run` = `0x03` + FILETIME, the format Task Manager writes), lists it as Enabled and appends it to the end of the saved order. From the next logon StartupController launches it in order. This reverses D2 in `docs/plans/2026-09-30-code-review-fixes.md` (user decision, 2026-10-02: "Its a hostile takeover, it should silently takeover the control on every launch, if it is enabled in registry then it taken over and enabled by default, and added last."). It changes PRD §1 (what is listed), §3 (the app now changes Windows' startup configuration) and "Registry Usage" (the app now writes `StartupApproved`). It serves PRD §4 (order), because entries Windows runs can't be ordered.

## Decisions (user, 2026-10-02)

- **D-T1 (was Q1): Gated takeover.** Take over only when "Launch programs on startup" is on **and** the app's own Run entry exists **and** it is enabled in Windows. Otherwise nothing is taken over (one Info line).
- **D-T2 (was Q2): Hidden stored names keep their old position** and become Enabled with a fresh fingerprint. Only names the app has never stored go last.
- **D-T3 (was Q3): No in-app "Return to Windows" action. The uninstaller offers it.** User: "Should add it as an option to the installer, upon uninstall it should ask if they should all be enabled and run by windows instead." The app records every name it takes over in `HKCU\Software\StartupController\TakenOverPrograms`. On uninstall a Yes/No prompt asks whether to return them. Yes writes `StartupApproved\Run\<name>` = `02` + zero FILETIME for each recorded name still in Run. No leaves them as they are. Entries the user had disabled in Task Manager before the takeover are never recorded, so they are never re-enabled. Design: see "Uninstall: return to Windows".
- **D-T4 (was Q-T4): Yes returns every recorded entry.** User: "by uninstalling StartupController, we should ask the user if they want all taken over apps to be enabled, and started up by windows." On Yes, every name in `TakenOverPrograms` that still exists as a string value in Run is set to enabled. There is no check of the app's enabled flag, the fingerprint or the current `StartupApproved` bytes.
- **D-T5 (was Q-T5): The MSI patch runs only as the setup project's `PostBuildEvent`** (the user builds the MSI in Visual Studio when needed). There is no release-checklist step. M-T1 is a one-time check of the first patched build. The post-build step must fail the build loudly and must not leave an unpatched MSI behind.

## Current behavior

- `StartupRegistryService.GetStartupPrograms` (`StartupController/StartupRegistryService.cs:45-104`) skips the app's own entry (57-61), entries with no or empty approved value (63-68) and Windows-enabled entries (73-77), logging "Skipping '<name>': ... Windows runs it". Non-string Run values are skipped after that (80-85). The rest is merged with the stored order by `OrderMerger.Merge` (`OrderMerger.cs:14-56`). New names go last and are **disabled**.
- `StartupApprovedState.IsEnabled` (`StartupApprovedState.cs:7-15`): null/empty or even first byte = Windows runs it.
- `SaveStartupOrder` (`StartupRegistryService.cs:108-123`) writes `EnabledPrograms`, `EnabledFingerprints`, then `ProgramOrder` last. It goes through `OrderMerger.MergeForSave`, which takes flags from the displayed snapshot (so a `Changed` row is saved as disabled).
- Nothing writes `StartupApproved`. `StartupApprovedWriteGuardTests` asserts this with a source scan and a byte-identical Run/StartupApproved dump after list/save/launch cycles.
- Load path: `Form1.RunStartupAsync` (`Form1.cs:363-377`) → `StartupSession.LoadProgramsAsync` (`StartupSession.cs:51-68`, runs `registry.GetStartupPrograms` on a background task) → in `--launch` mode `StartupSession.RunLaunchModeAsync(_runner, _model.EnabledPrograms(), ...)`.
- D8: `Program.DecideStartup` (`Program.cs:107-140`) exits before any load on a second `--launch` in the same logon session (`ExitAlreadyLaunched`), so a takeover never runs then.
- "Launch programs on startup" (`SettingsController.ApplyLaunchProgramsOnStartup`, `SettingsController.cs:30-49`) adds or removes the app's own Run value `StartupController` (`AddThisApplicationToStartup`, `StartupRegistryService.cs:272-280`).

## Design

New method `IStartupRegistry.TakeOverWindowsEntries(bool launchSettingOn)` → `IReadOnlySet<string>` (names taken over this call, OrdinalIgnoreCase). `StartupSession.LoadProgramsAsync` calls it in the same background task, right before `GetStartupPrograms()`. `GetStartupPrograms` keeps its read-only listing semantics (its 45 test call sites are unchanged). After the takeover the entries read as `0x03`, so the normal listing picks them up. `StartupSession` then sets the transient `StartupProgram.TakenOver = true` on the returned programs whose names are in the set.

### Algorithm (`StartupRegistryService.TakeOverWindowsEntries`)

1. **Gate (D-T1):** return an empty set, with one Info line, unless `launchSettingOn` is true **and** the app's own Run value exists **and** its own `StartupApproved` value is Windows-enabled (`IsEnabled`). Otherwise taken-over programs would never start: no `--launch` happens at logon. If the caller can't read the setting, it passes `false`.
2. Open `Run` and `StartupApproved\Run` read-only. If `Run` is missing, return an empty set. One pass over `runKey.GetValueNames()`:
   - Skip the own entry (`STARTUP_CONTROLLER_NAME`, OrdinalIgnoreCase). Never write anything for it.
   - `approved = approvedKey?.GetValue(name) as byte[]`. A wrong-kind value gives null, which means "Windows runs it". That matches today's listing.
   - Read the value once with `ReadRunValue`. Non-string, null or deleted → not a candidate (item 5). Otherwise compute `RunFingerprint.FromRunValue`.
   - If `StartupApprovedState.IsEnabled(approved)`, it is a **candidate** (name and fingerprint), unless the name is longer than `MAX_NAME_LENGTH` (it could not be stored, so it would end up disabled in Windows and never launched). Log a Warning and skip it.
   - If it is Windows-disabled, remember its fingerprint in `currentFingerprints` (used for migration in step 3).
3. If there are no candidates, return an empty set. **Nothing is written** (keeps the "nothing written on load" property in the normal case). Otherwise build `next` from `previous = LoadStoredOrder()`, **not** through `MergeForSave`. Other names keep exactly their stored flag and fingerprint, so a silent save can't turn `Changed` rows into Disabled:
   - Order: `previous.Order`. A candidate already in it (hidden name, item 3) keeps its position. New candidates are appended in Run enumeration order. Stop appending at `MAX_NAMES`. Names over the cap are not candidates: log one Warning and don't take them over.
   - Enabled: `previous.Enabled` ∪ candidates.
   - Fingerprints: `previous.Fingerprints`, with each candidate's fingerprint set or replaced by its current one. **Migration:** if `!previous.FingerprintsKnown`, also add `currentFingerprints[n]` for every `n` in `previous.Enabled` that has no stored fingerprint and is in `currentFingerprints`. Without this, the first `EnabledFingerprints` write would turn migrating names into `Changed`. This is the same set a UI save records today.
   - `fingerprintsKnown: true`.
   - Read the clock **once** per call and build `bytes = StartupApprovedState.Disabled(utcNow)`. Every candidate in this call gets the same 12 bytes.
   - Takeover record (D-T3): `TakenOverPrograms` from `LoadTakenOver()`, plus each candidate name not already in it (OrdinalIgnoreCase, capped at `MAX_NAMES`). The value holds names only: after D-T4 the uninstaller needs no marker bytes. If the existing value has the wrong kind, log a Warning and **abort the takeover** (return an empty set, write nothing). The app must never disable an entry it can't record for the uninstaller.
4. **Write the order first:** extract the write part of `SaveStartupOrder` into `private void WriteStoredOrder(StoredOrder toWrite)` (Cap, then `EnabledPrograms`, `EnabledFingerprints`, `ProgramOrder`). `SaveStartupOrder` calls it after `MergeForSave`, and the takeover calls it with `next`. If it throws, log an Error ("Takeover skipped: the order could not be saved; Windows keeps starting N program(s)") and return an empty set. Nothing is written to `StartupApproved`.
   Then write `TakenOverPrograms` (REG_MULTI_SZ, through `WriteMultiString`). If that throws, log an Error and return an empty set. Nothing is written to `StartupApproved`, and the candidates are stored Enabled but hidden, which is harmless (see below).
5. **Then disable in Windows:** `_root.CreateSubKey(STARTUP_APPROVED_KEY, writable: true)` (the key may be missing on a fresh profile). For each candidate, call `WriteApprovedDisabled(key, name, bytes)`. This is a new `internal virtual` seam that writes the `bytes` from step 3 as `REG_BINARY`: `{0x03,0,0,0}` + `BitConverter.GetBytes(utcNow.ToFileTimeUtc())`, 12 bytes with a little-endian FILETIME. The clock is an injected `Func<DateTime>` (default `DateTime.UtcNow`) so tests can pin the bytes. A failure for one entry logs a Warning ("Could not take over '<name>'; Windows keeps starting it") and the loop continues. If `CreateSubKey` fails, log one Warning; nothing is taken over.
6. On success, log one Info per entry: `Took over '<name>' from Windows startup; StartupController launches it from the next logon`. Add it to the result set.

### Why order first, with no rollback (item 4)

Write order: `EnabledPrograms`, `EnabledFingerprints`, `ProgramOrder`, `TakenOverPrograms`, then `StartupApproved`. If the order is saved and the `StartupApproved` write then fails (or the process dies between the two), the name is stored Enabled but stays Windows-enabled. D4 hides it and it is not launched by the app, so Windows keeps starting it. There is no double launch and no "never starts". The next load retries the takeover (it is a candidate again, keeps its position and gets a fresh fingerprint). The dangerous state (disabled in Windows, not enabled in the app) can't happen, so no rollback code is needed. A partial order write (for example `EnabledPrograms` written, `EnabledFingerprints` throws) leaves a candidate enabled without a fingerprint. That is hidden and harmless, and fail-closed (`Changed`) if it is ever listed.

*Rejected:* write `StartupApproved` first, then save the order, with rollback (restore the original bytes or delete the value) if the save fails. The rollback can itself fail and leave exactly the dangerous state. It also needs per-entry original-value bookkeeping.

The takeover record is written before `StartupApproved` for the same reason: every entry the app disables is already recorded. A recorded name whose `StartupApproved` write then failed is harmless. Windows still runs it, and returning it at uninstall just writes the enabled value again.

### Hidden stored names (item 3), decided (D-T2): keep position, force Enabled, refresh fingerprint

A name already in `ProgramOrder` but hidden because Windows runs it (an app or the user re-enabled it in Task Manager, or something deleted its approved value) keeps its stored position. It becomes Enabled with its **current** fingerprint, even if it was stored Disabled or with an old fingerprint. Justification:
- **Position:** the stored position is an explicit user choice. "Last" is only a default for names the app has never seen. Sending a known name to the end would silently scramble the order every time an updater re-enables itself.
- **Enabled:** Windows was starting it, so its effective state was "runs". Taking it over as Disabled would silently stop a program from starting, the very state item 4 forbids.
- **Fingerprint:** D7 guards against a Run command the user approved being changed behind their back. Here Windows was already running the current command, so adopting it adds no exposure.
- This deviates from a literal "added last" only for names the app already knows. The user confirmed it (D-T2).

### Double launch on the first logon (item 1)

In `--launch` mode Explorer processes the Run key in one pass, including StartupController's own entry. By the time the app has started, passed D8 and loaded, Explorer has almost certainly already started (or is starting) the other entries. So entries taken over in a load are **not launched in that `--launch` run**: `StartupSession.LaunchableAtLogon(programs)` returns `EnabledPrograms().Where(p => !p.TakenOver)`, and `Form1.RunStartupAsync` passes that to `RunLaunchModeAsync`. They are launched from the next logon (D8 allows exactly one `--launch` per logon session, so there is no second chance this session anyway). A normal UI start never launches automatically, so it needs no change. The manual Launch button still works for taken-over rows. Accepted residual: if the app wins the race against Explorer, an entry misses one logon (a single miss, never a double start). The takeover log line says "from the next logon", so this is visible.

### Rejected alternative: takeover inside `GetStartupPrograms`
That would avoid the second Run read, but it turns a read into a writer, changes all 45 existing call sites' semantics, and mixes failure handling into the listing. The race between the two reads (Run data changed in between) only leads to `Changed` (fail-closed).

### Uninstall: return to Windows (D-T3)

**Installer technology.** `SetupStartupController/SetupStartupController.vdproj` is a Visual Studio Installer Projects MSI. It has `InstallAllUsers = FALSE`, installs to `[ProgramFiles64Folder][ProductName]`, has `RemovePreviousVersions = TRUE` (a major upgrade uninstalls the old version), an empty `"CustomAction"` section (line 98) and an empty `PostBuildEvent`. Custom logic can only go in a vdproj **Uninstall custom action**: an exe installed with the product, with an `Arguments` string and a `Condition`. Two vdproj behaviours shape the design:
- vdproj marks its custom actions deferred and `NoImpersonate` (msidbCustomActionTypeNoImpersonate, 0x800). They then run as LocalSystem, whose HKCU is not the user's hive. This action **must impersonate** the uninstalling user, so a post-build step patches the MSI.
- An exe custom action that exits non-zero, or fails to start, fails the uninstall and rolls it back. The same patch sets msidbCustomActionTypeContinue (0x40) so the return code is ignored, and the helper always exits 0 as well.

**Helper.** Add a new project `StartupController.Uninstall` (WinExe, so no console window). Its output is `StartupController.ReturnToWindows.exe`, with `TargetFrameworks` `net462;net10.0-windows`. The installer ships the **net462** build. .NET Framework 4.6.2+ is part of every supported Windows (10 1607 and later) and can't be uninstalled, so the helper still runs if the .NET 10 runtime was removed first. The `net10.0-windows` build exists only so `StartupController.Tests` can reference the logic. The code must compile on both targets: no `Convert.ToHexString`, no `FrozenSet`.
- *Rejected:* `StartupController.exe --return-to-windows`. If the .NET 10 runtime is missing, the apphost shows a blocking ".NET required" dialog, even in a silent uninstall, and fails.
- *Rejected:* a VBScript custom action. VBScript is deprecated in Windows 11 and is being removed.
- *Rejected:* a NativeAOT helper. It needs the C++ build tools on the build machine.
- *Rejected:* a .NET Framework `Installer` class (InstallUtil). It is harder to test and has hosting quirks.

**Custom action.** In the vdproj Uninstall custom actions, add the helper with:
- `Arguments` = `--uilevel [UILevel] --choice "[RETURNTOWINDOWS]"`
- `Condition` = `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`

Without the `NOT UPGRADINGPRODUCTCODE` part, every major upgrade would prompt and return all entries.

`RETURNTOWINDOWS` is a new public property, for scripted uninstalls: `1` = return without asking, `0` = leave without asking, empty = decide by UI level.

**MSI patch (D-T5).** A new script, `tools/Patch-UninstallCustomAction.ps1 -Msi <path>`, is the setup project's only patch step. It runs as the vdproj `PostBuildEvent`, for example `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "$(ProjectDir)..\tools\Patch-UninstallCustomAction.ps1" -Msi "$(BuiltOuputPath)"` (vdproj spells the macro `BuiltOuputPath`), with `RunPostBuildEvent` left at "on successful build". The script:
- opens the MSI through the `WindowsInstaller.Installer` COM object, finds the helper's `CustomAction` row by its source file key, clears 0x800 (NoImpersonate) and sets 0x40 (ignore exit code), and commits;
- re-opens the MSI read-only and verifies the new `Type` and the `Condition` (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`);
- uses `$ErrorActionPreference = 'Stop'` with a `try`/`catch` around everything. On **any** failure (no row, more than one row, COM error, failed verification) it **deletes the built MSI**, writes `error : Patch-UninstallCustomAction: <reason>` (MSBuild error format, so Visual Studio shows it in the Error List) and exits 1, which fails the build. An unpatched MSI is never left in the output folder.
- has a `-Verify` switch that only checks, for M-T1.

An unpatched MSI is unsafe: the action would run as SYSTEM, find nothing to return, and a non-zero exit code could block the uninstall.

**Decision logic** (`UninstallDecision.Decide(uiLevel, choice)`, pure):

| Input | Result |
|---|---|
| `choice` = `1` | Return, no prompt |
| `choice` = `0` | Leave, no prompt |
| Any other non-empty `choice` | Treated as empty, with a Warning |
| `choice` empty, `UILevel` exactly 3, 4 or 5 (basic, reduced, full; uninstall from Settings > Apps is expected to be basic or reduced UI, record it in M-T2) | Prompt Yes/No (120 s timeout; skipped as SYSTEM, in session 0 or when not interactive) |
| `choice` empty, `UILevel` 2 (`/qn`), a value with flag bits (such as `/passive`), or missing or unparsable | **Return, no prompt** |
| Prompt answered No | Leave |
| Prompt answered Yes, timed out, failed, threw, or skipped | Return |

The prompt is `MessageBoxW` with MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND and the title "StartupController". Its text: "StartupController took over N startup program(s) from Windows. Do you want all of them to be enabled and started by Windows again? Yes: all taken-over programs are enabled and Windows starts them at sign-in. No: they stay disabled and won't start." There is no prompt when N = 0. A failed MessageBox counts as No.

**Silent default = Return (justification).** After uninstall nothing launches the taken-over entries. Leaving them disabled in a silent uninstall would stop every startup program the app took over, and nobody would be there to notice. Returning them gives the same result as a Yes (D-T4): Windows starts every taken-over program again, which is the state before the takeover. Entries the user disabled in Task Manager before the takeover are never recorded, so they stay disabled. Admins who want the opposite pass `RETURNTOWINDOWS=0`.

**Return logic** (`ReturnToWindows.Run(RegistryKey root, IUninstallLog log)`, testable against a sandbox root). For each name in `TakenOverPrograms` (D-T4):
- If the name still exists in `Run` with a string value (REG_SZ or REG_EXPAND_SZ), it is returned. Otherwise it is kept and logged (`not in Run`, `not a string`).
- The own entry `StartupController` is never touched, even if a hand-edited `TakenOverPrograms` lists it.
- There are deliberately **no** checks of the app's enabled flag, the D7 fingerprint or the current `StartupApproved` bytes.

Accepted consequences of D-T4 (document them in the README and SECURITY.md):
- A taken-over program the user later switched off in the app, re-disabled in Task Manager, or whose command changed (`Changed`, D7) is enabled again.
- An entry that is already enabled in Windows is just written again.
- Only entries Windows was running at takeover time are ever recorded, so an entry the user disabled before the takeover is never touched.

For a returned name, write REG_BINARY `02 00 00 00 00 00 00 00 00 00 00 00` and remove it from `TakenOverPrograms`. Kept and failed names stay. The helper never deletes approved values. It never writes `Run`, `ProgramOrder`, `EnabledPrograms` or `EnabledFingerprints`. `HKCU\Software\StartupController` stays in place (uninstall doesn't remove user data today).

**Failure handling.**
- Every registry operation is in a try/catch per entry. A missing `Software\StartupController`, `Run` or `StartupApproved\Run` key, or a missing or wrong-kind `TakenOverPrograms`, is logged and means "nothing to return".
- If rewriting `TakenOverPrograms` fails after the approved writes, log it. A stale name is harmless: a later run writes the same enabled value again.
- `Main` catches everything and exits 0.

**Logging.** Append to `%LOCALAPPDATA%\StartupController\logs\uninstall.log` (impersonated, so the user's folder). It is a separate file, so the helper doesn't depend on `LoggingService`. Format: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] text`. Names are escaped with the same rules as `LoggingService.Escape`, extracted into a shared `LogEscape.cs` linked into the helper. Lines:
- one session header (helper version, UILevel, choice);
- the decision (`Prompted: Yes`/`No`, `Silent: Return`, `Property: Leave`);
- one line per name (`Returned 'X' to Windows` or `Kept 'X': <reason>`);
- a summary `Returned n of m`.

Names only, never commands, paths or byte values. A log that can't be written is ignored.

**Scope limits (documented, accepted).**
- Only the uninstalling user's HKCU is processed. Other users of the app on the same machine keep their disabled entries (README: enable them in Task Manager).
- An over-the-shoulder UAC uninstall with a different admin account impersonates that admin and finds nothing to return.
- vdproj is expected to sequence Uninstall custom actions before `RemoveFiles`, so the helper still exists when it runs. Verify in M-T2.

## Registry impact

All HKCU. No admin for the app. The uninstaller runs elevated (per-machine MSI), but its custom action is patched to impersonate the user and only touches that user's HKCU. HKLM `Run`, `WOW6432Node\Run`, `StartupApproved\Run32` and `StartupApproved\StartupFolder` are not read or written.

| Key / value | Access | Notes |
|---|---|---|
| `HKCU\...\CurrentVersion\Run` | read; own entry write as today | Takeover never writes Run. |
| `HKCU\...\Explorer\StartupApproved\Run\<name>` | **new write**: REG_BINARY 12 bytes `03 00 00 00` + FILETIME | Only for takeover candidates, never the own entry. It overwrites a wrong-kind, empty, all-zero or even-first-byte value. Creates the key if it is missing. Never deletes. The uninstall helper writes `02` + 11 zero bytes for every recorded name still in Run as a string, on Yes or in a silent uninstall (D-T3, D-T4). |
| `HKCU\Software\StartupController\TakenOverPrograms` (**new**, REG_MULTI_SZ) | written by the takeover, before `StartupApproved`. Read and pruned by the uninstall helper | One name per taken-over entry. No commands, no paths, no bytes. A UI save never touches it. Wrong kind: the takeover aborts (fail closed). |
| `HKCU\Software\StartupController\EnabledPrograms`, `EnabledFingerprints`, `ProgramOrder` | written on load **only when there are candidates** | Same write order as today. Existing data is kept: other names keep their flags and fingerprints. A legacy/migrating store gets its fingerprints recorded (as a UI save would do). `StartupOrder` (legacy) is untouched. |

Migration: no format change. The first run after upgrade takes over every Windows-run HKCU entry (subject to the gate). Downgrading to an older build leaves those entries disabled in Windows. The older build lists them and keeps them enabled from `EnabledPrograms`, so they still launch.

## Steps

1. `StartupProgram.cs`: add `public bool TakenOver { get; set; }`. It is transient and never stored. `OrderMerger.Copy` doesn't need to carry it (it is set after the merge).
2. `IStartupRegistry.cs`: add `IReadOnlySet<string> TakeOverWindowsEntries(bool launchSettingOn);` with a comment on the write order. Update the test fakes that implement `IStartupRegistry` (grep `: IStartupRegistry` in `StartupController.Tests`).
3. `StartupRegistryService.cs`:
   - Add a constructor overload with `Func<DateTime> utcNow` (keep `(RegistryKey root)`).
   - Extract `WriteStoredOrder` from `SaveStartupOrder`.
   - Add `TakeOverWindowsEntries` (algorithm above), plus `BuildTakeoverOrder(StoredOrder previous, candidates, currentFingerprints)` as `internal static` so it can be unit-tested without the registry, and the virtual seam `WriteApprovedDisabled`.
   - Update the comment at lines 41-44: Windows-run entries are listed only after they are taken over. Keep the existing "Skipping ... Windows runs it" lines: they still apply when the gate is off or a takeover failed.
4. `StartupApprovedState.cs`: add `internal static byte[] Disabled(DateTime utc)` (the 12-byte value), so the format lives next to the parity rule.
5. `StartupSession.cs`: `LoadProgramsAsync(..., bool takeOver)`. Inside the background lambda, call `TakeOverWindowsEntries(takeOver)` in its own try/catch (log Error, continue with an empty set; a failed takeover must never fail the load), then `GetStartupPrograms()`, then mark `TakenOver`. Add `LaunchableAtLogon`.
6. `Form1.cs` `RunStartupAsync`: compute `takeOver` from `_settings.GetLaunchProgramsOnStartup()` in a try/catch (false on error). Pass it in, and pass `StartupSession.LaunchableAtLogon(_model.EnabledPrograms())` to `RunLaunchModeAsync`. Update `HelpText` (580-603).
7. Tests (below), including the revised `StartupApprovedWriteGuardTests`.
8. `StartupRegistryService.cs`: add `TAKEN_OVER_VALUE = "TakenOverPrograms"`, `LoadTakenOver()`, and the record write in the takeover (algorithm steps 3-4).
9. Shared format: the enabled value (`02` + 11 zero bytes) goes in `StartupApprovedState.cs`, and the `TakenOverPrograms` value name and cleaning rules go in a new `StartupController/TakeoverRecord.cs`. Write both as net462-compatible code and link them into the helper project, so the app and the uninstaller can't drift.
10. Extract `LoggingService.Escape` into `StartupController/LogEscape.cs` (net462-compatible) and link it into the helper. `LoggingService` delegates to it, so behaviour is unchanged.
11. New project `StartupController.Uninstall` (net462;net10.0-windows, WinExe):
    - `Program.Main`: args parsing, a top-level catch, always exit 0;
    - `UninstallDecision`, `ReturnToWindows`, `UninstallLog`;
    - a `MessageBoxW` P/Invoke behind an `IPrompt` seam.

    Add the project to `StartupController.sln`. `StartupController.Tests` references its net10 build.
12. `SetupStartupController.vdproj`:
    - add the net462 helper output to the application folder;
    - add the Uninstall custom action with the arguments and condition above;
    - add the `RETURNTOWINDOWS` property;
    - set `PostBuildEvent` to the patch command above.
13. `tools/Patch-UninstallCustomAction.ps1`: patch, verify, and on any failure delete the MSI and exit 1 (see "MSI patch"), plus the `-Verify` switch.
14. `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`. Build the MSI in Visual Studio (the `PostBuildEvent` patches it), then the manual checks M-T1 to M-T9.

## Test plan (RegistrySandbox and fakes only; never the real HKCU, never real launches)

Takeover basics (`TakeoverTests.cs`, new; fixed clock):
- No approved value, empty `byte[0]`, `02`+8 zero bytes, all-zero 12 bytes, `06`, single byte `02`, wrong-kind approved (REG_SZ, REG_DWORD) → each taken over. Approved is exactly `03 00 00 00` + FILETIME of the clock (REG_BINARY). It is listed Enabled, appended last in Run enumeration order, `EnabledFingerprints` has `name|<hash of current data>`, and the result set contains it.
- Already disabled (`03`, `01`, `07`, the 12-byte Task Manager value) → approved bytes unchanged, behaviour as today (stored flag/fingerprint rules).
- Own entry `StartupController` (and `startupcontroller`, case) with no approved value, `02` or `03` → never written, never listed.
- Non-string Run values (REG_DWORD, REG_BINARY, REG_MULTI_SZ) with no approved value → not taken over, approved not created, not listed.
- Quoted path with arguments (`"C:\Apps\B b.exe" --min`) and REG_EXPAND_SZ `%ProgramFiles%\C\C.exe` → fingerprint is over raw unexpanded data plus kind (matches `RunFingerprint.Compute`). The Run value is byte-identical afterwards.
- `StartupApproved\Run` key missing entirely → created, value written.
- `Run` key missing → empty set, nothing created.
- Name of 261 characters → not taken over, Warning, approved untouched. Store at `MAX_NAMES` → extras not taken over, Warning.
- No candidates → `Software\StartupController` values byte-identical (nothing written on load).
- Second call → empty set, no writes (idempotent).
- Gate (D-T1): setting off; own Run entry missing; own entry disabled in StartupApproved (`03`) → nothing written, one Info line.

Order and fingerprints:
- Stored `A,B` (A enabled with fingerprint, B disabled), new Windows-run `N` → `ProgramOrder` = `A,B,N`, `EnabledPrograms` = `A,N`, A's fingerprint unchanged.
- Hidden stored `X` at position 2 of `A,X,B` (stored Disabled, or Enabled with an **old** fingerprint), X Windows-run → X stays at position 2, is Enabled, has the new fingerprint, and is listed Enabled (not `Changed`).
- `Changed` row `C` (enabled, mismatched fingerprint) plus a takeover in the same load → C is still in `EnabledPrograms` with its **old** fingerprint, and still `Changed` after reload (no silent downgrade).
- Legacy migration: only `StartupOrder = "A;B"` (A, B Windows-disabled) and a Windows-run `N` → after the takeover `EnabledFingerprints` has A, B and N. A reload lists A, B, N all Enabled (not `Changed`). `StartupOrder` is unchanged.
- Pre-D7 v2 store (no `EnabledFingerprints`) → same as legacy.

Failure handling (seams on a `StartupRegistryService` subclass):
- `WriteMultiString` throws on `EnabledPrograms` → empty set, approved untouched for all candidates, Error logged, and `GetStartupPrograms` still works (candidates not listed).
- Throws on `EnabledFingerprints` on the first-ever save → `ProgramOrder` not written, approved untouched.
- `WriteApprovedDisabled` throws for `N2` only → `N1` taken over. `N2` is stored Enabled, stays Windows-run, is not listed and not passed to the launcher. On the next call (seam fixed) `N2` is taken over, keeps its position and is listed Enabled.
- `TakeOverWindowsEntries` throws (fake registry) → `LoadProgramsAsync` still loads the list, logs an Error, returns true.
- Entry removed externally between the takeover and the listing (seam deletes the Run value) → no exception. The stored name is kept hidden (D4). The orphan approved value is left alone.
- Run data changed between the takeover read and the listing read → listed `Changed`, not launched.

Launch (fake launcher, `StartupSession` tests):
- `--launch` load with a takeover of `N` and an existing enabled `A` → only `A` is handed to `LaunchSequenceAsync`. `N` is `TakenOver`, Enabled in the model.
- Next simulated logon (new service/model, no takeover) → `A`, `N` launched in stored order.
- Manual Launch of a `TakenOver` row → launched.
- `ExitAlreadyLaunched` (D8) → no takeover (assert via the `Program.DecideStartup` seam that no load happens; existing tests cover it).

Logging:
- Exactly one Info line per taken-over entry, containing the name (escaped per the Phase 4 rules: test a name with CR/LF and `|`) and no part of the command. The failure Warning and Error lines also contain no command text.

`StartupApprovedWriteGuardTests` (revise; keep the guard strict):
- Source scan: `FindApprovedWrites` finds exactly **one** violation in production, "creates/opens StartupApproved for writing" in `StartupRegistryService.cs`, inside `TakeOverWindowsEntries`. Zero in every other file. No `DeleteSubKey`/`DeleteSubKeyTree` on it. Add rules for `DeleteValue` within `StartupRegistryService.cs` (allowed only for `STARTUP_CONTROLLER_NAME` on `RUN_KEY`) and for `SetValue` with a non-`Binary` kind on the approved key.
- Keep `ProductionCode_MentionsStartupApprovedOnlyInTheRegistryService` and `ProductionCode_HasNoSetProgramEnabled`.
- New scan: `RUN_KEY`/`RunKey` is opened writable only in `AddThisApplicationToStartup` and `RemoveThisApplicationFromStartup`.
- Rewrite `ListSaveLaunchCycles_...` (same seed, fixed clock, `TakeOverWindowsEntries(true)` before each listing). Run is byte-identical after 3 cycles. In StartupApproved, only `WinRuns02` and `NoValue` changed, to exactly `03 00 00 00` + the clock FILETIME. `A`, `B`, `C`, `StartupController` and `Orphan` are byte-identical. A seeded REG_DWORD Run entry with no approved value has no approved value. The dump after cycle 1 equals the dump after cycle 3 (no repeat writes).
- Add a variant with `TakeOverWindowsEntries(false)` → both dumps byte-identical (the old guarantee).

Takeover record (`TakeoverTests.cs`):
- Takeover of `N1`, `N2` → `TakenOverPrograms` = `N1`, `N2`. Already-disabled entries, the own entry and non-string entries are **not** recorded.
- Retake-over of a recorded name (approved reset to `02` externally) → still listed once (no duplicate, case-insensitive).
- The `TakenOverPrograms` write throws → empty set, `StartupApproved` untouched.
- `TakenOverPrograms` has the wrong kind (REG_SZ) → takeover aborted, nothing written anywhere, Warning logged.
- A UI save (`SaveStartupOrder`) leaves `TakenOverPrograms` byte-identical.
- Format parity: names written by the app (including `|` and CR/LF) are read back unchanged by the helper's `TakeoverRecord` code.

Uninstall helper (`ReturnToWindowsTests.cs`, sandbox root, fake `IPrompt`, no process start, no real MessageBox):
- `UninstallDecision.Decide`: choice `1` → Return; `0` → Leave; `x` → as empty plus a Warning; empty + UILevel 2 → Return; 3, 4, 5 → Prompt; missing or `abc` → Return. The prompt result Yes → Return, No → Leave, and an exception → Leave. N = 0 → no prompt. The prompt text says that all N taken-over programs will be enabled and started by Windows.
- Return, D-T4: recorded `A` (enabled in the app), `E` (disabled in the app), `F` (Run command changed), `C` (re-disabled in Task Manager, different FILETIME), `D` (approved value deleted) and `X` (approved already `02`), all still string values in Run → each one's approved value = `02` + 11 zero bytes (REG_BINARY) and each removed from `TakenOverPrograms`. `Run`, `ProgramOrder`, `EnabledPrograms` and `EnabledFingerprints` are byte-identical. A REG_EXPAND_SZ Run value counts as a string.
- Kept, each logged with its reason, approved unchanged, the name kept: `B` removed from Run, `G` changed to a REG_DWORD Run value.
- Pre-takeover disabled entry `P` (`03` + some FILETIME, never recorded) → untouched, with and without a `TakenOverPrograms` value.
- Missing `Software\StartupController`, missing `StartupApproved\Run`, wrong-kind `TakenOverPrograms`, empty or whitespace names, over-long names → no exception, nothing written for those, logged.
- `StartupController` listed in `TakenOverPrograms` (hand-edited) → never written.
- The approved write throws for one name (seam) → the others are returned, and the failed one keeps its line.
- Rewriting `TakenOverPrograms` throws → the approved writes stay and the error is logged. A second run returns nothing and writes nothing.
- Log: the session header, one line per name, the summary, names escaped, no command text. Log directory not writable → no exception.
- `Program.Run(args, root, prompt, log)` returns 0 on every path, including an injected exception.

`StartupApprovedWriteGuardTests` also scans the helper sources. The helper may write `StartupApproved` only in `ReturnToWindows`, with REG_BINARY only and no deletes. It never opens `Run` or `Software\StartupController` writable, except for the `TakenOverPrograms` rewrite.

Patch script (Pester or a PowerShell-invoking xUnit test is **not** required). Exercise it once by hand on a copy of the built MSI: with the helper row removed → exit 1, the `error :` line, and the MSI deleted. With a read-only MSI → the same.

Manual installer checks (test VM or throwaway Windows user only; never this machine's real startup configuration):
- **M-T1 (one-time, first patched build):** open `SetupStartupController.vdproj` in Visual Studio and check that the helper action appears under **Custom Actions > Uninstall** (not Install, Commit or Rollback), with the Arguments and Condition from the plan. Add a throwaway Uninstall custom action in the VS editor and compare its vdproj block with the hand-written one (section GUID `{4AA51A2D-7D85-4A59-BA75-B0809FC8B380}`, `"InstallAction" = "3:8"`, `"FileType"`, `"Identifier"`), then remove the throwaway action. Build the MSI in the **Release** solution configuration (the setup project builds only there, and it packages `StartupController.Uninstall\bin\Release\net462\StartupController.ReturnToWindows.exe`; check the File entry still points there). After the build, `Patch-UninstallCustomAction.ps1 -Verify` (or Orca) shows the helper action without 0x800, with 0x40 and 0x400, without 0x100/0x200, sequenced in InstallExecuteSequence between InstallInitialize and InstallFinalize and before RemoveFiles, and with the condition `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`. Also break the patch on purpose (rename the helper file key) → the build fails in the Error List and no MSI is left in the output folder.
- **M-T2:** take over several entries, then switch one off in the app and re-disable another in Task Manager. Uninstall from Settings > Apps. Uninstall **non-elevated** (the helper writes no `uninstall.log` while its token is elevated or SYSTEM, round 2 N1); if the uninstall can only run elevated, check the result in Task Manager instead. First record the UILevel in the `uninstall.log` header: UILevel 3, 4 and 5 prompt (round 2 N7). If Settings > Apps runs at UILevel 2 or with flag bits, there is no prompt and the silent default (Return) applies; report that to the user. With UILevel 3-5 → one prompt whose text says all taken-over programs will be enabled and started by Windows. Yes → Task Manager shows **all** taken-over entries Enabled, including the two changed ones. An entry disabled in Task Manager **before** the takeover stays Disabled. `uninstall.log` is in that user's `%LOCALAPPDATA%`, which proves impersonation and that the helper still existed.
- **M-T3:** same with No → entries stay Disabled, and the log says Leave.
- **M-T4:** `msiexec /x <product> /qn` → no prompt, entries returned. With `RETURNTOWINDOWS=0` → none returned, and with `=1` under full UI → no prompt, returned.
- **M-T5:** `/qb` and `/passive` → record whether the prompt appears, and confirm that `RETURNTOWINDOWS` avoids it.
- **M-T6:** a major upgrade over an installed build → no prompt and nothing returned. The taken-over entries stay Disabled and are still launched by the app.
- **M-T7:** uninstall after removing the .NET 10 runtime → the helper runs and the uninstall completes.
- **M-T8:** helper exe deleted before uninstall → the uninstall still completes.
- **M-T9:** repair or modify → no prompt.

Existing tests to adjust: none should break, because `GetStartupPrograms` is unchanged. Fakes implementing `IStartupRegistry` need the new member. `LoadProgramsAsync` callers need the new parameter.

## Docs to update (documenter)

- `StartupController/PRD.MD`: Purpose; §1 (lists disabled entries **and** takes over the ones Windows runs); §3 (remove "never changes Windows' startup configuration"; describe the takeover, Enabled by default, appended last, the own entry excluded, the gate); Registry Usage (the `StartupApproved` write, 12-byte format, HKCU only, no admin); Success Criteria.
- `README.md` and `StartupController/README.MD`: what happens on first start (all HKCU startup apps are moved under StartupController; Task Manager shows them Disabled; the first logon after takeover is started by Windows, later ones by the app); how to give an entry back to Windows (enable it in Task Manager, though the app takes it over again on its next load, or use the uninstaller's option).
- `Form1.HelpText`: rewrite "Which programs are listed" and remove "Windows' own startup settings are never changed".
- `CHANGELOG.md` [Unreleased] "Changed" (behaviour change, prominent): silent takeover, the gate, the fact that disabling in the app now means the program doesn't start at all, and the D7 consequence (an updated command → `Changed` → not started by anyone until re-enabled).
- `SECURITY.md`: the app now writes `StartupApproved`. A tampered `Run` value written while the app runs is taken over with its current fingerprint (same trust level as Run itself).
- `docs/plans/2026-09-30-code-review-fixes.md`: mark D2 as "Reversed 2026-10-02, see `2026-10-02-silent-takeover.md`". Note that 2.1's skip rule now applies only when the gate is off or a takeover failed.

- Uninstall (D-T3): README.md and StartupController/README.MD, a new "Uninstalling" section. It covers the prompt, the silent default (Return), `RETURNTOWINDOWS=0/1`, that Yes returns **all** taken-over entries still in Run (including ones switched off in the app, re-disabled in Task Manager after the takeover, or `Changed`), while entries disabled before the takeover are never touched, other users on the machine and `uninstall.log`. CHANGELOG entry. SECURITY.md: the uninstall custom action runs impersonated, the MSI post-build patch, and D-T4 (no fingerprint check at uninstall, so a changed Run command is enabled in Windows again). PRD Registry Usage: `TakenOverPrograms`.

## Risks / open questions

- **Risk: vdproj custom-action behaviour** (NoImpersonate, sequencing before `RemoveFiles`, the UILevel when uninstalling from Settings) comes from vdproj and MSI conventions. Confirm it on a VM (M-T1, M-T2, M-T5). If Settings uninstall runs at UILevel 2, there is no prompt there and the silent default (Return) applies. Report that to the user.
- **Risk: uninstall covers one user only** (see "Scope limits").
- **Risk: D-T4 re-enables changed commands.** If something rewrote a taken-over Run value (D7 `Changed`), Yes or a silent uninstall makes Windows start the new command. This is accepted per the user's decision and documented in SECURITY.md.
- **Risk: the patch depends on the Visual Studio build.** A command-line MSBuild can't build vdproj, so there is no unpatched path. The `PostBuildEvent` is the single gate, and it fails closed (no MSI).
- **Risk: D7 amplification.** Every startup app is now subject to D7. Updaters that rewrite their Run command (versioned paths) become `Changed` and are started by nobody until the user re-enables them. The Warning log and status text exist, but `--launch` shows no balloon for it. Suggested follow-up (user call): a single balloon in `--launch`, "N startup programs changed and were not started".
- **Risk: losing the store.** If the user deletes `HKCU\Software\StartupController`, all taken-over entries are listed Disabled and start nowhere. This was already true for managed entries, but now it covers all of them. Document it.
- **Risk: missed first logon** if the app disables an entry before Explorer reaches it (see item 1). It is a single miss, logged.
- **Assumption: logon timing (review fix 20).** Explorer processes the Run key in one pass at logon and StartupController's own entry is part of it, so an entry taken over during `--launch` has already been started (or is being started) by Explorer. Such an entry is therefore deliberately **not** started by the app in that logon (`StartupSession.LaunchableAtLogon`); the app starts it from the next logon. If this assumption is wrong for a machine (the app consistently wins the race), every newly taken-over entry misses exactly one logon; it is never started twice.
- **Risk: renamed copies / wrappers of StartupController** under another Run name are taken over (they are not the own entry). Disabling them in Windows is desirable (fewer relaunch paths). The self-launch guard then blocks them in `--launch`. They show Enabled but are skipped. Acceptable, documented.
- **Risk: Task Manager / other tools re-enabling** an entry triggers another takeover on the next load. That is the intended "hostile" behaviour, logged each time.
- PRD conflict: PRD §3 and Registry Usage currently forbid writing `StartupApproved`. This plan requires the PRD update listed above, approved by the user's decision.

## Review fixes (2026-10-02)

Fixes after the tester, code-inspector and security-analyser reviews, plus the new user decision D-T6. Only deviations and additions to the design above are listed; everything else is as planned.

- **D-T6 (user decision): warning only, no app-side return.** `StartupRegistryService.CountStrandedTakenOver()` (new `IStartupRegistry` member, read-only) counts recorded names that are still string values in Run and disabled in Windows while StartupController's own Run entry is missing or disabled, and logs a Warning. After a UI load (not in `--launch`), `Form1` shows `StartupSession.StrandedWarning(n)` as a 10 s tray balloon with the warning icon. It is shown **even with "Silence Notifications" on**, because it reports programs that start nowhere. The uninstall helper logs the user SID in its header, a Warning when it runs as SYSTEM, a Warning when it finds nothing to return, and after a return a line that other profiles were not handled.
- **Custom action (fix 1):** `"InstallAction"` is now `3:8` (Uninstall). The vdproj enum appears to be bit flags (1 Install, 2 Commit, 4 Rollback, 8 Uninstall), so `3:4` was Rollback. This is from memory of the format and not verified in Visual Studio; M-T1 now includes the VS check and the comparison with a VS-generated action.
- **Verify (fix 2):** `-Verify` and the post-patch verify also reject the rollback/commit bits (0x100/0x200), require exactly one InstallExecuteSequence row whose sequence lies between InstallInitialize and InstallFinalize, and require it to come before RemoveFiles when that row exists (the helper must still exist). This was exercised on synthetic MSI databases in the scratchpad, never on a real MSI.
- **Prompt (fixes 3-5):** only UILevel 5 prompts; 3 and 4 behave like silent (Return). The prompt is `MessageBoxTimeoutW` with a 120 s timeout. A timeout means the silent default (Return), and the prompt text says so. A prompt that fails, or an entry point that is missing, counts as No, as before. The prompt is skipped (and the silent default applies) when the helper runs as SYSTEM, in session 0, or when `Environment.UserInteractive` is false. The rule is logged. These come from a new `IHelperEnvironment` seam.
- **Race (fix 6):** right before the StartupApproved writes, the takeover re-reads the stored order and skips (with a Warning) any accepted name that is no longer in `EnabledPrograms`. `UiSave_BetweenTheTakeoversStoreWrites_DoesNotDropTheEntry` is un-skipped and passes.
- **D7 on hidden names (fix 7), deviates from D-T2 for one case:** a candidate already stored Enabled with a different fingerprint (or, once fingerprints exist, with none) keeps its stored fingerprint. It is still taken over in Windows and recorded, but it lists as Changed and is not launched until the user re-enables it. A Warning names it. Stored-disabled hidden names and migrating stores behave as D-T2 describes.
- **NUL in names (fix 8):** `TakeoverRecord.IsStorable` rejects names containing `\0`, and `GetStartupPrograms` skips them with a Warning, because a lookup by such a name would read a different value. The .NET registry API can't create such a name, so the listing check is covered by a source test.
- **Elevation at logon (fix 9):** new `IProgramLauncher.LaunchAtLogon`, used by `LaunchRunner.LaunchSequenceAsync`. Its default implementation delegates to `Launch`. In `ProgramLauncher`, ERROR_ELEVATION_REQUIRED is not retried through the shell; it logs "'<name>' requires elevation, not started" and returns a failure. The manual Launch button keeps the UAC fallback.
- **Helper log (fix 10):** I kept the log and guarded it rather than skipping it when elevated, because M-T2 relies on `uninstall.log`. No line is written if any folder on the path, or the file itself, is a reparse point, or if the opened file has more than one link. The file is opened with `FileMode.Append` and `FileShare.Read`.
- **Prompt count (fix 11):** `ReturnToWindows.ReturnableCount(root, names)` counts only names that are still string values in Run (not the own entry). The summary "Returned n of m" still counts every recorded name.
- **App state in the log (fix 12):** a returned name gets " (it was Disabled in StartupController)" or " (it was Changed in StartupController)" when the stored order says so. The D7 hash moved to a shared, net462-compatible `FingerprintHash.cs`, which the helper links and `RunFingerprint.Compute` delegates to.
- **Packaging (fix 13):** the copy to `bin\Installer` is gone. The vdproj File entry points at `StartupController.Uninstall\bin\Release\net462\StartupController.ReturnToWindows.exe`. The setup project builds only in the Release solution configuration (and depends on the helper project), so Debug and Release can't be mixed.
- **Code quality (fixes 14-18):**
  - `TakeOverWindowsEntries` is split into `ScanForTakeover`, `FilterByRecordRoom`, `BuildTakeoverOrder` (now returns a `TakeoverPlan` record), `WriteTakeoverStore` and `StillStoredEnabled`. The StartupApproved `CreateSubKey` and the write loop stay in `TakeOverWindowsEntries`, so the guard test is unchanged.
  - `MAX_NAMES` and `MAX_NAME_LENGTH` alias the `TakeoverRecord` caps.
  - `StartupApprovedState.Disabled` converts a Local time and takes Unspecified as UTC. A clock before 1601 is caught in `TakeOverWindowsEntries` and logged, with nothing written.
  - The PostBuildEvent calls `"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"` with `-ExecutionPolicy RemoteSigned`.
  - The helper's global usings are trimmed to `System` and `System.Collections.Generic`, which the linked files need; helper files declare the rest.
  - The duplicate gate comment in Form1 is removed.
- **Arguments (fix 19):** a switch given more than once is unparsable (null) and logged as a Warning. A duplicate `--choice` lets the UI level decide; a duplicate `--uilevel` gives the silent default.
- **Timing (fix 20):** stated in the `LaunchableAtLogon` comment and under Risks above.

## Review fixes, round 2 (2026-10-02, security re-check)

- **N7 (user requirement), fixed:** the prompt is back for UILevel exactly 3, 4 or 5, because the user wants to be asked and Settings > Apps most likely uninstalls at basic or reduced UI. Values with flag bits (`/passive`) and 2 stay silent. The hang guards from round 1 stay: the 120 s `MessageBoxTimeoutW` timeout and the SYSTEM, session 0 and not-interactive skips. The decision table above and M-T2 are updated.
- **N1, fixed:** `UninstallLog.For(environment, path)` returns a log that writes nothing while the token is elevated or SYSTEM. The round 1 reparse-point and hard-link guard stays for the non-elevated case. M-T2 now says to uninstall non-elevated to see `uninstall.log`.
- **N2, fixed:** a failed prompt (`PromptAnswer.Failed`) or one that throws applies the silent default (Return) and logs "Prompt failed: silent default: Return". Only an explicit No leaves the entries disabled. This replaces round 1's "failed counts as No".
- **N3, fixed:** `StartupRegistryService.OwnEntryProblem` is shared by the takeover gate and `CountStrandedTakenOver`. The own Run value must be REG_SZ or REG_EXPAND_SZ, its parsed exe (`CommandLineParser.Parse`) must be this app, its arguments must contain the exact token `--launch`, and it must be enabled in Windows. Anything else counts as missing: no takeover, and the stranded warning applies.
  - "This app" is checked by the injected `Func<string, bool> isThisApp`. The default, `IsThisAppExe`, compares against `Environment.ProcessPath` after `PathHelper.NormalizePath`, or as the same file via `PathHelper.IsSameFile`. Tests inject a fake app path.
  - Deviation: I reuse the PathHelper logic but not `ProgramLauncher.IsSelf`. IsSelf deliberately matches any file named StartupController.exe, which is too loose for proving that the entry starts this app.
  - `CountStrandedTakenOver` now counts only recorded names that are stored Enabled and not Changed, using the same rule as `OrderMerger.Merge`.
- **N5, fixed:**
  - `IProgramLauncher.LaunchAtLogon` has no default implementation any more; the test fake implements it.
  - In logon mode, a `.lnk` whose header has `SLDF_RUNAS_USER` (LinkFlags 0x2000) is not shell-started and logs "'<name>' requires elevation, not started". Only the 24 header bytes are read, and the header size (0x4C) and shell-link CLSID are checked; a file that can't be read or isn't a shell link is shell-started as before.
  - Remaining gaps: a shortcut whose target exe requires elevation by its manifest, a `.bat`/`.cmd` that elevates itself, and bare names resolved by the shell can still show a UAC prompt at logon.
- **N6, fixed:** with whitespace collapsed, the sequence condition must equal `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`, or `(<that>) AND $<component>=2`, or `$<component>=2 AND (<that>)`. `NOT (...)`, `... OR ...`, extra terms, another component state and the unparenthesized form are rejected. If vdproj emits a form outside these, the build fails loudly and M-T1 shows the actual condition. I ran this against synthetic MSI databases in the scratchpad: 3 accepted forms passed, and 5 rejected forms deleted the MSI and exited 1.
- **N4:** no change. D-T4 is a user decision; left for the documenter.
- **Nitpick:** the patch script's `Release-All` is renamed `Clear-ComObjects` (an approved PowerShell verb).
