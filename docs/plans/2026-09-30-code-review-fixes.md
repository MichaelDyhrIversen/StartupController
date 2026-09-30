# Plan: Code review fixes (tester read-only review, 20 findings)

Date: 2026-09-30
Input: tester's read-only review (no tests added, no repros run). The planner checked every finding against `StartupController/PRD.MD` and the current code on `master` (701437a).

---

## Decisions (2026-09-30)

The user answered the open questions from the first version of this plan.

- **D1 (was Q1): Reword PRD §3.** The app is an add-on that controls existing Windows startup behaviour. Enable/Disable in the app changes only the app's own launch list and never writes `StartupApproved`. PRD §3 was updated in the same change as this plan revision.
- **D2 (was Q2): No "Take over" feature.** The user said: "it should be an add on to control the existing functionality". The app never disables or enables entries in Windows. Entries Windows runs itself (no `StartupApproved` value, or an even first byte) are not listed after 2.1. Item 2.3 deletes the unused `SetProgramEnabled`.
- **D3 (was Q3): Don't keep writing the legacy `StartupOrder` value.** Read it once to migrate, then **leave it untouched: don't delete it and don't rewrite it**. Why this is safer than deleting it:
  - If writing the new values fails partway, the legacy value is still there and migration runs again on the next load. Deleting it first, or in the same step, could lose the user's only saved order.
  - A user who rolls back to 701437a still has a working (if older) launch list instead of an empty one.
  - Deleting it gains nothing functionally. After the first save, `ProgramOrder` exists and the legacy value is ignored.
  - Downside we accept: changes made on a rolled-back build aren't picked up after upgrading again, because `ProgramOrder` already exists. The CHANGELOG notes this.
- **D4 (was Q4): Keep saved names whose Run entry is currently missing** (or currently Windows-enabled), with their position and enabled flag. They are hidden from the list and launched only when they are listed again, meaning present in Run and disabled in Windows. So there is no double launch.
- **D5 (was Q5): Skip the unsaved-changes prompt during Windows shutdown or logoff** (`CloseReason.WindowsShutDown`). Changes stay unsaved and nothing is written. That is the conservative choice, because the user never confirmed the save.
- **D6 (was Q6): HKLM, `Run32` and `StartupFolder` stay out of scope.** Only HKCU `Run` and HKCU `StartupApproved\Run` are read. No admin is needed anywhere in this plan.

---

## Goal

Fix the correctness defects in listing, ordering, persistence and launching startup programs. These serve PRD §1 (list disabled programs), §3 (enable/disable), §4 and §5 (order and save it), "Notification Popup and Tray", "Registry Usage" and "Singleton". Before any fix, add a test project with registry and process seams so every fix has regression tests. No test may touch the real `HKCU\...\Run`, `StartupApproved` or `HKCU\Software\StartupController`, and no test may launch a process.

## Verification of findings

