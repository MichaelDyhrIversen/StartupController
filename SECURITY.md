# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.0.2   | :white_check_mark: |
| < 1.0.2   | :x:                |

## Reporting a Vulnerability

The StartupController team and community take security bugs in StartupController seriously. We appreciate your efforts to responsibly disclose your findings, and will make every effort to acknowledge your contributions.

To report a security issue, please use the GitHub Security Advisory ["Report a Vulnerability"](https://github.com/michaeldyhriversen/StartupController/security/advisories/new) tab.

The StartupController team will send a response indicating the next steps in handling your report. After the initial reply to your report, the security team will keep you informed of the progress towards a fix and full announcement, and may ask for additional information or guidance.

## Escalation

If you do not receive an acknowledgement of your report within 6 business days, or if you cannot find a private security contact for the project, you may escalate to the OpenJS Foundation CNA at `security@lists.openjsf.org`.

If the project acknowledges your report but does not provide any further response or engagement within 14 days, escalation is also appropriate.

## Trust model

- Everything under HKCU is writable by any process running as the same user. StartupController gives no protection against malware running as the same user.
- The app launches only HKCU `Run` entries that Windows has disabled (odd first byte in `StartupApproved\Run`) and that the user enabled in the app. It never launches anything elevated at logon (see "Silent takeover" below).
- An entry shown as "Disabled" in Task Manager may still be launched by StartupController, because Windows' disabled state is what lets the app take over launching it.
- Silent takeover (reverses the earlier "never writes `StartupApproved`" rule): when the launch setting is on and the app's own Run entry is valid (REG_SZ or REG_EXPAND_SZ, this exe, `--launch`, enabled in Windows), every load disables the Windows-run HKCU `Run` entries in `StartupApproved\Run` (REG_BINARY, `03 00 00 00` plus a FILETIME) and enables them in the app with their current fingerprint. It never writes the app's own entry, `Run`, HKLM or `StartupApproved\Run32`, and never deletes values. A `Run` value tampered with just before a load is taken over with its current fingerprint (same trust level as `Run` itself). The names are recorded in `HKCU\Software\StartupController\TakenOverPrograms` (REG_MULTI_SZ, names only) before `StartupApproved` is written. If that value has the wrong kind, nothing is taken over.
- Single point of failure: after a takeover, the app's own `Run` entry is the only thing that starts those programs. If it is removed or disabled, or the `HKCU\Software\StartupController` store is deleted, the taken-over programs start nowhere. A warning balloon (shown even with notifications silenced) reports the first case. Also, an entry that becomes Changed (D7) is started by nobody until the user re-enables it.
- D7 protects only entries that stay disabled in Windows. A same-user process that re-enables an entry in `StartupApproved` makes Windows run its current command without a fingerprint check, until the next load takes it over again. The uninstall return (below) also re-enables Changed entries.
- Elevation at logon: `--launch` never shows a UAC prompt. `ERROR_ELEVATION_REQUIRED` is not retried through the shell, and a `.lnk` with the `SLDF_RUNAS_USER` flag is not started at logon (both are logged). Manual Launch keeps the UAC fallback. Remaining gaps: a shortcut whose target exe requires elevation by its manifest, a `.bat`/`.cmd` that elevates itself, and bare names resolved by the shell can still show a UAC prompt at logon.
- Return to Windows at uninstall (D-T3/D-T4): the MSI runs `StartupController.ReturnToWindows.exe` (net462) as an Uninstall custom action with `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`. It writes `02` plus 11 zero bytes to `StartupApproved\Run\<name>` for each recorded name that is still a string value in `Run` (never the app's own entry), then removes the returned names from `TakenOverPrograms`. It never writes `Run` or the app's order values and never deletes approved values.
  - There is deliberately no check of the app's enabled flag, the D7 fingerprint or the current `StartupApproved` bytes (user decision D-T4). A program the user switched off, re-disabled in Task Manager, or whose command changed is enabled in Windows again, and Windows then runs the current command. This is accepted: same trust level as `Run`, and a same-user writer could do this already. Entries disabled before the takeover are never recorded and never touched.
  - Prompt: only when the UI level is exactly 3, 4 or 5, and not as SYSTEM, in session 0 or non-interactively. 120 s timeout. Timeout, failure or no prompt means return (silent default), because leaving the entries disabled would stop them everywhere. Only an explicit No leaves them. `RETURNTOWINDOWS=0` or `1` (public MSI property) overrides the prompt, so anyone who can run the uninstall with properties (an administrator) can choose either.
  - The helper runs impersonated as the uninstalling user (custom action, below), so it touches only that user's HKCU. Other profiles are not handled. If impersonation were missing it would run as SYSTEM and find nothing, and it logs a Warning.
  - The elevated uninstall writes no log: `uninstall.log` is written only when the token is not elevated or SYSTEM, to avoid writing to a user-controlled path with high rights. When it is written it refuses reparse points and files with several hard links. Names only, escaped like the main log.
  - The helper always exits 0 (the custom action ignores the exit code), so a failed return never blocks the uninstall. The cost is that a failure is visible only in `uninstall.log` or in Task Manager.
  - Uninstall custom action: `SetupStartupController\Package.wxs` (WiX) authors it deferred, impersonated (not NoImpersonate, so it never runs as SYSTEM with the wrong HKCU), exit code ignored, and before RemoveFiles. After every build `tools\Patch-UninstallCustomAction.ps1 -Verify` checks the type, the arguments, the condition, the sequence position (between InstallInitialize and InstallFinalize, before RemoveFiles) and that `RETURNTOWINDOWS` is a secure property. On any failure the build deletes the MSI and fails. Do not distribute an MSI that did not come from a successful, verified build.
- Fingerprints (`EnabledFingerprints`) bind each enabled name to a SHA-256 hash of its Run value (data and value type). This detects changes made by installers or updates and reuse of a Run value name. It does not stop a same-user attacker (the hash is unkeyed) and does not detect an exe replaced at the same path.
- Migration: while `EnabledFingerprints` is absent, previously enabled names that are currently listed are accepted. Their hashes are recorded at the first save.
- Changed entries ("Changed – re-enable to launch") are never auto-launched. Manual Launch can still start them and does not approve them.
- Run command parsing: an unquoted command ends at the first .exe, .com, .bat, .cmd or .lnk token, so a folder name containing such an extension must be quoted. Unquoted extensionless commands with arguments give "Executable not found". Relative paths are never started, `%VAR%` is expanded once, and commands over 32767 characters are rejected. Recommendation: always quote Run paths.
- Existing .exe and .com files are started directly, without a shell. This skips Windows' Mark-of-the-Web/zone "Open File - Security Warning" prompt. This is accepted, because the D7 approval (fingerprint plus user enable) covers it. A UAC prompt still appears when the program requires elevation. A missing command is never passed to the shell, and bare names are resolved from the system directory. URL and AppUserModelID Run values are not shell-started.
- Self-launch guard: StartupController refuses to launch itself, but residual gaps remain: `cmd /c` and `.lnk` wrappers, renamed copies anywhere (not only on another volume), and bare-name PATH/App Paths lookup. The singleton mutex prevents relaunch loops, and the limit of one `--launch` per Windows logon session (D8, implemented) stops loops that the guard can't see.
- D8 `LaunchSession.{id}` (HKCU `Software\StartupController`, REG_SZ, one per WTS session id) is untrusted same-user input. A writer can cause at most one extra run, or suppress the automatic run. That is the same trust level as editing `Run`. Only the session id and logon time are stored.
- D8 fails closed: if the session can't be identified or recorded, or "Launch programs on startup" can't be read, `--launch` launches nothing, logs an Error, shows one balloon and exits. The Launch button still works.
- D8 depends on WTS session information. Behaviour with the Remote Desktop Services (TermService) service disabled is not verified.
- Any process in the user's session can bring the window to the front by signalling `Local\StartupControllerActivate`. The signal is rate-limited and can do nothing else.
- D9: `%VAR%` in REG_SZ and REG_EXPAND_SZ Run values is expanded from the current environment. `HKCU\Environment` is not covered by the D7 fingerprint. It has the same trust level as Run.
- The D7 fingerprint covers the unexpanded Run string and its value kind only. It does not cover environment variables, PATH/App Paths, which file the path resolves to, or file contents.
- Platform: the app targets .NET 10 (LTS, supported until November 2028), framework-dependent, so runtime security fixes come from updating the installed .NET runtime (for example through Microsoft Update), not from a new StartupController release. The minimum OS is Windows 10 version 1607 (build 14393).
- Install location (L1): the integrity of StartupController depends on its install folder being writable by administrators only. The installer is per-machine, always installs to `Program Files\StartupController` and has no folder-selection page. The folder's directory Id (`AppInstallFolder`) is a private MSI property, so it can't be redirected from the `msiexec` command line (unlike the old `TARGETDIR`). Copying the files elsewhere by hand is unsupported: a user-writable folder would allow exe or DLL planting (including `hostfxr.dll`).
- Installer scope: per-machine only (`ALLUSERS=1` is enforced by a launch condition). Setup refuses to run while an older "Just me" build is installed for the current user (it is found by its stable component GUIDs), since that product would share the install folder and its later removal would delete this install's files. Releases must raise version field 1, 2 or 3; a same-version rebuild replaces the installed product.
- Signing: the MSI and the binaries are not Authenticode-signed yet, so UAC shows "Unknown publisher" and a tampered MSI can't be told apart from a genuine one. Only install an MSI you built yourself or got directly from the maintainer.
- Runtime patching (I1): the app is framework-dependent. .NET 10 Desktop Runtime security patches arrive through Microsoft Update only if "Receive updates for other Microsoft products" is turned on in Windows Update settings. Otherwise install the patches manually. The default roll-forward uses the newest installed 10.0.x patch. .NET 10 support ends in November 2028, and after that the app must be retargeted.
- DOTNET_ROOT (I2): the apphost honours `DOTNET_ROOT` and `DOTNET_ROOT_X64`, for example set from `HKCU\Environment`. A writer running as the same user can make the app load a different runtime. This is the same trust level as editing `Run` and is not covered by D7.
- Mutex squatting: a process in the same session can suppress the logon launch by creating `Local\StartupControllerSingletonMutex` before the app does. The app logs this. It is the same trust level as any same-user process.
- Logging guarantees:
  - Run command-line arguments are never written to the log. Unresolved commands are shown by their first path part only.
  - Logged values are escaped: line breaks, tabs, control characters, bidi and invisible formatting characters, and invalid Unicode, so one log call stays on one line.
  - Logs are in `%LOCALAPPDATA%\StartupController\logs` with the user's ACLs. They rotate at 1 MB and keep 3 files.
  - The log is not tamper-evident against processes running as the same user.
  - "Open in editor" (in the log viewer) opens only the current log with the user's `.log` file association. This is pre-existing behaviour and acceptable because the app runs asInvoker (no elevation).
  - The folder `%LOCALAPPDATA%\StartupController\logs` is protected only by the per-user `%LOCALAPPDATA%` permissions. Log content is treated as untrusted when displayed.
- Log viewer and Help window:
  - The log viewer is read-only. It opens the file with sharing for reading and writing, holds no handle between refreshes (so logging and rotation are never blocked), never creates the log or its folder, and reads at most the last 4 MiB and 50,000 lines.
  - Log content is shown as escaped plain text in a list. It is never opened, run or turned into links, and no log line has an action that opens a path.
  - Copy puts the line on the clipboard with control and bidi characters escaped per field. App-written lines are unchanged.
  - Help is compiled-in plain text. There is no RTF and no URL detection.
  - "Open log folder" opens only `%LOCALAPPDATA%\StartupController\logs` by shell execute, after a `Directory.Exists` check. Accepted: a process running as the same user could swap the folder in between (a race), but that crosses no privilege boundary. Hardening (start `explorer.exe` with `UseShellExecute = false`) is deferred.
  - Neither window opens in `--launch` mode.
- Logging of the takeover and the uninstall helper: names only, never commands, paths or byte values.
- Tests use a throwaway sandbox under `HKCU\Software\StartupController.Tests` and a fake process starter. Leftover sandbox keys are harmless.
- `InternalsVisibleTo` for the test assembly is a test convenience, not a security boundary. The test assembly is not shipped in the installer.
