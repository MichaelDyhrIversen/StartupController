# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions come from `version.json`.

## [Unreleased]

### Added
- **Return to Windows when uninstalling.** The installer now runs a small helper (`StartupController.ReturnToWindows.exe`, .NET Framework 4.6.2) when StartupController is uninstalled. Upgrades, repair and modify do nothing.
  - With the installer window (UI level 3 to 5) it asks whether all taken-over programs should be enabled and started by Windows again. No answer within 120 seconds, or a prompt that can't be shown, counts as Yes. SYSTEM, session 0 and non-interactive runs skip the prompt and return.
  - Silent uninstall (`/qn`, `/passive`) returns without asking. The installer property `RETURNTOWINDOWS=0` leaves the entries disabled, `RETURNTOWINDOWS=1` returns without asking.
  - Yes returns every recorded entry that is still a string value in Run, including entries that are Disabled or Changed in the app. Entries you disabled in Task Manager before the takeover are never touched. Only the uninstalling user's profile is handled.
  - Log: `%LOCALAPPDATA%\StartupController\logs\uninstall.log` (names only), written only when the uninstall isn't elevated. Recovery: Task Manager > Startup apps > Enable.
  - Build: `tools\Patch-UninstallCustomAction.ps1 -Verify` checks the uninstall custom action after every installer build. A failed check fails the build and deletes the MSI.
- Starting StartupController a second time restores the existing window from the tray instead of doing nothing.
- `--launch` logs "Launched n of m" when it finishes.
- New Help text and tooltips.
- **`--launch` runs at most once per Windows logon session.** A later `--launch` in the same session logs one line and exits with nothing launched. Signing out and in, or rebooting, allows a new run. The Launch button is unaffected.
  - The session is recorded per Windows session in the HKCU REG_SZ value `Software\StartupController\LaunchSession.{session id}`. It holds only the session id and logon time. Console and Remote Desktop sessions of the same user don't interfere. No admin rights are needed.
  - Fail closed: if the session can't be identified or recorded, or "Launch programs on startup" can't be read, `--launch` launches nothing. It logs "--launch blocked: nothing launched", shows one balloon ("Startup programs were not launched automatically. Open StartupController and use Launch.") and exits after about 4 seconds. No window opens at login.

