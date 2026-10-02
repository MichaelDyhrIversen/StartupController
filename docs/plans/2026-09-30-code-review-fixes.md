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
- **D7 (2026-09-30): Enabled programs are bound to a fingerprint of their Run command** (the user's choice from the security review). This covers the case where something rewrites a Run value the user enabled.
  - New REG_MULTI_SZ `EnabledFingerprints` under `HKCU\Software\StartupController`. Each line is `name|sha256`: the lowercase hex SHA-256 of the value kind plus the raw Run data (`kind + "\0" + raw`, UTF-16LE), read once with `RegistryValueOptions.DoNotExpandEnvironmentNames`. Only the hash is stored, never the command.
  - Mismatch (the Run data changed since the user enabled it): the program is treated as disabled and not launched. The Status column shows "Changed – re-enable to launch", and a Warning is logged with **the name only**.
  - Re-enabling the program in the app records the new fingerprint.
  - A reinstall that writes the same Run data keeps the same fingerprint, so it keeps launching.
  - Migration: a currently listed enabled name that has no fingerprint (legacy `StartupOrder`, or a pre-D7 `EnabledPrograms`) is accepted and launched. Its fingerprint is recorded at the next save. Nothing is written on load. A **hidden** name (D4) that reappears without a fingerprint is **not** launched. It is treated like a mismatch.
  - Write order: `EnabledPrograms` and `EnabledFingerprints` first, `ProgramOrder` last.
  - Rejected by the user: fingerprint expiry, re-disabling every name that reappears, and matching on the exe path only.
  - Phase 2 review changes that come with D7:
    - The legacy fallback happens **only when `ProgramOrder` is absent**. If `ProgramOrder` exists but is malformed (wrong kind or unreadable), nothing is enabled, a Warning is logged, and the legacy value is **not** used.
    - Stored lists are capped at about 1024 names, each at most 260 characters, on read and on write. Over-long names are dropped, entries past the cap are ignored, and a Warning is logged once per load.
  - The developer is implementing D7 now. Plan text in 2.2 below is updated to match. Where the plan fills in detail beyond the user's decision, it is marked "(plan interpretation)" for the developer to confirm.

## Decisions (2026-10-01)

The user answered the four open questions after Phase 3 was committed.

- **D8: Allow at most one `--launch` sequence per Windows logon session.** This comes from the Phase 3 security review (L2, option 5). It breaks relaunch loops from entries the self-launch guard can't detect: `cmd /c` wrappers, `.lnk` files and renamed copies. A second `--launch` in the same session logs and exits without launching. Suggested design, to be finalised by the planner: store the session id plus the logon or boot time under `HKCU\Software\StartupController`. Accepted downside: a second manual `--launch` in the same session does nothing. **Designed in 4.D8 (key: WTS session id plus logon time; fail closed, no fallback key, confirmed 2026-10-01 as Q-D8a/Q-D8b below). Not implemented yet.**
- **D9: Don't verify whether Windows expands `%VAR%` in REG_SZ Run values.** Keep the current Phase 3 behaviour: REG_SZ values are expanded at launch, and REG_EXPAND_SZ values are expanded once at read. The accepted risk (security I1) is that a change to `HKCU\Environment` can redirect a launch without changing the D7 fingerprint. That needs same-user write access, which is the same trust level as `Run`. Document this in SECURITY.md.
- **D10: Move to .NET 10 (LTS) as Phase 5.** .NET 8 support ends on 2026-11-10. Retarget both projects and check the installer (`SetupStartupController.vdproj`), the NuGet packages and CA1416. **Planned in Phase 5. Q5.1-Q5.4 and Q5.6 answered 2026-10-01 (below); Q5.5 still open. Not started.**
- **D11: The SECURITY.md supported-versions table stays at 1.0.2.** No change is needed.

## Decisions (2026-10-01, D8/Phase 5)

The user answered the 4.D8 and Phase 5 open questions.

- **Q-D8a: Fail closed.** If the logon session can't be identified, or the `LaunchSession` check or record fails, `--launch` launches nothing, logs an Error, shows a balloon, and exits after `NotificationExitDelay`. Manual Launch is unaffected. This is the design already in 4.D8.
- **Q-D8b: No fallback key (plan decision).** The user wasn't asked separately. The planner's recommendation applies because it follows from Q-D8a: if WTS fails, the guard returns `Unavailable` and fails closed. There is no session-id-plus-boot-time fallback.
- **Q5.1: Framework-dependent.** Keep today's deployment model. No self-contained publish profile.
- **Q5.2: Drop the launch condition if the installer extension can't target .NET 10.** In that case, remove the .NET launch condition from `SetupStartupController.vdproj` and rely on the apphost's ".NET required" dialog as a stopgap. Moving to WiX is not part of this release.
- **Q5.3: Ship Phase 5 in the same release as Phases 1-4 and D8.**
- **Q5.4: Keep the .NET Framework 4.7.2 prerequisite for now.** The user did not choose to remove it, so Phase 5 leaves it as is. It can be revisited in a later release.
- **Q5.5: Answered 2026-10-02: raise the minimum OS to Windows 10 1607.** Change `[assembly: SupportedOSPlatform("windows7.0")]` to `windows10.0.14393` (the .NET 10 minimum). Also raise the installer's minimum OS to match, if it sets one. Fix any new CA1416 warnings in the same commit.
- **Q5.6: Yes, update `.claude/agents/*.md` from ".NET 8 / net8.0-windows" to ".NET 10 / net10.0-windows" as part of Phase 5.** User-approved, but only when Phase 5 lands (in the Phase 5 commit), not before.

## Status and handoff (2026-10-01, end of session)

Branch `code-review-fixes` (not pushed, not merged to `master`):

| Commit | Content |
|---|---|
| 80aeccb | Phase 1: testability seams and the xUnit project |
| 3b5a0d8 | Phase 2: StartupApproved semantics, order storage v2 with D7 fingerprints, AutoSave and closing |
| 75890bf | Docs for Phases 1 and 2 (CHANGELOG, PRD, SECURITY, CONTRIBUTING, READMEs) |
| 2cd1ef0 | Phase 3 plus two security review rounds (parser M1, direct start, self guard, IPC, CWD pinning) |

418 tests pass, the build has 0 warnings, and `dotnet format` is clean. The security-analyser verdict is that Phase 3 is OK for release as far as M1 is concerned.

Next steps, in order:
1. ~~**Planner:** add D8 as a Phase 3 or Phase 4 item with a design and test cases, and add Phase 5 (D10).~~ **Done (2026-10-01):** D8 is 4.D8, .NET 10 is Phase 5. The Phase 4 table was re-checked against the code, and done items are marked.
2. **Phase 4 (developer):** see the Phase 4 table for status. Cleanup items 8, 15, 17, 18 and 20. Item 18 now also covers security L2 and I3: stop logging command-line arguments (names or the exe path only), escape `\r`, `\n` and `\t` in logged values, and add log rotation. Also: wrap `new Mutex` (done in Phase 3), dead code in Form1 (the commented `AdjustListViewColumns` block, the `#pragma CS8602`), `Program.Form_Load`, `async Task Main`, unused usings, making `LaunchFromStartup` a property, moving `NormalizePath` and its P/Invoke into a path helper, and the optional linear parser scan (I-3).
   **Status (2026-10-01): Phase 4 is implemented (uncommitted in the working tree) and in review** by the tester, code-inspector and security-analyser. Next: the developer fixes the findings, then commits Phase 4. **Update (2026-10-02):** the review findings are fixed (see "Phase 4 review fixes" below), still uncommitted; next is a re-review, then the Phase 4 commit.
3. **4.D8 implementation (developer)**, after the Phase 4 commit, in its own commit, then the tester, code-inspector and security-analyser review it. Q-D8a and Q-D8b are answered (fail closed, no fallback), so it is ready to start.
4. **Documenter:**
   - CHANGELOG and SECURITY.md for Phase 3: the parsing rules (unquoted commands end at the first .exe/.com/.bat/.cmd/.lnk token, quote folders with such extensions, extensionless unquoted commands with arguments give NotFound).
   - SECURITY.md: direct start without the MOTW/zone prompt (I-1); the self-guard residuals (wrappers and renamed copies *anywhere*; the mutex and D8 stop loops); the 30 s `--launch` timeout; D9.
   - The in-app help text in `Form1.cs` (the meaning of Enabled, the Changed status, the salmon Save button, the close prompt).
   - Check the README button names.
5. **Phase 5 (D10):** .NET 10, after 4.D8 (committed 46649f5, 2026-10-02), in the same release (Q5.3). All Phase 5 questions are answered. Q5.5: raise the minimum OS to `windows10.0.14393`. Check installer support for .NET 10 early (it is the long pole; fallback per Q5.2), and finish before 2026-11-10. Update `.claude/agents/*.md` in the same phase (Q5.6).
6. **Manual UI checks by the user**, still outstanding: start to tray with no flash, tray restore, second start restoring the window, the Launch button disabled during a launch or UAC prompt, the settings checkboxes, the exit prompt Yes/No/Cancel, AutoSave, first-start migration, the Task Manager enable/disable round trip, and `--launch` on a throwaway profile only.

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

### 2.2 Order persistence v2: fix the ApplyCustomOrder crash, keep app-disabled order, add fingerprints (findings 2, 3, 13; D7)
**Files:** `StartupRegistryService` (`SaveStartupOrder`, `LoadStartupOrder`, `ApplyCustomOrder`), new `StoredOrder.cs`, new `RunFingerprint.cs`, `StartupProgram` (add raw command and a fingerprint status), `StartupListModel`, `Form1.SaveOrderAsync`, `Form1.RefreshListView` (status text).
- `record StoredOrder(IReadOnlyList<string> Order, IReadOnlySet<string> Enabled, IReadOnlyDictionary<string,string> Fingerprints)`, all with case-insensitive name comparers.
- `GetStartupPrograms` reads each Run value **once** with `DoNotExpandEnvironmentNames`, plus `GetValueKind`. Both the launch `Path` and the fingerprint come from that single read (security re-review L1): `Path` = `Environment.ExpandEnvironmentVariables(raw)` for REG_EXPAND_SZ, otherwise `raw`. `RunFingerprint.Compute(kind, raw)` = lowercase hex SHA-256 of the UTF-16LE bytes of `kind + "\0" + raw`, where `kind` is the `RegistryValueKind` name (`String` or `ExpandString`). A REG_SZ ↔ REG_EXPAND_SZ switch with the same text therefore counts as Changed. The format hasn't shipped, so there's no version prefix. A non-string Run value can't be fingerprinted, so it is never launched and a Warning is logged with the name only (plan interpretation).
- Parse `EnabledFingerprints` lines by splitting on the **last** `|`, because names may contain `|` and hashes can't. Ignore malformed lines (not 64 hex characters, empty name) with one Warning.
- New pure `OrderMerger.Merge(IReadOnlyList<StartupProgram> listed, StoredOrder stored) → List<StartupProgram>`:
  1. Stored names present in `listed` (OrdinalIgnoreCase), in stored order. `Enabled` is true only when the name is in `stored.Enabled` **and** its fingerprint check passes (D7):
     - a stored fingerprint that matches → enabled;
     - a stored fingerprint that doesn't match → disabled, status `Changed`;
     - no stored fingerprint and the name was listed at the last save or comes from migration → enabled (the fingerprint is recorded at the next save);
     - no stored fingerprint and the name was hidden (D4) → disabled, status `Changed`. Tell "hidden" and "migrating" apart like this: a pre-D7 store has no `EnabledFingerprints` value at all, so every name is migrating. Once `EnabledFingerprints` exists, each enabled name that was listed at save time has a fingerprint, so an enabled name without one must have been hidden (plan interpretation).
  2. Then listed names not in storage, in registry enumeration order, with `Enabled = false`. New entries are not auto-launched, same as today.
  3. Never index by position. Duplicates in storage: the first one wins.
- Save (`SaveStartupOrder(StoredOrder)`): the order written is the displayed order **plus** stored-but-unlisted names kept in their relative positions (D4): each unlisted name stays right after the name that came before it in storage, or at the front if it was first. Their enabled flags are kept in `EnabledPrograms` and their existing fingerprints in `EnabledFingerprints`. Filter empty and whitespace names. Apply the cap (1024 names, 260 chars each).
- Fingerprints on save: for each listed enabled name, write the fingerprint of its **current** raw data. Enabling (or re-enabling a `Changed` entry) is therefore what records the new fingerprint. Disabling a name drops its fingerprint. `Changed` entries stay disabled with no fingerprint until the user re-enables them.
- Remove the misleading TODOs and no-op `Task.Run` at `Form1.cs:281-282, 299-300, 318`. Enable/Disable stay in-memory model changes plus `SetDirty(true)`. Enable on a `Changed` entry clears the status.

**Registry impact (all HKCU, no admin):**

| Value under `HKCU\Software\StartupController` | Kind | Role |
|---|---|---|
| `ProgramOrder` (new) | REG_MULTI_SZ | All managed names, display order |
| `EnabledPrograms` (new) | REG_MULTI_SZ | Names StartupController launches |
| `EnabledFingerprints` (new, D7) | REG_MULTI_SZ, `name\|sha256hex` | Fingerprint of each enabled name's raw Run data. A hash only, never the command |
| `StartupOrder` (legacy) | REG_SZ, `;`-joined | Read once for migration only. Never written, never deleted (D3) |

- **Migration on load:** if `ProgramOrder` is absent and `StartupOrder` is present → `Order = Enabled = legacy.Split(';', RemoveEmptyEntries | TrimEntries)`. This matches what 701437a meant: the legacy list holds exactly the enabled names in order. Don't write anything on load. The new values are written on the first save. Log "Migrated legacy StartupOrder (n names)". Migration counts as done once `ProgramOrder` exists. From then on the legacy value is ignored and left untouched (D3).
- Fall back to legacy **only when `ProgramOrder` is absent** (D7 review change). If `ProgramOrder` exists but is malformed (wrong kind, for example REG_SZ or REG_DWORD written by hand), use an empty stored order: every listed entry shows, none enabled. Log a Warning and don't throw. The legacy value is not consulted. Treat a malformed `EnabledPrograms` or `EnabledFingerprints` the same way: nothing enabled.
- No legacy write (D3). `SaveStartupOrder` writes `EnabledPrograms` and `EnabledFingerprints` first, then `ProgramOrder` last (D7, see Risks), and never calls `DeleteValue("StartupOrder")`.
- Never change the kind of the existing `StartupOrder` value. 701437a reads it `as string`, so a MULTI_SZ there would silently wipe the order on downgrade. (Rejected alternative: reuse `StartupOrder` as REG_MULTI_SZ.)

**Risks:** rolling back to 701437a after a v2 save gives the order as it was at migration time, not later edits. Upgrading again ignores edits made on the rolled-back build, because `ProgramOrder` exists. Both are accepted per D3 and noted in the CHANGELOG. Write `ProgramOrder` last: if the `EnabledPrograms` or `EnabledFingerprints` write fails on the first save, migration reruns from the legacy value instead of leaving a half-written v2 state. On later saves a partial write can leave a new `EnabledPrograms` next to old fingerprints. The worst case is an entry shown as `Changed` (fail-closed), never an unexpected launch. D7 risk: any legitimate update that rewrites the Run value (a new version path, changed arguments) stops that program launching until it is re-enabled. The Warning log and the status text make that visible. The CHANGELOG and help must explain it.
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
- Malformed: `ProgramOrder` as REG_DWORD, with legacy `A;B` present → everything listed, **nothing enabled**, legacy ignored, one Warning, no exception. The same for `ProgramOrder` as REG_SZ.
- Malformed `EnabledFingerprints` (REG_SZ) → nothing enabled, Warning.
- Caps: 1500 names in `ProgramOrder` → the first 1024 are used, one Warning; a 300-char name is dropped on read and never written; a save with more than 1024 names writes 1024.
- D7 match: enabled `A` with a fingerprint of its current raw data → enabled and launched (fake launcher).
- D7 mismatch: change `A`'s Run data in the sandbox → `A` disabled, status "Changed – re-enable to launch", not handed to the launcher. The log has a Warning whose text contains `A` and **no** part of either command.
- D7 re-enable: after a mismatch, enable `A` and save → `EnabledFingerprints` holds the new hash and a reload launches `A`.
- D7 reinstall: delete `A` from Run and re-add it with byte-identical data → still enabled.
- D7 raw data: the Run value is REG_EXPAND_SZ `%LOCALAPPDATA%\x.exe`. The fingerprint is computed over the unexpanded string, so changing the `LOCALAPPDATA` environment variable doesn't change it. Compare with a known SHA-256 of the UTF-16LE bytes of `"ExpandString\0" + raw`. Also: Path and fingerprint come from one read; a REG_SZ → REG_EXPAND_SZ switch with the same text → Changed; REG_EXPAND_SZ Path is expanded, REG_SZ Path is literal.
- D7 storage: `EnabledFingerprints` lines contain only `name|` plus 64 lowercase hex characters. Assert that no stored value contains the command text.
- D7 name with `|`: `a|b` round-trips (split on the last `|`).
- D7 migration, listed: only legacy `A;B`, both listed → both enabled and launched. Nothing is written on load (the sandbox values are unchanged). After a save, `EnabledFingerprints` has both hashes.
- D7 migration, pre-D7 v2 store: `ProgramOrder`/`EnabledPrograms` exist with no `EnabledFingerprints` → listed enabled names are accepted and recorded at the next save.
- D7 hidden reappearance: `EnabledFingerprints` exists, `X` is in `EnabledPrograms` with no fingerprint (it was hidden at the last save), and `X` reappears in Run disabled in Windows → listed, status `Changed`, not launched.
- D7 disable drops the fingerprint: disable `A` and save → no `A|` line.
- D7 write order: a fake store records the call sequence → `EnabledPrograms`, `EnabledFingerprints`, `ProgramOrder`. If the fake throws on `EnabledFingerprints` during the first save → `ProgramOrder` is not written and the next load migrates again.
- D4 position: stored `A,X,B` (X enabled), listed `A,B`, move B up, save → `ProgramOrder` = `B,A,X` (X stays after A) and `EnabledPrograms` still contains X.
- D4 relisting: stored `A,X` (X enabled, **with a fingerprint** of its old data), X absent from Run → X not listed or launched. Seed X in Run with the same data and approved `03` → X is listed in position 2 and enabled. Seed it with **different** data → listed in position 2, status `Changed`, not launched (D7).
- D4 no double launch: X stored and enabled, X present in Run with approved `02` (or no approved value) → X is not listed and not handed to the launcher.
- Entry removed externally while the app runs: load `A,B`, delete B from the sandbox Run key, save from the model → B kept in storage with its position, flag and fingerprint (D4, D7). Reload → `A` only, no exception.

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

**Registry impact:** same values as 2.2 (`ProgramOrder`, `EnabledPrograms`, `EnabledFingerprints`), in the same write order. There is no new key.
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
**Tests (fake `fileExists` / `IProcessStarter`):** `"C:\a b\x.exe" -y`; unquoted `C:\Program Files\x.exe -y` with only the full path existing → exe `C:\Program Files\x.exe`, args `-y`; `C:\x.exe.d\app.exe` with only the full path existing → full path; unmatched quote `"C:\a b.exe` → exe `C:\a b.exe`; `%LOCALAPPDATA%x.exe` → expanded; `rundll32.exe shell32.dll,Foo` with no file → exe `rundll32.exe`, args `shell32.dll,Foo`; empty → error result with no Start call; the starter throws → failure result, logged once as a failure; the notifier throws → the launch is still logged as a success; FileNotFound → the notification is raised; the returned handle is disposed (a fake that tracks Dispose).

### 3.1a Never launch StartupController itself (security-analyser, Low)
**Why:** 2.1 hides only the canonical `StartupController` Run value. A Run entry under **any other name** (stale from an old install, copied, or planted) that points at `StartupController.exe --launch` could be enabled and launched. The child starts after the parent exits (the parent exits after launching; see 3.2), takes the mutex, and relaunches every enabled entry, including itself, in a loop.
**Files:** `ProgramLauncher.cs` (it already owns parse and resolve after 3.1), `CommandLineParser.cs`, `Form1.LaunchSelectedProgram`, `Form1.LaunchEnabledProgramsAsync`.
- `ProgramLauncher` takes a `selfExePath` (default `Application.ExecutablePath`) and an injectable `Func<string,string> normalizePath` (default: `Path.GetFullPath` plus `GetLongPathNameW` when the file exists, so 8.3 short names like `STARTU~1.EXE` resolve).
- After parsing, env expansion and prefix resolution, **before** any `Start` (including the shell fallback), block when the resolved exe:
  - normalizes to the same path as `selfExePath` (OrdinalIgnoreCase), **or**
  - has the file name `StartupController.exe` (OrdinalIgnoreCase) in any directory, **or**
  - has no extension and resolves via the `.exe` probe to such a file.
- The check is on the **parsed executable only**, whatever the Run value name. Arguments are not scanned.
- A blocked launch returns `LaunchResult.Blocked` and logs a Warning with the entry name only. In `--launch` it counts as skipped in the "Launched n of m" summary, with no per-entry balloon. A manual Launch shows a MessageBox: "This entry starts StartupController itself and can't be launched from here."
- Optional: in `RefreshListView`, show such entries with status "Blocked (StartupController)" so the user understands why they never start.

**Registry impact:** none. It reads the Run data already loaded.
**Risks:** wrappers such as `cmd /c StartupController.exe --launch` or a renamed copy of the exe are not caught, because the rule matches the parsed exe only. The singleton mutex blocks the loop only while the parent is still running. The 3.2 exit delay narrows but doesn't close that window. Recorded as a residual risk for `security-analyser`. Comparing file hashes of the target and self would catch renamed copies; that is out of scope unless requested.
**Tests (fake `IProcessStarter`, fake `fileExists`/`normalizePath`, `selfExePath` = `C:\Program Files\StartupController\StartupController.exe`):**
- `"C:\Program Files\StartupController\StartupController.exe" --launch` under Run name `Foo` → Blocked, starter never called, Warning logged containing `Foo` and not the command.
- The same path in different case (`c:\program files\startupcontroller\STARTUPCONTROLLER.EXE`) → Blocked.
- Another directory: `"D:\Old\StartupController.exe" --launch` → Blocked (file-name rule).
- Unquoted with spaces: `C:\Program Files\StartupController\StartupController.exe --launch`, resolved by prefix → Blocked.
- No extension: `"C:\Program Files\StartupController\StartupController" --launch` with the `.exe` probe succeeding → Blocked.
- Env var: `%ProgramFiles%\StartupController\StartupController.exe` → Blocked after expansion.
- Relative segments: `C:\Program Files\x\..\StartupController\StartupController.exe` → Blocked after normalization.
- 8.3 short name: the fake `normalizePath` maps `C:\PROGRA~1\STARTU~1\STARTU~1.EXE` to the self path → Blocked.
- Shell fallback path: the exe isn't found but its file name is `StartupController.exe` → Blocked, no shell start.
- Not blocked: `C:\x\StartupControllerHelper.exe`, `C:\x\MyStartupController.exe`, `notepad.exe C:\x\StartupController.exe` (the name appears only in the arguments) → started once each.
- `--launch` sequence `A`, self-entry `S`, `B` → the starter receives `A` and `B` only, and the summary is "Launched 2 of 3".
- Manual Launch on `S` → Blocked result, and the MessageBox path is taken (checked through the `INotifier`/dialog seam).

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

### Phase 3 implementation notes (developer, 2026-10-01)
- 3.1 parser, first version (superseded): unquoted input was resolved longest existing prefix first. **Superseded by the review round below (security M1).**
- 3.1 parser, current: unquoted input is scanned **shortest first**, and the first prefix with an executable extension (.exe .com .bat .cmd .lnk, ignoring trailing dots and spaces) is the exe, whether or not it exists; a missing one is NotFound. Nothing else is probed. The `.exe` probe applies only to the whole string, and only when no token has an executable extension. Consequences:
  - An unquoted path whose directory name contains an executable extension (`C:\My.exe Tools\app.exe`) resolves to the first token; quote it to run the longer path.
  - An extensionless unquoted exe followed by arguments (`C:\App\tool -q`) is not resolved; it gives NotFound.
  - Probes are capped at 32 per command.
  - An unmatched quote with no executable token keeps the whole rest as the exe.
- 3.1 no double expansion: `StartupProgram.PathExpanded` (true for REG_EXPAND_SZ, set in `GetStartupPrograms`) tells the launcher that `Path` is already expanded. Fingerprints are unchanged.
- 3.1 shell fallback: a missing exe is shell-started (parsed exe + args) **only when it is a bare file name without spaces** (e.g. `rundll32.exe`). A missing path with a directory part returns NotFound, and nothing is started. URL- or AppUserModelID-style commands are no longer shell-started (fail closed).
- 3.1 / 3.1a: logging and notifications are in the new `LaunchRunner` (used by the Launch button and `--launch`). There is a new `IMessageDialog` seam for the blocked-entry MessageBox. Blocked entries get no `LAUNCH` log line, because that line contains the command.
- 3.1a extra: an extensionless `StartupController` (a bare name the shell could resolve through App Paths) is also blocked. The optional "Blocked (StartupController)" status in the list is not implemented.
- 3.2: `Form1.SetVisibleCore` suppresses the first show and posts the startup work, because Load is raised only by a real show. `InstanceActivation` (`Local\StartupControllerActivate`) restores the window. A second instance started with `--launch` exits without signalling, so a login-time launch doesn't pop up the window of an instance the user already opened.
- 3.3: the checkbox logic is in `SettingsController`. If the setting write fails after the Run entry was written, the Run change is undone (best effort). `AppRegistryPaths` holds only the Run and app key paths; the `StartupApproved` path stays private to `StartupRegistryService` (source-scan guard).
- Left for Phase 4: `async Task Main` (item 17), the unused usings and `Form_Load`.

#### Phase 3 review fixes (developer, 2026-10-01)
- M1: shortest-first parsing, as described above. The tester's two longest-first pins were flipped and renamed: `Unquoted_LongestFirst_PicksACombinedNameWhenSuchAFileExists` and `DotExeInDirectory_WithSpaces_LongestWins`. The second also pinned longest-first; keeping it would have re-opened M1.
- Direct start: an existing, fully qualified `.exe` or `.com` starts with `UseShellExecute = false`. It retries through the shell only on `Win32Exception` 740 (elevation required). `.bat`, `.cmd`, `.lnk` and bare names still use the shell.
- L1: `FileExists` is called only for fully qualified paths. A bare name goes straight to the shell branch. Any other relative form (`sub\x`, `..\x`, `C:x`, `\x`) returns NotFound.
- Self guard:
  - File names are compared after trimming trailing dots and spaces, and `.exe` is never appended to a name that ends in a dot.
  - `\\?\`, `\\?\UNC\` and `\\.\` prefixes are stripped before comparing.
  - A bare name is also checked as if it sat in `AppContext.BaseDirectory`.
  - A file-identity check (volume serial plus file index) runs when both files exist. It fails safe if it throws.
  - After the self check, a resolved exe with a `:` after the drive position (alternate data stream) or a trailing dot or space is rejected as a failure.
- Bounds: a command longer than 32767 characters, raw or after expansion, is a failure, logged by name only.
- IPC:
  - The activation is created before `Form1`, and a failure to create it is logged and the app continues without it.
  - `ActivationRelay` keeps a request that arrives before the form exists.
  - A request that arrives before the window handle exists is honoured in `Form1.OnHandleCreated`, the equivalent of honouring it in SetVisibleCore or Load.
  - Repeats within 1.5 s are ignored, logged at most once per interval.
  - `SignalExisting` also catches `IOException` and `ArgumentException`.
- Code inspector:
  - `LaunchRunner.FailureMessage`, and an `INotifier.SafeNotify` extension used everywhere.
  - The Launch button ignores clicks and is disabled while a launch runs.
  - The startup mode (launch mode and start hidden) is read once and cached.
- `--launch` hang: each launch has a 30 s timeout. When it runs out, the entry is logged as a failure and the sequence continues.

#### Security re-review follow-ups (developer, 2026-10-01)
- L-A:
  - `Program.Main` sets the current directory to `Environment.SystemDirectory` first.
  - The bare-name shell start sets `WorkingDirectory = Environment.SystemDirectory`, so the directory the app was started from is never searched.
- L-B:
  - The activation event must be newly created (the `createdNew` overload). If it already exists, the handle is disposed, `ActivationEventExistsException` (an `IOException`) is thrown, and `Program.CreateActivation` logs a Warning and runs without activation.
  - The listener also calls `Reset()` after each wake.
- L-C:
  - The fallback splits off arguments only after a bare first token. Otherwise the whole command is the exe, so it ends up NotFound: `D:\Apps v2\tool` never becomes a planted `D:\Apps`.
- L-D:
  - Manual launches have no timeout; the Launch button stays disabled until the launch really finishes. `--launch` keeps the 30 s timeout.
  - A launch that completes after its timeout is logged by name only: started, failed or blocked.
- I-2: for extensionless paths, `<path>.exe` is probed before `<path>` (CreateProcess order).
- I-5: a failure to create the singleton mutex is logged and the app exits cleanly.

---

## Phase 4: Low / cleanup

Status checked against `code-review-fixes` at 756b828 (planner, 2026-10-01). This list matches handoff step 2. Implement the table first, then 4.D8 as its own commit, because 4.D8 edits the same `Program.Main` lines as item 17.

| Item | Status | Files | Change | Tests |
|---|---|---|---|---|
| 8 | **Done (developer, 2026-10-01, uncommitted).** Pragma removed; the tray menu is built in a local owned by `components`. Before: `GetSelectedProgram` uses `Tag as StartupProgram` (`Form1.cs:449`), and no hard casts are left. | `Form1.cs` | Use `is not StartupProgram prog` for any remaining `Tag` access. Remove the `#pragma warning disable/restore CS8602` around the tray menu (`Form1.cs:75-83`): build the `ContextMenuStrip` in a local, add the items, then assign it to `notifyIcon.ContextMenuStrip`. | Build with 0 warnings. |
| 15 | **Done.** Settings are cached since 1.2. `Resize` (`Form1.cs:59-61`) uses the cached `_settings`. The dead `else` and the empty catch in `SetDirty` went away in the 2.4 rewrite. | – | – | Existing `UserSettings` caching tests. |
| 16 | **Done in 3.3.** | – | – | – |
| 17 | **Done (developer, 2026-10-01, uncommitted).** Also removed usings that duplicate the implicit global usings in `ClosePolicy`, `ProcessStarter`, `StartupRegistryService`, `LoggingService`, `ProgramLauncher` (IDE0005 does not run from the CLI here, so this was checked by hand). | `Program.cs`, `Form1.cs` | Remove `Program.Form_Load` (`Program.cs:101-104`). Make `Main` a synchronous `static void Main(string[] args)` (it has no `await`). Remove unused usings: `System.Net.WebSockets` (`Form1.cs:2`), `System.Threading.Tasks` in `Program.cs`, and anything else IDE0005 / `dotnet format` reports. Turn `public bool LaunchFromStartup = false;` (`Form1.cs:11`) into an **internal** property. Internal avoids the WinForms WFO1000 serialization diagnostic on the .NET 9+ SDKs (see Phase 5). | Build only, plus the existing `StartupSession` tests. |
| 17a | **Done (developer, 2026-10-01, uncommitted).** Dead code | `Form1.cs` | Delete the commented-out `AdjustListViewColumns` block (`Form1.cs:376` to about `412`). | Build. |
| 18 | **Done (developer, 2026-10-01, uncommitted).** Now includes **security L2 and I3**. `LaunchResult.ExePath` added; the Process start line logs `Args=<n chars>`. Escaping also covers 0x7F-0x9F and U+2028/U+2029; backslashes are not escaped. `OpenLogFile(IProcessStarter, out error)` returns false on failure, and Form1 shows a warning with the log path. `LoggingService.cs` was removed from the `Process.Start` allowlist in `SandboxGuardTests`. Tests: `Phase4Tests`. | `LoggingService.cs`, `LaunchRunner.cs:128` | (a) Never log command-line arguments. `LogLaunchResult` receives the entry name and the **resolved exe path** only, not `program.Path`, which is the raw command with its arguments. Add the resolved exe to `LaunchResult` if it isn't there yet. At most log `args=<n chars>`. (b) `StartSession` (`LoggingService.cs:95-101`) logs only `--launch: yes/no`, not the joined command line. (c) Escape `\r`, `\n`, `\t` and other control characters below 0x20 in every field (`\r`, `\n`, `\t`, `\uXXXX`), so one log call stays on one line. (d) Size-based rotation under the existing `_lock`: at 1 MB, `startupcontroller.log` → `.1.log` → `.2.log`, keep 3 and delete the oldest. Rotation failures are swallowed like write failures. (e) Report `OpenLogFile` failures through `Debug.WriteLine` and a log line. Background writing is optional. | Temp-dir log: rotation at the threshold keeps exactly 3 files. A name containing tab, CR and LF is logged escaped on one line. A launch whose command has the argument `--token=SECRET` logs no `SECRET`. `StartSession` with args `--launch SECRET` logs no `SECRET`. The blocked and timeout paths still log by name only. |
| 20 | **Done (developer, 2026-10-01, uncommitted).** `StartupListModel.IndexToReselect`: same instance first, then name (OrdinalIgnoreCase). The row is also focused and scrolled into view. `SelectProgram` was removed (it was redundant). | `Form1.RefreshListView` | Keep the selection by program name (OrdinalIgnoreCase) across the refresh, and wrap the refill in `BeginUpdate/EndUpdate`. | Manual: Move Up/Down and Enable keep the moved or toggled row selected. |
| Mutex wrap | **Done in Phase 3** (I-5, `Program.cs:29-39`). | – | – | Existing. |
| Path helper | **Done (developer, 2026-10-01, uncommitted).** The one-argument `ProgramLauncher(starter)` constructor was removed, and `Program` passes `Application.ExecutablePath`. Tests use `Programs.TestHostExe` (the old default), and two test references now call `PathHelper.*`. | `ProgramLauncher.cs:223-341` → new `PathHelper.cs` and `NativeMethods.cs` | Move `NormalizePath`, `IsSameFile`, `StripDevicePrefix` and the `GetLongPathNameW` / `GetFileInformationByHandle` P/Invokes. Behaviour is unchanged. 4.D8 adds its WTS P/Invokes to the same `NativeMethods`. | The existing `SelfLaunchGuardTests` and `Phase3*Tests` pass unchanged. |
| I-3 (optional) | **Done (developer, 2026-10-01, uncommitted).** One pass with running trim lengths, and no allocation per candidate. Equivalence is checked against the old algorithm on 20,000 random token inputs. | `CommandLineParser.cs` | Scan the command once, left to right, instead of rebuilding each prefix. Keep shortest-first, the 32-probe cap and every current result. | The existing parser tests pass unchanged. Add a 32767-character command that finishes quickly. |
| 12 | Nothing to do (D6: HKLM, Run32 and StartupFolder are out of scope). | – | – | – |

### Phase 4 review fixes (developer, 2026-10-02, uncommitted)

Findings from the tester, code-inspector and security-analyser reviews of the Phase 4 working tree. 4.D8 and Phase 5 are not started.

- **Security L2 / tester Medium (arguments in the log):** `ProgramLauncher.LoggableExe(exePath)`. A parsed exe that contains whitespace and is not an existing fully qualified file is shown as its first token plus `<+n chars>`. It is used for `LaunchResult.ExePath`, both NotFound messages and the unquoted-path warning, so `FailureMessage` and the LAUNCH line never carry arguments. Exception text goes through `SafeErrorText`, which redacts the command, the hidden part of the exe and the arguments (values of 4 or more characters) and caps the text at 512 characters. `LaunchResult.Ok` was removed because only one test used it.
- **Security L1 (lone surrogates). Verdict:** on .NET 8.0.31, `File.AppendAllText(path, text)` **throws** `EncoderFallbackException` on a lone surrogate, so the line was dropped. The security-analyser was right. The tester's test passed only because xUnit serializes string `InlineData` at discovery and turns a lone surrogate into U+FFFD, so the test never saw one. Its "duplicate ID" skip came from the same cause. Fix: `Escape` writes lone surrogates as `\uXXXX` and keeps valid pairs, and the log is written with `new UTF8Encoding(false, throwOnInvalidBytes: false)` as a backstop. The tester's theory now builds its values in code and expects the escaped form.
- **Security I2 (optional, done):** format characters (category Cf: bidi overrides and isolates, LRM/RLM, BOM, soft hyphen) are escaped as `\uXXXX`. ZWNJ and ZWJ (U+200C, U+200D) are kept because emoji sequences and some scripts need them, and `Escape_KeepsSurrogatePairs` relies on that. **I1 (escaping backslashes) not done:** it would double every path in the log, and two tester tests document the current behaviour as accepted.
- **Tester Low (deleted logs folder):** `AppendLine` recreates the directory on `DirectoryNotFoundException` and retries once. The Skip is removed from `MissingDirectory_LoggingRecoversOnTheNextLine`. `OpenLogFile` still returns false when the folder is missing, as `OpenLogFile_MissingDirectory_ReturnsFalse_AndNeverStarts` expects; its own error log line recreates the folder, so the next click works.
- **Code-inspector Should 1 / security I4 (rotation backoff):** after a failed rotation, the next attempt waits `RotationRetryDelay` (1 minute) or until the log has doubled since the failure. A success clears the backoff. The clock is the `LoggingService.UtcNow` seam. The length is **not** cached: the tester's rotation tests grow the file from outside the logger, and one `FileInfo` per line is cheap.
- **Code-inspector Should 3 (composition):** `Program.Main` uses `new Form1 { LaunchFromStartup = ... }`, so the parameterless constructor is the single composition root (settings, registry, one `ProcessStarter` shared by the launcher and OpenLogs).
- **Code-inspector Should 4 (HelpText):** aligned with README Usage. Added the own entry being hidden, "After the next save it shows as Disabled", the unquoted-path advice and a Settings paragraph. Arrows and the en dash are `\u` escapes, in `StatusText` too.
- **Small items:** `Components` comment clarified. `AdjustListViewColumns` uses named constants and returns early with fewer than 4 columns. Comment added on the private constructor's concrete `ProcessStarter`. `IndexToReselect` tries an exact ordinal name before OrdinalIgnoreCase (the tester's characterization test was updated to expect the exact match).
- **Flaky test (coordinator, 2026-10-02):** `Escape_LoneSurrogates_AreEscapedAndLoggingDoesNotThrowOrSplit("a{D83D}")` failed in about 1 run in 16. Root cause: xUnit's string `StartsWith`/`EndsWith`/`Contains`/`DoesNotContain` compare with the current culture. In da-DK collation "aa" is one letter, so when the random hex marker ended in `a`, the expected suffix `a...` could not match inside `...aa...`. The logged line was correct, so this was not a product bug. Fix: every string-overload assertion in the test project passes `StringComparison.Ordinal` (about 90 call sites; the collection overloads are unchanged). This also closes silent false passes of `DoesNotContain(secret, log)`. New guards: a test pinning the da-DK behaviour, and a source scan that fails if an `Assert.StartsWith`/`EndsWith` omits a comparison. The product code has no culture-sensitive string calls.
- **Tests:** new `Phase4FixTests.cs` (LoggableExe, redaction, surrogate verdict, LAUNCH line with a lone surrogate, format characters, help text characters, reselection) and `Phase4FixRotationTests` (backoff, recovery; `LoggerRedirect` collection). `Phase4Tests.AppendLine_RotatesUnderTheLockBeforeWriting` now pins `RotateWithBackoff` plus the write under the lock.