| # | Tester severity | Verdict | Reasoning |
|---|---|---|---|
| 1 | High | **Rejected as a bug, with one part upgraded** | Listing only Windows-disabled entries (`StartupRegistryService.cs:33`) is intended. PRD §1 says "List Disabled Startup Programs", and the ordering model needs entries disabled in Windows. The all-zero = enabled rule (line 57) matches Windows behaviour. **But** "missing value = disabled" (line 54) is wrong. Windows runs Run entries that have no `StartupApproved` value, and Task Manager shows them as Enabled. The app lists them, and if they are enabled in the app they launch **twice**. This also lists the app's **own** `StartupController` Run value (written by `AddThisApplicationToStartup` without an approved value), so the app can end up launching itself. This part is High and fixed in 2.1. |
| 2 | High | **Confirmed** | `StartupRegistryService.cs:113-116`. The loop runs to `order.Count` over `ordered` and throws when saved names outnumber the listed entries. That happens when an entry is removed, or re-enabled in Task Manager, which is common. The exception is caught in `Form1.cs:186`, the list stays empty, and `--launch` starts nothing. A stale name with an equal-or-longer list flags the wrong program. Example: saved `A;X`, list `A,B` → B gets launched. `order.IndexOf` is case-sensitive, but registry value names are not. |
| 3 | High | **Partly confirmed, downgraded to Medium on its own** | Enable/Disable only change memory (`Form1.cs:280,298,317`). Under the ordering model that is **correct**: app Enable must not write `StartupApproved`. The TODOs and the no-op `Task.Run` at lines 282 and 300 are misleading and get removed. The claim "disabling a middle entry re-enables the wrong programs" is **wrong on its own**. Saving `A;C` and reloading gives `A,C,B`, and the first two are A and C, which is correct. It only happens together with #2's stale names. The real defect: the order of app-disabled entries is never saved (line 550 saves enabled names only), so they jump to the bottom on reload. This is fixed by the v2 format in 2.2. |
| 4 | High | **Confirmed** | `Form1.cs:139-143` (fire-and-forget), 547-551 (enumerates `startupPrograms` on the thread pool), 556-560 (a failure doesn't set dirty), 92-95 (turning AutoSave on doesn't flush), 552 (a balloon on every save). |
| 5 | Medium | **Confirmed, moved to Phase 2** | `Form1.cs:110-127`. `e.Cancel` set after `await` is ignored, and the process may exit mid-write. It is tightly coupled to 4. |
| 6 | Medium | **Confirmed** | `Form1.cs:97-107`: exit right after launching. The load failure (`Form1.cs:186-190`) shows a balloon that disappears when `Application.Exit` disposes the tray icon. Tray-hide happens in `Program.cs:31-34`, `Form1.cs:39-46` and `Form1.cs:579-587`. |
| 7 | Medium | **Confirmed** | `Form1.cs:330-368`. Unmatched quote: `"C:\a b.exe` falls through to the regex, which keeps the quote. Lazy regex: `C:\x.exe.d\app.exe` → `C:\x.exe`. The fallback passes the whole command line as the file name (`Form1.cs:387`, `444`). Launch code is duplicated at 376-405 and 433-462. Note: `RegistryKey.GetValue` already expands REG_EXPAND_SZ. Expanding `%VAR%` in REG_SZ is a safe improvement, not a proven bug. |
| 8 | Medium | **Downgraded to Low** | The dereferences are at 279-280, 297-298, 316-317 and 373/378. `Tag` is always set in `RefreshListView` (line 205), so a null is theoretical. The CS8602 suppression (`Form1.cs:55-63`) covers `ContextMenuStrip`, **not** these lines, so that part of the claim is misattributed. |
| 9 | Medium | **Confirmed** | `Form1.cs:464` (notification inside the launch try), 465 (success log commented out), 392/403/449/460 (`Process` not disposed), 467-470 (no notification on FileNotFound). The same pattern is at 407 for a manual launch. |
| 10 | Medium | **Downgraded to Low** | `SetProgramEnabled` (`StartupRegistryService.cs:66-76`) has **no callers**. The behaviour described is accurate but can't be reached. It is deleted in 2.3 (D2). |
| 11 | Medium | **Partly confirmed** | Parity is right: Windows uses `0x02`/`0x06` for enabled and odd values (`0x03`, `0x07`, `0x01`) for disabled, and line 62 treats `0x06` as disabled. "All-zero is unfounded" is **rejected**. All-zero is observed as enabled, and the parity rule gives the same answer anyway. "Short arrays not validated" only matters for writes, because reads use only `value[0]`. |
| 12 | Medium | **Mostly rejected, downgraded to Low** | A missing `StartupApproved\Run` key means no entry is Windows-disabled, so an empty list is the **correct** result under the model once 2.1 lands. A missing Run key → empty list is also correct. HKCU-only is out of scope (D6). Exception handling: `SecurityException` is already caught by the UI (`Form1.cs:186`). What remains is to log the key path. Low. |
| 13 | Medium | **Confirmed, merged into 2.2** | `;` join/split at lines 84 and 98. It is fixed by the REG_MULTI_SZ migration. |
| 14 | Low | **Partly rejected** | Settings and order sharing `HKCU\Software\StartupController` is intended (PRD Registry Usage). Only the duplicated constant is worth fixing. Unhandled exceptions in the `CheckedChanged` handlers (`Form1.cs:65-95`) are real. Low. |
| 15 | Low | **Confirmed** | Dead `else` at `Form1.cs:156-159` (autosave + dirty already returned at 143). The empty `catch {}` at 167. `Resize` reads the registry on every resize (line 41). |
| 16 | Low | **Confirmed** | `Form1.cs:75-88` has no try/catch, and the setting is saved before the Run write, so they can get out of sync. `AddThisApplicationToStartup` uses `OpenSubKey`, which returns null when the key is missing (`StartupRegistryService.cs:123`). |
| 17 | Low | **Confirmed** | `Form1.cs:2`, `Program.cs:59-62`, `Program.cs:27`. Also, `[STAThread]` on `async Task Main` is fragile. Make `Main` synchronous so the STA apartment is certain. |
| 18 | Low | **Confirmed** | `LoggingService.cs`. The static path makes it untestable. |
| 19 | Low | **Confirmed, upgraded to Medium** | `Program.cs:74`. `MainWindowHandle` is 0 for a hidden (tray) window, so a second launch does nothing. That breaks the PRD Singleton requirement in the default tray use case. |
| 20 | Low | **Confirmed** | `Form1.cs:283,301,319` refresh without reselecting. |