### Changed
- **Installer rebuilt with WiX Toolset 6; no Visual Studio needed.** `SetupStartupController.wixproj` replaces the Visual Studio Installer Projects `.vdproj` and builds with `dotnet build SetupStartupController -c Release` (or the VS Code task "Build installer (MSI)"). The uninstall custom action is authored correctly in `Package.wxs`, so the MSI is no longer patched after the build, only verified. The install is now explicitly per-machine (Program Files, all-users Start menu), the install folder can't be redirected from the `msiexec` command line, and the .NET 10 Desktop Runtime (x64) check is kept. Same UpgradeCode, so it upgrades per-machine installs, and a rebuild with the same version replaces the installed one. Setup refuses to run while an older build installed "Just me" (per-user) is present, because an upgrade can't remove it and uninstalling it later would delete the new install's files: uninstall it first. `ALLUSERS` can't be overridden. Releases must raise version field 1, 2 or 3 (Windows Installer ignores the 4th).
- **Silent takeover of startup apps (behaviour change, reverses the earlier "never changes Windows' startup configuration" rule).** Each time StartupController loads its list (normal start and `--launch`), it now silently takes over every `HKCU\...\Run` entry that Windows would start. It disables the entry in Windows (`StartupApproved\Run`, `03` plus a FILETIME, the value Task Manager writes), lists it as Enabled and appends it to the end of the saved order. Earlier builds only listed entries that were already disabled in Windows.
  - **Gate:** it takes over only while "Launch Enabled Programs On System Startup" is on and StartupController's own Run entry is a string value that points to this exe, has `--launch` and is enabled in Windows. Otherwise nothing is taken over.
  - **Order and state:** new names go last. A name the app already stores keeps its position and becomes Enabled. Changed entries stay Changed. Nothing is written when there is nothing to take over.
  - **Task Manager now shows taken-over apps as Disabled.** Disabling a program in StartupController now means it doesn't start at all, because Windows no longer starts it either. The D7 consequence: an updater that rewrites its Run command makes the entry Changed, and nobody starts it until you enable it again.
  - **First logon after a takeover is started by Windows.** Programs taken over during a `--launch` run are not started by the app in that logon. Later logons are started by the app, in your order.
  - **No UAC at logon.** `requireAdministrator` exes and "Run as administrator" shortcuts are not started at logon (logged as "requires elevation, not started"). Manual Launch still elevates. A shortcut whose target exe requires elevation by its manifest, or a `.bat`/`.cmd` that elevates itself, can still show a UAC prompt at logon.
  - **Warning balloon:** after a normal start, a warning balloon appears if taken-over programs would start nowhere (StartupController's own Run entry is missing or disabled). It is shown even with "Silence Notifications" on.
  - New value `HKCU\Software\StartupController\TakenOverPrograms` (REG_MULTI_SZ, names only) records what was taken over.
  - Downgrading to an older build leaves the taken-over entries disabled in Windows. The older build still launches them from its enabled list.
- **Requires the .NET 10 Desktop Runtime (x64) and Windows 10 version 1607 or later.** The app now targets .NET 10 (LTS) instead of .NET 8, whose support ends on 2026-11-10. Install the .NET 10 Desktop Runtime before upgrading. Windows 7, 8 and 8.1 are no longer supported.
  - The installer checks for the .NET 10 Desktop Runtime and stops with a download link if it is missing. Upgrading replaces the previous version in place. Settings, order and the startup entry are kept.
  - If the runtime is removed later, Windows shows a ".NET required" dialog when StartupController starts.
- **The installer always installs to Program Files.** The folder selection page is gone. A folder that other users can write to would let them replace the program, and it runs at your login. The page also held the "Everyone / Just me" choice, so the Start menu shortcut is now created for the installing user only.
- **Run command parsing (behaviour change).** A Run value is split into program and arguments as follows:
  - Quoted paths are used as written. Quote any path that contains spaces.
  - An unquoted command ends at the first token ending in .exe, .com, .bat, .cmd or .lnk. A folder name that contains one of these extensions must be quoted.
  - An unquoted command with arguments and no such extension gives "Executable not found".
  - Relative paths are never started.
  - Environment variables (`%VAR%`) are expanded once.
  - Commands longer than 32767 characters are rejected.
- **Launching (behaviour change).** Existing .exe and .com files start directly, with no shell. A UAC prompt still appears when the program needs elevation. A missing command is never handed to the shell, and bare names are resolved from the system directory.
  - URL and AppUserModelID Run values are no longer shell-started.
- StartupController never launches itself, whether from the Run list or manually.
- **Launch timing.** In `--launch` mode each launch has a 30 second timeout, and a launch that completes late is logged. Manual Launch waits as long as needed, and the Launch button stays disabled until the launch finishes.
- **`--launch` mode** waits about 4 seconds for balloon notifications before exiting.
- **Order storage moved.** The order is now stored in three REG_MULTI_SZ values under `HKCU\Software\StartupController`: `ProgramOrder`, `EnabledPrograms` and `EnabledFingerprints`. The legacy `StartupOrder` value is read once to migrate (split on `;` and trimmed, as before) and is left in place.
  - Rolling back to 701437a: the order is as it was at migration time. Edits made on the rolled-back build are ignored after you upgrade again.
- **Enabled means StartupController launches it.** (The earlier note that the app never writes `StartupApproved` no longer applies, see the silent takeover above.)
- **Enabled programs are bound to their Run command.** Only a hash (fingerprint) is stored, never the command.
  - If the command or the value type changes (REG_SZ vs REG_EXPAND_SZ), the entry shows "Changed – re-enable to launch" and is not launched. The status shows until the next save, after which the entry is shown as Disabled.
  - A reinstall with identical data keeps working.
  - On upgrade, enabled programs that are currently listed keep launching. Their fingerprints are recorded at the first save.
  - App updates that rewrite their Run entry need re-enabling.
- **Logging (security).**
  - Command-line arguments are never logged or shown in balloons. Unresolved commands show only their first path part. Error messages no longer include arguments.
  - Log escaping covers line breaks, tabs, control characters, bidi and invisible formatting characters, and invalid Unicode. Names with invalid Unicode are now logged instead of silently skipped.
  - Log rotation at 1 MB keeps 3 files, with about a minute of backoff if rotation fails.
  - The log session header is written for every first instance, including early exits.
- Settings are now cached for the life of the process.
- The selection is kept after move, enable, disable and reload.
- AutoSave no longer shows a balloon after each successful save. A failed autosave shows a balloon and turns the Save button salmon. Switching AutoSave on saves pending changes immediately.
- Closing with unsaved changes: Cancel keeps the app open. Yes waits for the save, and if it fails the app stays open with an error. On Windows shutdown or logoff there is no prompt. Unsaved changes are discarded and logged.

### Fixed
- **No double launches.** Entries that Windows already runs are no longer listed: those with no `StartupApproved` value, or an even first byte such as 0x06. The app's own entry is never listed. Run values that are not strings are never listed.
- Programs disabled in the app keep their position in the order.
- Program names are matched case-insensitively, and names containing `;` work.
- A removed Run entry no longer empties the list or launches the wrong program.
- Saved programs whose Run entry disappears are kept hidden and return in place when listed again.
- A malformed `ProgramOrder`, `EnabledPrograms` or `EnabledFingerprints` enables nothing and no longer falls back to the legacy value. Lists are capped at 1024 names of up to 260 characters.
- Every successful launch is now logged.
- The logs folder is recreated if it was deleted.
- View Logs and Open Logs warn with the log path if the log can't be opened.
- `--launch` reports a load failure instead of exiting silently.
- No window flash when the app starts to the tray.
- A settings checkbox reverts and shows a notification if saving fails.
- "Launch Enabled Programs On System Startup" works even if the Run key does not exist yet.
- If another program holds the app's activation event or mutex name, the app logs it and runs without restore-on-second-launch, or exits cleanly.