### 4.D8 At most one `--launch` sequence per Windows logon session (D8)

**Goal.** Break relaunch loops that the 3.1a self guard can't see (`cmd /c` wrappers, `.lnk` files, renamed copies). The first `--launch` in a logon session runs the list. Any later `--launch` in the same session logs Info and exits without launching or signalling. This serves PRD "Singleton" and "Registry Usage", and the Run-at-login behaviour (§4/§5 order). The accepted downside (D8) is that a second manual `--launch` in the same session does nothing. The Launch button is unaffected.

**Why Phase 4 and not 3.4.** Phase 3 is committed and reviewed. 4.D8 changes `Program.Main`, which item 17 rewrites (sync `Main`, `LaunchFromStartup` as a property). Doing 17 first keeps the D8 diff small and reviewable.

#### Session key: WTS session id plus WTS logon time
- Call `WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSSessionInfo (24), out buffer, out bytes)` from `wtsapi32.dll`. Read `WTSINFOW.SessionId` and `WTSINFOW.LogonTime` (a LARGE_INTEGER), then call `WTSFreeMemory(buffer)`. The developer must check the struct layout against `wtsapi32.h` (`WINSTATIONNAME_LENGTH+1`, `DOMAIN_LENGTH+1`, `USERNAME_LENGTH+1` WCHAR arrays, then the five LARGE_INTEGER times).
- Key text: `v1:{SessionId}:{LogonTime}`, both decimal. Only equality matters, so the clock base or time zone of `LogonTime` is irrelevant.
- The key is valid only when `SessionId == Process.GetCurrentProcess().SessionId` and `LogonTime > 0`. Otherwise the key is unavailable, and the failure policy below applies.
- Why this key:
  - **Sign-out then sign-in, reboot, Fast Startup shutdown** (which signs the user out): each is a new logon with a new `LogonTime`, so the list runs once.
  - **Fast User Switching:** every user has their own HKCU hive and their own session. Users don't affect each other.
  - **RDP:** reconnecting to a disconnected session keeps `SessionId` and `LogonTime` (only `ConnectTime` changes), so nothing reruns. Windows doesn't rerun `Run` on reconnect either. A new RDP logon gets a new key.
  - **Elevation:** elevated and non-elevated processes in the same session get the same key, so an elevated wrapper can't sidestep the guard.
  - **Sleep, hibernate, lock:** same session, same key.
