# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions come from `version.json`.

## [Unreleased]

### Added
- Starting StartupController a second time restores the existing window from the tray instead of doing nothing.
- `--launch` logs "Launched n of m" when it finishes.

### Changed
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
- **Enabled means StartupController launches it.** The app only changes its own launch list. It never writes Windows' `StartupApproved` state.
- **Enabled programs are bound to their Run command.** Only a hash (fingerprint) is stored, never the command.
  - If the command or the value type changes (REG_SZ vs REG_EXPAND_SZ), the entry shows "Changed – re-enable to launch" and is not launched. The status shows until the next save, after which the entry is shown as Disabled.
  - A reinstall with identical data keeps working.
  - On upgrade, enabled programs that are currently listed keep launching. Their fingerprints are recorded at the first save.
  - App updates that rewrite their Run entry need re-enabling.
- Settings are now cached for the life of the process.
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
- `--launch` reports a load failure instead of exiting silently.
- No window flash when the app starts to the tray.
- A settings checkbox reverts and shows a notification if saving fails.
- "Launch Enabled Programs On System Startup" works even if the Run key does not exist yet.
- If another program holds the app's activation event or mutex name, the app logs it and runs without restore-on-second-launch, or exits cleanly.