Not in the report, noted for later and out of scope: PRD §5 "sortable columns" is not implemented (no `ColumnClick` handler), and PRD §6 help is a single MessageBox (`Form1.cs:564`).

---

## Phase 1: Testability seams and test project (no behaviour change)

Shippable alone: refactor only, plus characterization tests that pin **current** behaviour, including the bugs. Phase 2 flips those tests.

### 1.1 Test project
- Add `StartupController.Tests/StartupController.Tests.csproj`: xUnit, `net8.0-windows`, `<UseWindowsForms>true</UseWindowsForms>`, `ProjectReference` to `StartupController.csproj`. Add it to `StartupController.sln`.
- Add `InternalsVisibleTo("StartupController.Tests")`. `GenerateAssemblyInfo` is false, so add it to `Properties/GeneratedVersionInfo.cs` or a new `Properties/AssemblyInfo.cs`. Check that `tools/Increment-BuildVersion.ps1` doesn't overwrite it.

### 1.2 Registry seam
- `StartupRegistryService`: add `public StartupRegistryService(RegistryKey root)`. Keep the parameterless constructor → `Registry.CurrentUser`. Replace every `Registry.CurrentUser.` in the class with `_root.`. All paths stay relative.
- `UserSettingsService` (static) → `interface IUserSettings` + `sealed class UserSettings(RegistryKey root) : IUserSettings`. Give it the same four get/set pairs and cache values in memory after the first read (this also covers item 15).
- `Form1`: add a constructor `Form1(IUserSettings settings, StartupRegistryService registry, IProgramLauncher launcher)`. Keep the parameterless constructor for the designer, chaining to defaults. `Program.cs:30` uses the instance.
- **Test sandbox** (`Tests/Infrastructure/RegistrySandbox.cs`): creates `HKCU\Software\StartupController.Tests\{guid}`. That is a sibling of the app key, not a child. The sandbox:
  - exposes the sandbox `RegistryKey` as the service root, so `Software\Microsoft\Windows\CurrentVersion\Run` resolves **inside** the sandbox;
  - has helpers `SeedRun(name, cmd)`, `SeedApproved(name, byte[])` and `SeedOrder(...)`;
  - deletes the tree in `Dispose`;
  - guards: throws if the root name doesn't start with `HKEY_CURRENT_USER\Software\StartupController.Tests\`.
- Guard test: reflect over the test assembly and fail if any test code calls the parameterless `StartupRegistryService()`, `new UserSettings(Registry.CurrentUser)` or `Form1()`. If reflection is too heavy, add a Roslyn-free source grep in a test.

### 1.3 Pure functions
- New `internal static class StartupApprovedState { static bool IsEnabled(byte[]? value) }`. It keeps the **current** logic (missing = disabled, all-zero = enabled, `[0]==0x02`).
- `ApplyCustomOrder` → `internal static List<StartupProgram> ApplyCustomOrder(List<StartupProgram>, IReadOnlyList<string> order)`, which keeps the current logic. `GetStartupPrograms` calls `LoadStartupOrder()` and passes the result in.