- Rejected alternatives:
  - **Logon LUID** (`GetTokenInformation(TokenStatistics).AuthenticationId`). The elevated half of a split (UAC) token has a different LUID from the filtered half. An elevated wrapper would then look like a new session. LUIDs also restart at boot, so they would need a second component anyway. `LsaGetLogonSessionData` adds `LogonTime` but is still keyed per token.
  - **Boot time.** It would block the legitimate run after sign-out and sign-in without a reboot, which breaks the requirement. Derived as "now minus `TickCount64`", it also jitters and moves when the clock changes.
  - **Session id alone.** Ids are reused: the console session is usually 1 or 2 after every boot. Every later logon would be blocked.

#### Storage
- `HKCU\Software\StartupController\LaunchSession`, REG_SZ, holding the key text. It contains no user data, command or path.
- Read: absent means no record. A wrong kind, a value longer than 64 characters, or text that doesn't match `^v1:\d{1,10}:\d{1,20}$` counts as **no record**, with one Warning that gives the value name only. A malformed value can only cause one extra run, and it needs same-user write access, which is the same trust level as `Run`.
- Write: `CreateSubKey(AppRegistryPaths.AppKey)` and then `SetValue("LaunchSession", key, RegistryValueKind.String)`. This replaces a malformed value. No other value under the key is touched.
- No admin. Migration: the value is absent after an upgrade, so the first `--launch` claims the session. Older builds ignore the value. Uninstall leaves it in place, like the other HKCU values.

