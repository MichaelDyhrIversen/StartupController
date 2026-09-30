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
- Launch logging still records command lines. This remains until Phase 4 item 18 of `docs/plans/2026-09-30-code-review-fixes.md`.
- Tests use a throwaway sandbox under `HKCU\Software\StartupController.Tests` and a fake process starter. Leftover sandbox keys are harmless.
- `InternalsVisibleTo` for the test assembly is a test convenience, not a security boundary. The test assembly is not shipped in the installer.