### 1.4 Command parsing and launching
- Move `SplitCommand` verbatim to `internal static class CommandLineParser { (string exe, string args) Split(string) }`.
- New `interface IProcessStarter { IDisposable? Start(ProcessStartInfo psi); bool FileExists(string path); }` with a `ProcessStarter` default.
- New `ProgramLauncher(IProcessStarter)` with `LaunchResult Launch(StartupProgram)`. It holds the logic duplicated at `Form1.cs:376-405` and `433-462`, moved unchanged. `Form1.LaunchSelectedProgram` and `LaunchEnabledProgramsAsync` call it.

### 1.5 Logging seam
- `LoggingService.Initialize(string logDirectory)` (internal). The static constructor keeps the default. Tests point it at a temp directory. `LogLaunchResult` is unchanged for now.

### 1.6 Presenter extraction
- New `StartupListModel` (no WinForms types) that owns `List<StartupProgram>`, `MoveUp/Down/Top/Bottom(int)`, `Toggle/Enable/Disable(int)`, `IsDirty`, and `Snapshot()`, which returns an immutable `StoredOrder` (see 2.2). `Form1` handlers become thin: call the model, then refresh. The AutoSave logic moves here in 2.4.

**Registry impact:** none (refactor). **Risks:** `ProjectReference` to a WinExe works on .NET 8 but copies the app's runtimeconfig; if it fails, set `<GenerateRuntimeConfigurationFiles>`. Behaviour drift in the move: characterization tests guard it.

**Test cases (tester):**
- `StartupApprovedState` (current): null → false; `[]` → false; 12×`0x00` → true; `02 00..` → true; `03 00..` → false; `06 00..` → false (pinned, flipped in 2.1).
- `ApplyCustomOrder` (current): empty order → input order; `A;B` over `B,A,C` → `A,B,C` with A and B enabled; order `A;B;C` over list `A,B` → throws `ArgumentOutOfRangeException` (pinned, flipped in 2.2); order `A;X` over `A,B` → B enabled (pinned bug).
- `CommandLineParser` (current behaviour table): `"C:\a b\x.exe" -y`; `C:\a b\x.exe -y`; `C:\x.exe.d\app.exe` → `C:\x.exe` (pinned bug); `"C:\a b.exe` (unmatched); `notepad`; empty; whitespace.
- `ProgramLauncher` with a fake `IProcessStarter`: an existing exe → one Start with `FileName`, `Arguments` and `WorkingDirectory`; a missing exe → Start with the raw command and `UseShellExecute=true`; an empty path → FileNotFound result. Assert the fake received **no** real start.
- `GetStartupPrograms` against the sandbox: missing Run key → empty; missing approved key → empty; a mixed seed → only `03` entries are returned.
- `UserSettings` in the sandbox: defaults false; round-trip for each setting; a non-int value (REG_SZ "1") → false.

---

## Phase 2: High-severity correctness

Each item can ship alone in the order listed. 2.4 and 2.5 ship together.

### 2.1 Correct `StartupApproved` semantics and exclude the app's own entry (findings 1, 11)
**Files:** `StartupApprovedState.cs`, `StartupRegistryService.GetStartupPrograms`.
- `IsEnabled(byte[]? v)`: `null` or length 0 → **true** (Windows default). Otherwise `(v[0] & 0x01) == 0`.
- In `GetStartupPrograms`, skip `STARTUP_CONTROLLER_NAME` (case-insensitive), whatever its state.
- Log at Info each Run entry skipped because it has no approved value (name only).

**Registry impact:** reads only (HKCU Run, HKCU `StartupApproved\Run`). No admin. Behaviour change: entries without an approved value, and the app's own entry, leave the list. Their names may still be in the saved order. Per D4, 2.2 keeps them in storage, hidden, with their enabled flag, and launches them only if they are listed again.
**Risks:** users who "managed" an entry that had no approved value will see it disappear. It was already being run by Windows, so the app was launching it twice. Mention this in the CHANGELOG. We can't check the parity rule against the real Task Manager in tests (hard rule). The developer may check it manually in a throwaway VM.
**Tests:** null / `[]` → enabled; `02`, `06`, `00×12` → enabled; `03`, `07`, `01` → disabled; a 1-byte `03` → disabled; a sandbox seed with a `StartupController` Run value set to `03` → not listed; an entry with no approved value → not listed.