#### Seams (all `internal`)
- `ILogonSessionKeyProvider { string GetCurrentKey(); }`. It throws on failure. The default is `WtsLogonSessionKeyProvider`.
- `ILaunchSessionStore { string? Read(); void Write(string key); }`. The default is `RegistryLaunchSessionStore(RegistryKey root)`, which does the validation described above. Only `Program.cs` passes `Registry.CurrentUser`. Tests pass the sandbox root. Add it to the composition-root rule in `SandboxGuardTests`.
- `LaunchSessionGuard(ILogonSessionKeyProvider, ILaunchSessionStore)` with `LaunchClaim TryClaim()`, which returns `Claimed`, `AlreadyLaunched` or `Unavailable` and never throws:
  1. Get the key. On an exception, empty text or an invalid key → `Unavailable`, with an Error log that gives the exception type.
  2. Read the stored value. On an exception → `Unavailable`.
  3. If it equals the key (Ordinal) → `AlreadyLaunched`.
  4. Write the key. On an exception → `Unavailable`.
  5. → `Claimed`.
- A pure `LaunchGate.Decide(bool hasLaunchArg, bool launchSettingOn, Func<LaunchClaim> claim)` returns `StartupAction`: `Normal`, `LaunchAndExit`, `ExitAlreadyLaunched` or `BlockedAndExit`. `claim` is called **only** when both flags are true.

