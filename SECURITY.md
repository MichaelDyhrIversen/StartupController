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
- The app launches only HKCU `Run` entries that Windows has disabled (odd first byte in `StartupApproved\Run`) and that the user enabled in the app. It never launches anything elevated and never writes `StartupApproved`.
- An entry shown as "Disabled" in Task Manager may still be launched by StartupController, because Windows' disabled state is what lets the app take over launching it.
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
- Platform: .NET 8 support ends 2026-11-10. A move to .NET 10 is planned.
- Logging guarantees:
  - Run command-line arguments are never written to the log. Unresolved commands are shown by their first path part only.
  - Logged values are escaped: line breaks, tabs, control characters, bidi and invisible formatting characters, and invalid Unicode, so one log call stays on one line.
  - Logs are in `%LOCALAPPDATA%\StartupController\logs` with the user's ACLs. They rotate at 1 MB and keep 3 files.
  - The log is not tamper-evident against processes running as the same user.
  - View Logs opens the file with the user's `.log` file association. This is acceptable because the app runs asInvoker (no elevation).
- Tests use a throwaway sandbox under `HKCU\Software\StartupController.Tests` and a fake process starter. Leftover sandbox keys are harmless.
- `InternalsVisibleTo` for the test assembly is a test convenience, not a security boundary. The test assembly is not shipped in the installer.