### 2.2 Order persistence v2: fix the ApplyCustomOrder crash and keep app-disabled order (findings 2, 3, 13)
**Files:** `StartupRegistryService` (`SaveStartupOrder`, `LoadStartupOrder`, `ApplyCustomOrder`), new `StoredOrder.cs`, `StartupListModel`, `Form1.SaveOrderAsync`.
- `record StoredOrder(IReadOnlyList<string> Order, IReadOnlySet<string> Enabled)` with case-insensitive comparers.
- New pure `OrderMerger.Merge(IReadOnlyList<StartupProgram> listed, StoredOrder stored) → List<StartupProgram>`:
  1. Stored names present in `listed` (OrdinalIgnoreCase), in stored order, with `Enabled = stored.Enabled.Contains(name)`.
  2. Then listed names not in storage, in registry enumeration order, with `Enabled = false`. New entries are not auto-launched, same as today.
  3. Never index by position. Duplicates in storage: the first one wins.
- Save (`SaveStartupOrder(StoredOrder)`): the order written is the displayed order **plus** stored-but-unlisted names kept in their relative positions (D4): each unlisted name stays right after the name that came before it in storage, or at the front if it was first. Their enabled flags are kept in `EnabledPrograms`. Filter empty and whitespace names.
- Remove the misleading TODOs and no-op `Task.Run` at `Form1.cs:281-282, 299-300, 318`. Enable/Disable stay in-memory model changes plus `SetDirty(true)`.

**Registry impact (all HKCU, no admin):**

| Value under `HKCU\Software\StartupController` | Kind | Role |
|---|---|---|
| `ProgramOrder` (new) | REG_MULTI_SZ | All managed names, display order |
| `EnabledPrograms` (new) | REG_MULTI_SZ | Names StartupController launches |
| `StartupOrder` (legacy) | REG_SZ, `;`-joined | Read once for migration only. Never written, never deleted (D3) |

- **Migration on load:** if `ProgramOrder` is absent and `StartupOrder` is present → `Order = Enabled = legacy.Split(';', RemoveEmptyEntries | TrimEntries)`. This matches what 701437a meant: the legacy list holds exactly the enabled names in order. Don't write anything on load. The new values are written on the first save. Log "Migrated legacy StartupOrder (n names)". Migration counts as done once `ProgramOrder` exists. From then on the legacy value is ignored and left untouched (D3).
- If `ProgramOrder` exists but has the wrong kind (for example REG_SZ written by hand), fall back to legacy, then to empty, and log a warning. Don't throw.
- No legacy write (D3). `SaveStartupOrder` writes only `EnabledPrograms`, then `ProgramOrder` (last, see Risks), and never calls `DeleteValue("StartupOrder")`.
- Never change the kind of the existing `StartupOrder` value. 701437a reads it `as string`, so a MULTI_SZ there would silently wipe the order on downgrade. (Rejected alternative: reuse `StartupOrder` as REG_MULTI_SZ.)