#### Wiring and atomicity
- In `Program.Main`, inside `if (isNewInstance)` and **before** `CreateActivation`, `Form1` or any UI: create `settings`. If `args` contains `--launch`, read `settings.GetLaunchProgramsOnStartup()` in a try/catch. If the read fails, log an Error and treat the setting as off: nothing is launched, which is fail-closed and the same result as the setting being off. Then call `LaunchGate.Decide`.
  - `ExitAlreadyLaunched`: `LogInfo("--launch: startup programs were already launched in this logon session; exiting")` and return. There is no form, no tray icon, no activation event and no signal. The mutex is released by the existing `using`.
  - `BlockedAndExit`: start `Form1` in launch mode with a new internal `LaunchBlocked = true`. In `RunStartupAsync`, launch mode with `LaunchBlocked` skips `LaunchSequenceAsync`. It logs the Error "--launch: nothing launched because the logon session could not be recorded", calls `SafeNotify("Startup programs were not launched automatically. Open StartupController and use Launch.")` and exits after `StartupSession.ExitDelay(..., loadFailed: true)`. This mirrors the load-failure path. Extract this tail into `StartupSession.RunLaunchModeAsync(...)` so it can be tested.
  - `LaunchAndExit` and `Normal`: unchanged.
  - Form1's cached `StartupMode` reads the same `UserSettings` instance, which caches values, so Program and Form1 can't disagree about launch mode.
- **Atomicity:** the claim runs while this process owns `StartupControllerSingletonMutex`, and the process holds it until `Main` returns. Any other instance stays in the `!isNewInstance` branch until then (a `--launch` one exits there, as today, without claiming), so check and record can't interleave. The record is written before `Application.Run`, so it exists before any launch. A loop child either meets the mutex (the parent is still running) or the recorded key (the parent has exited).
- **The record is written before the list loads.** If the load fails, the session's claim is still used up. Accepted: nothing was launched, the user gets the load-failure balloon, and Launch works. Rejected alternative: record it in `Form1` just before `LaunchSequenceAsync`. That would allow a retry after a failed load, but it splits check and record across two classes and starts the form before we know whether to exit.

#### Failure policy: fail closed
If the key provider or the registry read or write fails, the result is `Unavailable` and **nothing is launched**. The app logs an Error, shows a balloon and exits. Why:
- The guard is the last loop breaker for the cases the self guard can't see. When recording fails, it fails the same way for every relaunched child. Failing open would turn a misconfigured wrapper entry into an unbounded loop that launches the whole list every round.
- Failing closed costs one logon's automatic launch, and the cost is visible: an Error in the log, a balloon, and Manual Launch still works.
- A failing `HKCU\Software\StartupController` also breaks the settings and order reads, so the app is already degraded in that state.
- Residual: on a system where WTS always fails (not expected on supported Windows, because the Local Session Manager always runs), automatic launch never happens. Every logon logs an Error. Accepted by the user (Q-D8a, Q-D8b).

#### Not affected
- The Launch button (`LaunchRunner.LaunchManualAsync`) never consults the guard.
- A start without `--launch`, or with `--launch` and "Launch programs on startup" off, never reads or writes `LaunchSession`.
- A second instance with `--launch` while the first is running exits as today (`Program.cs:68-72`) without claiming.

#### Decisions (4.D8, answered 2026-10-01)
- **Q-D8a: Fail closed (user decision).** When the logon session can't be identified or recorded, launch nothing, log an Error, show a balloon and exit after the delay. Fail open was rejected because the loop breaker wouldn't work in exactly the state where it is needed.
- **Q-D8b: No fallback key (plan decision, consistent with Q-D8a).** If WTS is unavailable, the guard returns `Unavailable` and fails closed. A session-id-plus-boot-time fallback was rejected: it adds an untested path for a case that isn't expected on supported Windows.

**Registry impact:** reads and writes one new HKCU REG_SZ value, `LaunchSession`. No admin. No HKLM. `StartupApproved` and `Run` are not touched.

#### 4.D8 implementation notes (developer, 2026-10-02, uncommitted)
- **Files.** New: `LaunchSessionGuard.cs` (`ILogonSessionKeyProvider`, `ILaunchSessionStore`, `LaunchClaim`, `LaunchSessionKey` with `Format`/`IsValid`, `LaunchSessionGuard.TryClaim`), `RegistryLaunchSessionStore.cs`, `WtsLogonSessionKeyProvider.cs`, `LaunchGate.cs` (`StartupAction`, `LaunchGate.Decide`). Changed: `NativeMethods.cs` (WTS P/Invoke and `WTSINFOW`), `AppRegistryPaths.LaunchSessionValueName`, `StartupSession.RunLaunchModeAsync` and `LaunchBlockedNotification`, `Program.Main` and `DecideStartup`, `Form1` (`LaunchBlocked`, an internal `Form1(IUserSettings)` constructor, and the launch-mode tail now calls `RunLaunchModeAsync`).
- **Struct layout (correction to the text above).** `WtsApi32.h` (SDK 10.0.19041) declares `WinStationName[WINSTATIONNAME_LENGTH]` (32), `Domain[DOMAIN_LENGTH]` (17) and `UserName[USERNAME_LENGTH+1]` (21). Only `UserName` has the +1. Size 216, `LogonTime` at offset 200. A test pins the size and offsets. The provider also refuses a buffer smaller than the struct. `WTSSessionInfo` = 24. The wtsapi32 imports use `DefaultDllImportSearchPaths(System32)`.
- **Key rules.** The regex uses `[0-9]` and `\z` instead of `\d` and `$`, so Unicode digits and a trailing newline don't match. Length is capped at 64. The rules from the session id and logon time (same session as the process, logon time > 0) are in the pure `WtsLogonSessionKeyProvider.KeyFrom`, which is tested without WTS. A REG_EXPAND_SZ or REG_BINARY `LaunchSession` is also malformed.
- **Launch mode is set from the gate, not from the arguments.** *(Superseded by review fix 3 below.)* `LaunchFromStartup = action is LaunchAndExit or BlockedAndExit`. If Program can't read "Launch programs on startup", the gate returns `Normal`, and Form1 can't turn the start into launch mode by reading the setting again later (the cache only stores successful reads). With the setting off this matches the old behaviour, because Form1 ANDed the flag with the setting anyway. Program passes its `UserSettings` to `new Form1(settings)`, so both share one cache.
- **Hardening beyond the plan.** `LaunchGate.Decide` catches an exception from `claim` and returns `BlockedAndExit` with an Error (`TryClaim` doesn't throw, so this is only a backstop). `RegistryLaunchSessionStore.Write` rejects text that isn't a valid key. `TryClaim` logs Info "--launch: first launch in this logon session" on `Claimed`. Key text is never logged.
- **Order of log lines.** *(Superseded by review fixes 4, 5 and 7 below.)* On `ExitAlreadyLaunched` only the Info line is written, without a `=== New session ===` header, because `StartSession` runs in `Form1.RunStartupAsync`. On `BlockedAndExit` the list still loads, as the plan says (the form is hidden). If that load also fails, the user sees both the load-failure and the blocked balloon.
- **Tests.** `LaunchSessionGuardTests.cs` (guard 1-11 and 18, key format, `KeyFrom`, layout, gate 12-16, launch mode 17, source guard 21) and `WtsLogonSessionKeyProviderTests.cs` (19). Test 19 is an `InteractiveSessionFact` that skips in session 0 or a non-interactive process, where WTS has no logon time. Test 20 and a check that the pattern matches `Program.cs` are in `SandboxGuardTests`, and the Forbidden list now rejects `new RegistryLaunchSessionStore(Registry.CurrentUser)` in tests. A source guard limits `new WtsLogonSessionKeyProvider(` to `Program.cs` and the smoke test file. 606 tests pass (5 runs), 0 warnings, `dotnet format` clean.
- **Review fixes (developer, 2026-10-02, uncommitted).** From the tester, code-inspector and security-analyser reviews of D8. The tester's `LaunchSessionReviewTests.cs`, the `LaunchSessionLog` collection and the `Program.DecideStartup(bool, IUserSettings, Func<LaunchClaim>)` seam are kept.
  1. **Security L1, one value per session.** Two sessions of the same user (console plus RDP) shared `LaunchSession` and could overwrite each other's record. Now each WTS session id has its own REG_SZ `LaunchSession.{id}` holding `v1:{id}:{logonTime}` (`AppRegistryPaths.LaunchSessionValueName(uint)`). `ILaunchSessionStore` is `string? Read(uint sessionId)` and `void Write(string key)`; `Write` derives the value name from the key, so it can only touch its own session's value. The guard gets the id with `LaunchSessionKey.TryGetSessionId`, and `IsValid` now also requires the id to fit a DWORD. A stored key for another session id counts as malformed. Ids are small and reused, so the set stays bounded. The pre-release single `LaunchSession` value is never read and is left alone (D8 was never released, so no build wrote it outside development machines).
  2. **Security L3.** `RegistryLaunchSessionStore.Read` checks `GetValueNames` for existence, then `GetValueKind`, and returns null with the malformed Warning for anything that isn't REG_SZ, before any `GetValue`. A planted REG_BINARY of any size is never read into memory.
  3. **Should 1, launch mode decided once.** Program sets `Form1.StartupAction = action`; `LaunchFromStartup` and `LaunchBlocked` are getter-only and derived through `LaunchGate.FormFlagsFor(action)`, the one mapping (in `LaunchGate` rather than `Program`, because Form1 uses it too). `StartupMode` no longer reads `GetLaunchProgramsOnStartup()`; the form reads settings only for Start to tray and the checkboxes. The tester's wiring test uses `FormFlagsFor` instead of its own copy.
  4. **Should 2, session header.** `LoggingService.StartSession(args)` moved from `Form1.RunStartupAsync` to `Program.Main`, right after the mutex is acquired in the `isNewInstance` branch and before `DecideStartup`. `ExitAlreadyLaunched` now has a header; the `!isNewInstance` path still logs none.
  5. **Should 4, one decision line.** `DecideStartup` logs the outcome of every `--launch` start once, after the per-step errors: Info `--launch: "Launch programs on startup" is off; nothing is launched`, Info "--launch: launching the startup programs", Info `AlreadyLaunchedMessage` (moved from `Program.Main`), or Error `BlockedMessage` "--launch blocked: nothing launched". `RunLaunchModeAsync` no longer logs its own blocked Error (it was a second Error for the same decision, and its "logon session could not be recorded" reason is wrong after fix 6), and `TryClaim` no longer logs Info on `Claimed`. Deviation from test 17: the one Error is now asserted on `DecideStartup`; test 17 asserts the tail doesn't log it again.
  6. **Tester finding, unreadable setting fails closed.** If `DecideStartup` can't read "Launch programs on startup", the result is now `BlockedAndExit` (the claim isn't made): nothing is launched, the blocked balloon shows, the app exits after the delay, and no window pops up at login. This replaces the "treat the setting as off" rule in Wiring above. The test is renamed `SettingReadThrows_IsBlocked_NoClaim_LogsTheCauseAndTheDecision_AndTheFormIsInBlockedLaunchMode`.
  7. **Should 3, one balloon.** `StartupSession.LoadProgramsAsync` has `notifyFailure` (default true); Form1 passes `!LaunchBlocked`, so a blocked start that also fails to load shows only the blocked balloon. The load failure is still logged.
  8. **Nitpicks.** The `LaunchGate` comment says "pure apart from calling claim and logging". `WTSINFOW`'s three name arrays are now `ushort[]` `ByValArray` padding (no `CharSet`), so station, domain and user names are never marshalled as text; size 216, `LogonTime` at 200, field names unchanged so the offset tests still apply. A comment notes the struct's single consumer.
  - **Tests added:** two-session ping-pong (each Claimed once, then AlreadyLaunched forever), a write never touches another session's value, the legacy value is not a record, the guard reads the id from the key, a key of another session is malformed, an id over a DWORD is invalid, a 1 MB planted REG_BINARY, a source check that `GetValueKind` precedes `GetValue`, blocked and load failed shows one balloon, Form1 wires `notifyFailure: !LaunchBlocked`, the header is logged in Program before the decision and not in Form1, Form1 reads the launch setting only for the checkbox, and one decision line per `--launch` outcome. 682 tests pass (5 runs), 0 warnings, `dotnet format --verify-no-changes` clean.