**Risks:** rolling back to 701437a after a v2 save gives the order as it was at migration time, not later edits. Upgrading again ignores edits made on the rolled-back build, because `ProgramOrder` exists. Both are accepted per D3 and noted in the CHANGELOG. Write `ProgramOrder` last: if the `EnabledPrograms` write fails, migration reruns from the legacy value instead of leaving a half-written v2 state.
**Tests (pure plus sandbox):**
- Merge: stored `A,B,C` / enabled `A,C`, listed `C,B,A` → `A(on),B(off),C(on)`.
- Stored longer than listed (`A,B,C`, listed `A,B`) → no throw, `A,B`.
- Stale name (`A,X` enabled both, listed `A,B`) → `A(on),B(off)`. B **not** enabled (regression for #2).
- Case: stored `spotify`, listed `Spotify` → matched, and the display uses the registry casing.
- Duplicates `A,A,B` → `A,B`. Empty segments in legacy `A;;B;` → `A,B`.
- Name containing `;` round-trips via v2.
- Middle disable: `A,B,C` all on, disable B, save, reload → `A(on),B(off),C(on)` in the **same order** (regression for #3).
- Migration: only legacy `A;B` → Order `A,B`, Enabled `A,B`; `ProgramOrder`/`EnabledPrograms` not created until save; after save both exist **and `StartupOrder` is still present and unchanged** (same kind and string).
- Post-migration: with `ProgramOrder`=`B,A` and legacy `A;B;C` present → the load uses `B,A` and ignores the legacy value. Saving never writes or deletes `StartupOrder`.
- Failed migration write: the fake store throws on the `ProgramOrder` write → the next load migrates again from the legacy value, with no data loss.
- Malformed: `ProgramOrder` as REG_DWORD → falls back, no exception.
- D4 position: stored `A,X,B` (X enabled), listed `A,B`, move B up, save → `ProgramOrder` = `B,A,X` (X stays after A) and `EnabledPrograms` still contains X.
- D4 relisting: stored `A,X` (X enabled), X absent from Run → X not listed or launched. Seed X in Run with approved `03` → X is listed in position 2 and enabled.
- D4 no double launch: X stored and enabled, X present in Run with approved `02` (or no approved value) → X is not listed and not handed to the launcher.
- Entry removed externally while the app runs: load `A,B`, delete B from the sandbox Run key, save from the model → B kept in storage with its position and flag (D4). Reload → `A` only, no exception.

### 2.3 `SetProgramEnabled` (finding 10)
- Delete `StartupRegistryService.SetProgramEnabled` (D2: the app never changes Windows' startup configuration). Nothing in the app opens `StartupApproved` for writing any more; the read in `GetStartupPrograms` stays.
- Test: a sandbox test where full list/save/launch cycles (fake launcher) leave the sandbox `StartupApproved\Run` values byte-identical. Add a source-scan guard test that fails if app code opens `StartupApproved` writable (`OpenSubKey(..., true)` or `CreateSubKey` on that path).
- **Registry impact:** none if deleted.

### 2.4 AutoSave: serialized, snapshot-based, failure-visible (finding 4)
**Files:** `StartupListModel` (or a new `OrderSaveCoordinator`), `Form1.SetDirty`, `Form1.SaveOrderAsync`, the AutoSave checkbox handler.
- Take the snapshot **on the UI thread** (`model.Snapshot()`) before any `Task.Run`. The background code never touches `startupPrograms`.
- A monotonic `_version` counter and a `_saveLock` around the registry write. A save writes its snapshot only if its version is newer than `_lastWrittenVersion`. That makes it latest-wins, whatever order the tasks finish in.
- `RequestAutoSave()` returns a Task. The caller in `SetDirty` awaits it in an `async void` wrapper with try/catch. On failure: `IsDirty = true`, the salmon button, one failure balloon, and a log entry.
- A successful autosave: a log entry only, no balloon. A manual Save: keep the "Order saved!" balloon.
- Turning AutoSave on while `IsDirty` → save immediately.
- Fix `SetDirty`'s dead branch and empty catch as part of this rewrite.

**Registry impact:** same values as 2.2. There is no new key.
**Risks:** a UI-thread deadlock if the UI thread waits on `_saveLock` while a background save needs the UI thread. Mitigation: the code under the lock is registry-only, with no UI marshalling.
**Tests (with a fake `IOrderStore` or the sandbox, and a fake that can delay or throw):**
- Two saves, where the first is slowed by the fake so the second finishes first → the stored value equals the second snapshot.
- The store throws → `IsDirty == true`, one failure notification raised (through an injected `INotifier`), nothing swallowed.
- Mutating the model right after a save request doesn't affect the written snapshot.
- AutoSave off → dirty; AutoSave on → exactly one save, then `IsDirty == false`.
- An autosave success raises no notification. A manual save raises one.

### 2.5 FormClosing that actually cancels and finishes saving (finding 5)
**Files:** `Form1.cs` FormClosing handler.
- Make the handler synchronous. If dirty: Yes → `model.SaveNow()` (synchronous, same `_saveLock`, latest snapshot). On failure → `e.Cancel = true` plus a MessageBox. Cancel → `e.Cancel = true`. No → close.
- If a save is in flight and nothing is dirty, `SaveNow()` still takes the lock, which waits for the in-flight write to finish before exit.
- `CloseReason.WindowsShutDown` (D5): no prompt, no save, don't cancel. Log "Unsaved changes discarded at shutdown". Other reasons (`UserClosing`, `ApplicationExitCall` from the tray Exit) keep the prompt. Put the decision in a pure `ClosePolicy.Decide(CloseReason, bool isDirty) → Prompt | CloseSilently`.

**Tests:** `ClosePolicy`: `WindowsShutDown`+dirty → CloseSilently; `UserClosing`+dirty → Prompt; `ApplicationExitCall`+dirty → Prompt; any reason + clean → CloseSilently. Model level: `SaveNow` after a pending async save writes the latest snapshot. When `SaveNow` throws it reports failure (the handler maps that to Cancel). The UI handler itself is checked manually.

---

## Phase 3: Medium items

### 3.1 Launch pipeline (findings 7, 9)
**Files:** `CommandLineParser.cs`, `ProgramLauncher.cs`, `Form1.LaunchSelectedProgram`, `Form1.LaunchEnabledProgramsAsync`, `LoggingService.LogLaunchResult`.
- Parser: `Environment.ExpandEnvironmentVariables` first. For a leading quote, find the closing quote, or strip an unmatched leading quote and treat the rest as unquoted. For unquoted input, use CreateProcess-style resolution: try each space-delimited prefix, shortest first, and pick the first one where `FileExists(prefix)` or `FileExists(prefix + ".exe")`. Otherwise use the first token as the exe and the rest as args. Drop the lazy regex. The parser takes `Func<string,bool> fileExists` so it stays pure.
- Launcher fallback when the exe isn't found: shell-start **the parsed exe and args**, not the whole string. Skip the shell start completely if the exe is empty.
- Dispose the `Process` returned by `Start` (`using`).
- `Launch` returns `LaunchResult { Success, Error }`. Notification and logging happen **outside** the launch try. Log success. Every failure, including FileNotFound, shows a balloon (unless silenced).

**Registry impact:** none (reads the Run command already loaded).
**Risks:** prefix resolution can resolve to an unexpected file (`C:\Program.exe` hijack, the classic unquoted-path issue). Send this to `security-analyser`. Consider preferring the longest existing prefix, or warning in the log when the path is unquoted and has spaces.
**Tests (fake `fileExists` / `IProcessStarter`):** `"C:\a b\x.exe" -y`; unquoted `C:\Program Files\x.exe -y` with only the full path existing → exe `C:\Program Files\x.exe`, args `-y`; `C:\x.exe.d\app.exe` with only the full path existing → full path; unmatched quote `"C:\a b.exe` → exe `C:\a b.exe`; `%LOCALAPPDATA%\x.exe` → expanded; `rundll32.exe shell32.dll,Foo` with no file → exe `rundll32.exe`, args `shell32.dll,Foo`; empty → error result with no Start call; the starter throws → failure result, logged once as a failure; the notifier throws → the launch is still logged as a success; FileNotFound → the notification is raised; the returned handle is disposed (a fake that tracks Dispose).

### 3.2 `--launch` mode, tray, singleton (findings 6, 19)
**Files:** `Form1.cs` Load handler and `OnShown`, `Program.cs`.
- Put tray-hide in one place: `Form1.ApplyStartupVisibility()`, overriding `SetVisibleCore` so the first show is suppressed when StartToTray or `--launch` is set (no flash). Remove `Program.cs:31-35` and the `OnShown` duplicate. The Resize handler keeps "minimize → tray" using the cached setting.
- In `--launch` mode: if load failed → log an Error and show a balloon, then wait about 4 s before `Application.Exit()`. After launching → if notifications aren't silenced, wait about 4 s, then exit. Log a summary: "Launched n of m".
- Singleton: replace the `MainWindowHandle` lookup with a named `EventWaitHandle` (`StartupControllerActivate`). The first instance waits on it on a background thread and `BeginInvoke`s show/restore/activate. The second instance signals it and exits. Keep the mutex.

**Registry impact:** none.
**Risks:** `SetForegroundWindow` restrictions still apply, so call `Activate()` from the first instance's own thread. Security: the named event is per-session (`Local\`), so there is no cross-user signalling.
**Tests:** unit-test the "exit delay" decision (a pure function of silenced/failed). Manual checks: a second launch while in the tray restores the window; `--launch` after a load failure exits after the delay and logs an error. Automated coverage: a unit test on the load path using an injected registry root that throws.

### 3.3 Settings and Run-entry robustness (findings 14, 16, 12 exception part)
**Files:** `Form1.cs:65-95`, `StartupRegistryService.AddThisApplicationToStartup`, `UserSettings`.
- Wrap each checkbox handler in try/catch. On failure, log, notify and revert the checkbox without re-triggering the handler (a `_suppressEvents` guard).
- `chkLaunchProgramsOnStartup`: write the Run entry **first**, then the setting.
- `AddThisApplicationToStartup`: `CreateSubKey(RUN_KEY)` instead of `OpenSubKey`.
- Share one `AppRegistryPaths` constants class between the services. Include the key path in the service's exception logs.

**Registry impact:** writes HKCU `Run\StartupController` (existing behaviour), and now creates the Run key if it's missing. No admin.
**Tests (sandbox):** the Run key is missing → Add creates it and the value is `"<path>" --launch`; Remove when the value is missing → no throw; the settings root is read-only (open the sandbox key without write access) → `Set*` throws and the handler logic (in the model) reverts the state.

---

## Phase 4: Low / cleanup

| Item | Files | Change | Tests |
|---|---|---|---|
| 8 | `Form1.cs` | `if (item.Tag is not StartupProgram prog) return;` everywhere. Narrow the CS8602 pragma, or replace it with `!`, on the ContextMenuStrip. | Covered by the model tests; not tested directly. |
| 15 | `Form1.cs` | Cached settings (from 1.2). `Resize` uses the cached value. | `UserSettings` caching: Get after Set returns the new value without re-reading. |
| 17 | `Form1.cs:2`, `Program.cs` | Remove the unused using, `Form_Load`, and make `Main` synchronous `void`. | Build only. |
| 18 | `LoggingService.cs` | Size-based rotation (for example 1 MB, keep 3). Replace `\t`, `\r` and `\n` in fields. Log the exe path but mask arguments (`args=<n chars>`). Log `OpenLogFile` failures via `Debug.WriteLine`. Background writing is optional and not required. | Temp-dir log: rotation at the threshold; a field with a tab becomes escaped; arguments are masked. |
| 20 | `Form1.cs` `RefreshListView` | Keep the selection by program name across the refresh. Use `BeginUpdate/EndUpdate`. | Manual. |
| 12 | – | Nothing (D6: HKLM/Run32/StartupFolder out of scope). | – |

---

## Docs to update (documenter)
- `CHANGELOG`: 2.1 behaviour change (entries with no approved value and the app's own entry no longer listed; the double-launch fix), the 2.2 storage change (`ProgramOrder`/`EnabledPrograms`, legacy `StartupOrder` read once for migration and left in place; a rollback gives the order as of migration), AutoSave no longer shows a balloon on success.
- `StartupController/PRD.MD`: §3 was already reworded by the planner (D1). Documenter: add `ProgramOrder`/`EnabledPrograms` to Registry Usage.
- `README.md` and `StartupController/README.MD`: explain what "Enabled" means in the app compared with Task Manager. Mention that entries enabled in Task Manager don't appear in the list.
- In-app help (`Form1.ShowHelp`): the same explanation.
- `CONTRIBUTING.md`: how to run `dotnet test`, and the registry sandbox rule.

## Risks (cross-cutting)
- Phase 1 changes the `Form1` constructor. The designer needs the parameterless constructor, so keep it.
- 2.1 plus 2.2 change what users see on the first run after upgrading. The CHANGELOG must explain it.
- 3.1 prefix resolution needs `security-analyser` review (unquoted-path hijack).
- Run `security-analyser` on Phases 2, 3.1, 3.2 and 3.3 (registry, process launch, IPC).