#### Tests (tester)
Guard, with a fake provider and `RegistryLaunchSessionStore(sandbox.Root)`:
1. No value → `Claimed`, and the value is REG_SZ equal to the key.
2. The value equals the key → `AlreadyLaunched`, and the value is unchanged (same kind and text).
3. A different key (an earlier logon) → `Claimed`, and the value is overwritten.
4. Two `TryClaim` calls with the same key → `Claimed`, then `AlreadyLaunched` (parent, then loop child).
5. The key changes between calls (sign-out/in or reboot) → `Claimed` both times.
6. Malformed value: REG_DWORD, REG_MULTI_SZ, REG_SZ `garbage`, or a 500-character string → `Claimed`, rewritten as a valid REG_SZ, one Warning that names only `LaunchSession`.
7. The provider throws, returns empty, or returns a key with the wrong format → `Unavailable`, and the value stays absent.
8. A fake store whose `Read` throws → `Unavailable`, and `Write` isn't called.
9. A fake store whose `Write` throws, and the sandbox app key opened read-only → `Unavailable`.
10. `Software\StartupController` is missing in the sandbox → `Claimed`, and the key is created.
11. After a claim, the other values under the sandbox app key (`ProgramOrder`, `EnabledPrograms`, `EnabledFingerprints`, settings DWORDs) are byte-identical.

Gate, pure:

12. No `--launch` → `Normal`, and the claim isn't called.
13. `--launch` with the setting off → `Normal`, and the claim isn't called.
14. `--launch`, setting on, `Claimed` → `LaunchAndExit`.
15. `AlreadyLaunched` → `ExitAlreadyLaunched`.
16. `Unavailable` → `BlockedAndExit`.

Launch mode, through `StartupSession.RunLaunchModeAsync` with a fake starter and notifier:

17. Blocked → no `Start` call, one notification, exit delay equal to `NotificationExitDelay`, one Error logged.
18. Manual Launch while `LaunchSession` holds the current key → started once (unaffected).

Real provider (read-only, safe to run):

19. `WtsLogonSessionKeyProvider` returns a key in the `v1:` format whose session id equals `Process.GetCurrentProcess().SessionId`, and returns the same value on two calls.

Source guards:

20. `new RegistryLaunchSessionStore(Registry.CurrentUser)` appears only in `Program.cs`.
21. `LaunchSessionGuard` and `TryClaim` are referenced only from `Program.cs` and the tests, never from `LaunchRunner` or `Form1`.

Manual (user, on a throwaway profile only, per the hard rule):
- Sign in: the list launches once. Run `StartupController.exe --launch` by hand: nothing launches and an Info line is logged.
- Sign out and in, or reboot: the list launches once.
- A second user through Fast User Switching: launches independently.
- RDP disconnect and reconnect: no rerun.

---

## Phase 5: .NET 10 LTS (D10)

**Answers (2026-10-01):** Q5.1 framework-dependent. Q5.2 option (a): drop the launch condition if the extension can't target .NET 10. Q5.3 ship with Phases 1-4 and D8. Q5.4 keep the 4.7.2 prerequisite for now. **Q5.5 is still open: ask the user before starting.** Q5.6 update `.claude/agents/*.md` with Phase 5. See "Decisions (2026-10-01, D8/Phase 5)". The original questions are kept below for the record.

- **Q5.1 Deployment model.** Framework-dependent (today's model; recommended) or self-contained? Framework-dependent keeps the MSI small, and Microsoft Update patches the runtime. Self-contained (about 60-100 MB) needs no runtime install, but every runtime CVE then needs a rebuild and re-release of a program that runs at every login. Self-contained would also need a publish profile (`Properties/PublishProfiles` is empty).
- **Q5.2 Installer fallback.** If the installed *Microsoft Visual Studio Installer Projects* extension can't set a .NET 10 launch condition, choose one: (a) remove the launch condition and rely on the apphost's "install .NET" dialog (recommended as a stopgap), or (b) move the installer to WiX or another tool (a separate project).
- **Q5.3 Ship together?** Should Phase 5 ship in the same release as Phases 1-4 and D8 (recommended: users install the new runtime once, before 2026-11-10), or as its own release?
- **Q5.4 Stale prerequisite.** The bootstrapper lists **.NET Framework 4.7.2** as a prerequisite (`SetupStartupController.vdproj:45-60` and `77-92`), which the app doesn't use. OK to remove it? (Recommended.)
- **Q5.5 Minimum OS.** .NET 10 supports only Windows 10 1607 and later, and Windows 11 (check on the supported-OS page). Keep `[assembly: SupportedOSPlatform("windows7.0")]` (recommended: no behaviour change, no new CA1416 warnings), or raise it to `windows10.0.14393`?
- **Q5.6 Agent prompts.** `.claude/agents/*.md` say ".NET 8 / net8.0-windows". Should they be updated with Phase 5? They are the user's configuration, so no agent edits them unasked.

**EOL check.**
- .NET 8 (LTS) support ends **2026-11-10**, which matches D10 and `SECURITY.md:38`. That is about six weeks from 2026-10-01.
- .NET 9 (STS, extended to 24 months) ends the same day, so it isn't an option.
- .NET 10 (LTS) was released on 2025-11-11 and is supported until November 2028. Check the exact date on the .NET support policy page and put it in SECURITY.md.
- The build machine already has SDK 10.0.303 and Microsoft.WindowsDesktop.App 10.0.11.

### 5.1 Retarget
- `StartupController/StartupController.csproj` and `StartupController.Tests/StartupController.Tests.csproj`: `net10.0-windows`.
- `Properties/AssemblyInfo.cs:7`: update the comment that says net8.0-windows. Keep the attribute unless the user answers Q5.5 with a new minimum OS.
- Keep the `RollForward` default (latest patch of 10.0). Optional: add a `global.json` pinning SDK `10.0.300` with `rollForward: latestFeature`, so CI and local builds agree.

### 5.2 Installer (`SetupStartupController/SetupStartupController.vdproj`)
- **Launch condition** (`:110-121`): ".NET Core" with `IsNETCore=TRUE` and `AllowLaterVersions=FALSE`, plus an `InstallUrl` built from `[NetCoreVerMajorDotMinor]`. The extension takes the version from the project's TFM. After retargeting, rebuild in VS, then check:
  - in the Launch Conditions editor, it requires the **.NET Desktop Runtime 10.0** (not the base runtime) for **x64** (`TargetPlatform 3:1`, `:234`);
  - in the built MSI (Orca), the LaunchCondition and AppSearch tables check 10.0.
  This needs an extension version that knows .NET 10. VS 2026 (v18) is in use. If it doesn't, remove the launch condition (Q5.2 answer) and rely on the apphost's ".NET required" dialog. Record this in the CHANGELOG and README as a known gap.
- **Project output** (`:776`): the cached `SourcePath` is `obj\Debug\net8.0-windows\apphost.exe`. After a rebuild it must say `net10.0-windows`, and a Release MSI must come from Release output. Check this in the built MSI's file table, or by installing and checking the version of `StartupController.dll`.
- **Prerequisite:** keep .NET Framework 4.7.2 as is (Q5.4 answer: not removed in this release).
- **Upgrade:** keep the `UpgradeCode`. Bump `ProductVersion` and `ProductCode` (`tools/Update-VdprojVersion.ps1 -UpdateProductCode`). `RemovePreviousVersions=TRUE` (`:217`) then replaces 1.0.x in place. HKCU data (settings, order, `LaunchSession`) is untouched. The Run value `"<path>" --launch` keeps working because the install path doesn't change.
- Users without the .NET 10 Desktop Runtime are stopped by the launch condition and given the download link. If the launch condition had to be dropped (Q5.2), the install succeeds and the apphost dialog appears at first start instead. If the runtime is later removed, the apphost shows a ".NET required" dialog at login. Put this in the CHANGELOG and README.

### 5.3 NuGet
- The app has no package references.
- The test project uses `coverlet.collector 6.0.4`, `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3` and `xunit.runner.visualstudio 3.1.4`. All of them run on net10.0.
- Run `dotnet list package --outdated` and `dotnet list package --vulnerable --include-transitive`. Bumping to `Microsoft.NET.Test.Sdk` 18.x is optional and goes in its own commit.
- The .NET 10 SDK audits transitive packages by default (`NuGetAuditMode=all`). New NU1901-NU1904 warnings would break the 0-warnings rule. Fix them by updating, not by suppressing.
- Keep VSTest for `dotnet test`. Don't opt in to Microsoft.Testing.Platform in this phase.

### 5.4 CA1416 / SupportedOSPlatform
- `GenerateAssemblyInfo=false`, so the SDK still won't emit the platform attribute. The manual `[assembly: SupportedOSPlatform("windows7.0")]` (`AssemblyInfo.cs:8`) stays until the user answers Q5.5. If they raise it, rebuild and fix any new CA1416 warnings in the same commit.
- The build must keep 0 CA1416 warnings in both projects. The test project is also `-windows`, so its Registry and WinForms calls stay valid.

### 5.5 Breaking changes to check (.NET 9 and .NET 10 pages, since we skip 9)
Read learn.microsoft.com/dotnet/core/compatibility/9.0 and /10.0, the Windows Forms, Core libraries and SDK sections. Known points:
- **BinaryFormatter removed (9.0). Checked, no impact.** The real resx entries are `bytearray.base64` icons (`Form1.resx:124`, `:141`). The `binary.base64` hits (`Form1.resx:22`, `Resources.resx:22`) are the standard resx header comment. There's no Clipboard or drag-and-drop use. Smoke check: the window icon and the tray icon load.
- **WinForms analyzer WFO1000 (9.0 SDK and later):** it flags public properties on controls and forms that have no serialization configuration. Item 17 makes `LaunchFromStartup` an internal property, and 4.D8's `LaunchBlocked` is internal too. Check that no other public properties on `Form1` trigger it.
- **WinForms 10:** dark mode (`Application.SetColorMode`) is opt-in, so don't call it. Check the ListView, the NotifyIcon balloons and the tray menu by eye.
- **Analyzers and formatting:** the 10 SDK raises the default analysis level, so new CA or IDE warnings may appear. Fix them, keep 0 warnings, and rerun `dotnet format --verify-no-changes` (formatter rules may shift).
- **Registry and Process:** no breaking changes are known for the APIs we use (`RegistryValueOptions.DoNotExpandEnvironmentNames`, `GetValueKind`, `CreateSubKey`, `ProcessStartInfo.UseShellExecute`/`WorkingDirectory`, `Win32Exception` 740). Confirm on the pages. The existing test suite covers them.
- **Named Mutex and EventWaitHandle:** confirm the `createdNew` semantics and `Local\` naming are unchanged. If .NET 10 adds options for named wait handles restricted to the current user or session, that is a possible later hardening for L-B, not part of this phase.
- **P/Invoke:** the `DllImport`s (kernel32, plus wtsapi32 from 4.D8) are unaffected.

### 5.6 Steps
0. Ask the user Q5.5 (minimum OS). If they want to keep it, proceed with the attribute unchanged.
1. Retarget both csproj files and update the AssemblyInfo comment (5.1).
2. `dotnet build -c Release` with 0 warnings, `dotnet test` all green, `dotnet format --verify-no-changes` clean. Fix any analyzer or WFO findings.
3. Package checks (5.3).
4. In VS: rebuild the installer, check the launch condition (drop it per Q5.2 if the extension can't target .NET 10) and the project output, leave the 4.7.2 prerequisite, bump the version (5.2).
5. Update `.claude/agents/*.md` from ".NET 8 / net8.0-windows" to ".NET 10 / net10.0-windows" (Q5.6, user-approved for Phase 5 only).
6. Commit "Phase 5: .NET 10".
7. Run the security-analyser on the installer, NuGet CVEs and the runtime change. The documenter updates the docs listed below.

### 5.7 Verification
- The output `StartupController.runtimeconfig.json` names `Microsoft.WindowsDesktop.App` 10.0.
- Clean VM with only the .NET 10 Desktop Runtime: install the MSI, start, start to tray, Launch on a throwaway Run entry, and `--launch` on a throwaway profile.
- VM with only .NET 8: the MSI refuses to install and shows the 10.0 link. If the launch condition was dropped (Q5.2), it installs and the app shows the apphost ".NET required" dialog at start.
- Upgrade over the 1.0.2 MSI: one entry in Apps & features, settings and order preserved, and the Run value still points at the installed exe.

### 5.8 Risks
- The installer extension may not support a .NET 10 launch condition. Mitigation (user decision Q5.2): drop the launch condition and rely on the apphost dialog. Check this **first**, before the other steps, because it is the long pole for the 2026-11-10 date.
- Users must install a new runtime. The launch condition blocks a broken install, but CHANGELOG and README must say so clearly.
- Analyzer and formatter churn can produce a large but mechanical diff. Keep it in the Phase 5 commit and separate from behaviour changes.
- Phase 5 touches only csproj, AssemblyInfo, vdproj and `.claude/agents/*.md`, so it barely conflicts with Phase 4 or 4.D8 and can run in parallel if the schedule needs it.

---

## Docs to update (documenter)
- `CHANGELOG`: 2.1 behaviour change (entries with no approved value and the app's own entry no longer listed; the double-launch fix), the 2.2 storage change (`ProgramOrder`/`EnabledPrograms`/`EnabledFingerprints`, legacy `StartupOrder` read once for migration and left in place; a rollback gives the order as of migration), AutoSave no longer shows a balloon on success. D7: an enabled program whose Run command changes stops launching and shows "Changed – re-enable to launch" until it is re-enabled. 3.1a: entries that point at StartupController itself are never launched.
- `StartupController/PRD.MD`: §3 was already reworded by the planner (D1). Documenter: add `ProgramOrder`, `EnabledPrograms` and `EnabledFingerprints` to Registry Usage. Note that `EnabledFingerprints` stores only a SHA-256 hash of each enabled program's Run data, never the command.
- `README.md` and `StartupController/README.MD`: explain what "Enabled" means in the app compared with Task Manager. Mention that entries enabled in Task Manager don't appear in the list.
- In-app help (`Form1.ShowHelp`): the same explanation, plus what "Changed – re-enable to launch" means (D7).
- `CONTRIBUTING.md`: how to run `dotnet test`, and the registry sandbox rule.
- 4.D8: SECURITY.md (the per-logon-session `--launch` loop breaker; fail closed when the session can't be recorded), PRD Registry Usage (the `LaunchSession` REG_SZ, which holds only a session id and logon time), README and in-app help (a second `--launch` in the same logon session does nothing; Launch still works), CHANGELOG.
- Phase 5: README and `StartupController/README.MD` (".NET 8" → ".NET 10", runtime download), `PRD.MD:10` platform line, SECURITY.md:38 (the .NET 10 support end date), CONTRIBUTING (SDK 10 needed), CHANGELOG (the new runtime requirement). `.claude/agents/*.md` are updated by the developer in Phase 5 (Q5.6, approved). If the launch condition is dropped (Q5.2), README and CHANGELOG say that the installer doesn't check for the runtime.

## Risks (cross-cutting)
- Phase 1 changes the `Form1` constructor. The designer needs the parameterless constructor, so keep it.
- 2.1 plus 2.2 change what users see on the first run after upgrading. The CHANGELOG must explain it.
- 3.1 prefix resolution needs `security-analyser` review (unquoted-path hijack).
- D7 fails closed: legitimate app updates that rewrite their Run value stop launching until re-enabled. The UI status and CHANGELOG must make this obvious.
- 3.1a doesn't catch wrapper commands or renamed copies (residual risk).
- Run `security-analyser` on Phases 2, 3.1, 3.1a, 3.2 and 3.3 (registry, process launch, IPC).
- 4.D8 fails closed: a registry or WTS failure costs that logon's automatic launch. It is visible (Error log and balloon), and Manual Launch still works. Run `security-analyser` on 4.D8 and on item 18 (logging).
- Phase 5 depends on the installer extension supporting .NET 10, and the .NET 8 end of support (2026-11-10) is a fixed date.
