# Help window with sections, and in-app log viewer

Status: implemented, pending manual checks M-H1, M-H2, M-L1 to M-L6 (planner, 2026-10-03), branch `code-review-fixes` at 58cb685. Open questions Q-H1 to Q-H6 decided 2026-10-03: all recommended options (see Open questions).

User request (verbatim): "I would like the Help section not to be just an alert, but more like a real help with sections. And also I would like to open the log file within the app itself."

One plan, two parts. They share the same wiring in `Form1` (buttons, tray menu, single-instance child windows, closing them with the main form), and the same rules (no process launch from content, `IProcessStarter` seam, plain-text display).

## Goal

**Part A (Help)** serves PRD §6 "Explanation/Help" and the success criterion "Users understand how to use the application via built-in help". Replace the single `MessageBox` with a resizable Help window that has a section list and a readable content pane, opened with the Help button or F1. The content describes actual behavior only (PRD §1-§5, silent takeover, uninstall return, tray, logging).

**Part B (Log viewer)** serves the non-functional requirement "All actions should be logged for troubleshooting". Today the log can only be opened in an external editor. Add a read-only log viewer window inside the app with refresh, level filter, search, copy and "Open log folder". It never changes the log and never runs anything from it.

Neither part changes the ordering model, the takeover or any registry data.

## Current behavior

Help:
- `Form1.HelpText` (`StartupController/Form1.cs:587-620`) is an `internal const string` of about 30 lines with 8 implicit sections ("Which programs are listed", "Enabled and Disabled", "Taking over and uninstalling", "Order", "\"Changed - re-enable to launch\"", "Run command format", "Settings", "Launch and View Logs"). Non-ASCII characters are `\u` escapes on purpose (arrows `↑ ↓ ⇈ ⇊`, en dash `–`) so the source file encoding can't break them.
- `Form1.ShowHelp` (`Form1.cs:622-626`) logs "Help shown" and calls `MessageBox.Show(this, HelpText, "StartupController Help", OK, Information)`. Wired at `Form1.cs:79` (`btnHelp.Click`). Tooltip "How StartupController works" (`Form1.cs:640`).
- `btnHelp` is in `Form1.Designer.cs:187-194` (660,292; 120x32; anchored top-right). No F1 handling exists (`KeyPreview`/`ProcessCmdKey` not used).
- Inaccuracy in today's text: "Which programs are listed / Every program in your Run key" contradicts PRD §1 (only entries disabled in Windows plus the taken-over ones; non-string values and the app's own entry are skipped). The new content must fix this.
- Tests pin the text: `Phase4Tests.HelpText_CoversTheMainTopics` (`StartupController.Tests/Phase4Tests.cs:524-532`) and `Phase4FixTests.HelpText_HasTheExactArrowAndDashCharacters` (`StartupController.Tests/Phase4FixTests.cs:250-259`). Comment at `Form1.cs:587` says to keep it in line with README "Usage".

Logging:
- `LoggingService` (`StartupController/LoggingService.cs`): file `%LOCALAPPDATA%\StartupController\logs\startupcontroller.log` (`:24-25`), exposed as `internal LogFilePath` (`:48-51`). UTF-8 without BOM, invalid UTF-16 tolerated (`:21`).
- Format (`:79-80`): `yyyy-MM-dd HH:mm:ss.fff<TAB>LEVEL<TAB>Category<TAB>field<TAB>field...`. Levels written: `INFO`, `WARN`, `ERROR`, `LAUNCH`, `SESSION` (`:170-201`). Categories: `Logger`, `App`, `Program`. `LAUNCH` lines have 4 fields: name, exe path, `SUCCESS`/`FAILURE`, details (`:189`). Every field goes through `LogEscape.Escape` (`LogEscape.cs:15-51`): `\r \n \t` become literal `\r \n \t`, control, bidi/format characters and lone surrogates become `\uXXXX`. Command-line arguments are never logged (`:8`, `:192`).
- Writes: `File.AppendAllText` under `_lock`, opened and closed per line (`:86`). `AppendAllText` opens with `FileShare.Read`, so a reader must open with `FileShare.ReadWrite` or the writer's open fails (and the line is silently lost, `:96-99`).
- Rotation (`:107-168`): at 1 MiB, `File.Move` to `.1.log`, `.1` to `.2` (3 files kept). After a failed rotation the next try waits 1 minute or until the file doubled, so the current file can be larger than 1 MiB. A reader holding a handle without `FileShare.Delete` makes `File.Move` fail.
- Opening today: `LoggingService.OpenLogFile(IProcessStarter, out error)` (`:204-227`) creates the file if missing and shell-executes it (default editor) through the `IProcessStarter` seam (`ProcessStarter.cs`). `Form1.OpenLogs` (`Form1.cs:648-652`; superseded, implemented: removed, the viewer's "Open in editor" calls `OpenLogFile`) calls it and shows a warning with the path on failure. Reached from `btnViewLogs` (`Form1.cs:111`, `Form1.Designer.cs:178-185`) and the tray item "Open Logs" (`Form1.cs:102-110`; tray menu is "Exit", "Open Logs").
- Tests: `Phase4ReviewTests.OpenLogFile_*` (`:247-278`, `:527-532`) and `Phase4Tests.OpenLogFile_TakesTheProcessSeam_AndDoesNotSwallowSilently` (`:294-302`) cover `OpenLogFile`; it stays.

UI/infrastructure: `AutoScaleMode.Font` (`Form1.Designer.cs:249-254`), no `SetHighDpiMode` in `Program.cs:51-52` (system-aware DPI from the app manifest/defaults). No WebView2 or other UI packages; the csproj has no package references.

## Design

### Shared wiring (Form1)

- Two new modeless windows, each at most one instance: `HelpForm` and `LogViewerForm`. `Form1` keeps a field per window. "Show" either activates the open one (restoring it if minimized) or creates a new one.
- They are **not owned** by `Form1` (`Show()` without owner, `ShowInTaskbar = true`, `StartPosition = CenterScreen`). This lets them open from the tray while the main window is hidden (Launch To Tray) and keeps them up when the main window is minimized to the tray. `Form1.FormClosed` closes and disposes both. In launch mode (`IsLaunchMode`) they are not opened (the app exits right after launching).
- F1 in `Form1` opens Help: override `ProcessCmdKey` for `Keys.F1` (works whatever control has focus, no `KeyPreview` change). F1 inside `HelpForm` does nothing extra. F5 refreshes in `LogViewerForm`, Esc closes either window.
- Buttons: `btnHelp` opens `HelpForm`. `btnViewLogs` opens `LogViewerForm` instead of the external editor. The external editor stays available as a button in the viewer ("Open in editor"), which calls the existing `LoggingService.OpenLogFile`.
- Tray menu (recommended, Q-H4): "View Logs" (in-app viewer, replaces "Open Logs"), "Help", separator, "Exit" (Exit last, which is the Windows convention; today Exit is first).

### Part A: Help window

Approaches:

| Option | Verdict |
|---|---|
| **A1. Form with section `ListBox` on the left, read-only `RichTextBox` on the right (`SplitContainer`), optional search box over the list** | **Recommended.** No dependency, scales to any number of sections, keyboard friendly (Up/Down in the list), DPI-safe with `AutoScaleMode.Font`, easy to test (content is data). |
| A2. `TabControl`, one tab per section | Rejected: 8-9 tabs overflow into scroll arrows at the default width, no search, tab headers read poorly with screen readers when crowded. |
| A3. WebView2 + HTML/Markdown | Rejected: new NuGet dependency plus the Evergreen runtime, a browser engine as attack surface, and script/link handling to lock down. Overkill for static text. |
| A4. `.chm` via `Help.ShowHelp` / hh.exe | Rejected: needs HTML Help Workshop to build, CHM is legacy, blocked by Mark-of-the-Web when copied, and the documenter can't edit it as text. |
| A5. Keep `MessageBox`, just add headings | Rejected: it is exactly what the user asked to replace; no scrolling or navigation for long text. |

`TreeView` instead of `ListBox` is only worth it with nested topics; the content is flat, so `ListBox`.

**Content storage (Q-H1).** Recommended: an embedded resource `StartupController/Help/Help.md` (UTF-8 with BOM, `<EmbeddedResource Include="Help\Help.md" LogicalName="StartupController.Help.md" />`) with a deliberately tiny format:
- `## Title` starts a section; everything until the next `## ` is its body.
- Blank line = paragraph break. Lines starting with `- ` render as bullets (`• `). `**...**` and other Markdown are **not** interpreted (shown literally), so the documenter must not use them; a test enforces it.
- Anything before the first `## ` (e.g. a comment for maintainers) is ignored. Lines starting with `<!--` are skipped.

Why not C# constants: the documenter edits plain text without `\u` escapes and string concatenation, diffs are readable, and README "Usage" can be copied over. The encoding risk that led to `\u` escapes is handled by reading the resource explicitly as UTF-8 and by a test that asserts the arrow and dash characters are present after loading (the same check as today). Alternative recorded: a `static IReadOnlyList<HelpSection>` in `HelpContent.cs` (closest to today, no parser) if the user prefers code (Q-H1).

New pure class `HelpContent` (`StartupController/HelpContent.cs`, no WinForms):
- `record HelpSection(string Title, string Body)`.
- `static IReadOnlyList<HelpSection> Parse(string markdown)`: the rules above; trims trailing blank lines; throws nothing (empty input gives an empty list; text before the first heading is ignored).
- `static IReadOnlyList<HelpSection> LoadEmbedded()`: reads the resource with `Assembly.GetManifestResourceStream` + `StreamReader(UTF8)`. Missing resource: returns one section "Help" with "Help content is missing from this build." and logs a warning (never throws).
- `static HelpSection About(string version, string logFolder)`: built in code, appended last, so version and log path are always correct (version from `AssemblyInformationalVersionAttribute`, today 1.0.29).
- `static IEnumerable<int> Search(IReadOnlyList<HelpSection> sections, string query)`: indices of sections whose title or body contains the query (ordinal ignore case). Empty query = all.

`HelpForm` (`StartupController/HelpForm.cs`, code-built layout, no designer file needed; a designer file is fine if the developer prefers):
- `SplitContainer` (left ~200 px at 96 DPI, min 150): top a search `TextBox` (placeholder "Search help"), below it the section `ListBox`. Right: `RichTextBox` with `ReadOnly = true`, `DetectUrls = false`, `BorderStyle = None`, `BackColor = SystemColors.Window`, word wrap on. The body is set with `Text`/`SelectionFont` only (title bold, body regular); `.Rtf` is never assigned.
- Selecting a section shows it; first section selected on open. Search filters the list; the shown section stays if it still matches.
- Size 760x520 at 96 DPI, `MinimumSize` 520x360, `AutoScaleMode.Font` with the same `AutoScaleDimensions` as `Form1` so it scales like the main window. Font: `SystemFonts.MessageBoxFont` for body.
- Buttons: "Close". Esc closes. Logs "Help shown" once per open (keeps today's log line).

Sections (content from PRD and current behavior, nothing new invented; developer drafts them from today's `HelpText` + README "Usage"/"Uninstalling", documenter polishes):
1. **Getting started**: what startup programs are (Run key entries Windows starts at login), what StartupController adds (one place, in your order), the main window at a glance.
2. **Which programs are listed**: entries in your HKCU Run key that are disabled in Windows, plus those taken over; own entry never listed; HKLM and the Startup folder are not shown. (Fixes today's "every program in your Run key".)
3. **Enabled and Disabled**: Enable/Disable/double-click; Disabled = starts nowhere; "Changed - re-enable to launch" (must contain `Form1.StatusText` for Changed verbatim).
4. **Order and how programs are launched**: arrows `↑ ↓ ⇈ ⇊`, Save Order, Autosave on change; Windows has no order, so the app launches enabled programs itself at login in list order; once per logon session (D8) and the "not launched automatically" balloon; no UAC at logon, Launch button elevates; Run command format and "Executable not found".
5. **Taking over Windows startup programs**: the gate, Task Manager shows them Disabled, first login after takeover still by Windows, the stranded-takeover warning balloon.
6. **Uninstalling**: the return-to-Windows prompt, `RETURNTOWINDOWS`, only your profile, recovery via Task Manager.
7. **Settings and the tray**: the four checkboxes; Launch To Tray, minimize to tray, tray menu items, notifications and Silence Notifications.
8. **Logs and troubleshooting**: View Logs, what is logged (and that arguments are never logged), common problems (program didn't start: check the log for FAILURE lines, quote the path; programs started twice/not at all: check Task Manager and the own entry).
9. **About** (generated): version, log folder path, "per-user, no administrator rights needed".

`Form1.HelpText` is removed; tests move to `HelpContent`.

### Part B: Log viewer

New pure classes (`StartupController/LogViewer/` or flat files, no WinForms):

`LogFileReader`:
- `static LogReadResult ReadTail(string path, long maxBytes)`: if the file doesn't exist returns `Exists = false`. Otherwise opens `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 KiB, FileOptions.SequentialScan)`, reads the last `maxBytes` (seek to `Length - maxBytes` when larger), closes the stream before returning (no handle is held between refreshes, so writing and rotation are never blocked). When it started mid-file it drops everything up to and including the first `\n` (removes a partial line and any split UTF-8 sequence) and sets `Truncated = true`. Decodes with `new UTF8Encoding(false, throwOnInvalidBytes: false)`. Returns `Lines`, `Truncated`, `FileLength`, `LastWriteTimeUtc`. `IOException`/`UnauthorizedAccessException` are returned as `Error` (message), never thrown.
- `MaxViewBytes = 4 MiB` (well above the 1 MiB rotation size and its "doubled" backoff, small enough for the UI).

`LogLine`:
- `static LogLine Parse(string raw)`: split on `\t`; if there are at least 3 parts and part 0 parses with `DateTime.TryParseExact("yyyy-MM-dd HH:mm:ss.fff", InvariantCulture)`, fills `Timestamp`, `Level`, `Category`, `Message` = remaining fields joined with `"  |  "`. Otherwise a raw line: `Level = ""`, `Message = raw` (older formats, hand-edited files). Every displayed field is passed through `LogEscape.Escape` again, so a tampered or pre-Phase-4 log can't show control or bidi characters. `Escape` doesn't touch backslashes, so already-escaped text is unchanged. Text is never unescaped. `Raw` keeps the original line for copying.

`LogFilter`:
- `static IReadOnlyList<LogLine> Apply(IReadOnlyList<LogLine> lines, LogLevelFilter levels, string search, bool currentSessionOnly)`: levels as a flags enum (Info, Warn, Error, Launch, Session, Other), search ordinal ignore case over `Raw`, current session = from the last `SESSION` line on. Pure, so fully unit-testable.

`LogViewerForm` (`StartupController/LogViewerForm.cs`), constructed with `(IProcessStarter starter, string logFilePath)`; Form1 passes `_starter` and `LoggingService.LogFilePath`:
- Top bar: file `ComboBox` ("Current log", "Previous log (.1)", "Older log (.2)", from `LoggingService.RotatedPath` and `KeptLogFiles`; only existing files enabled), level `CheckBox`es or a drop-down with checks (All / Errors and warnings / Launches), search `TextBox` (filters after a 300 ms pause), "Current session only" checkbox, Refresh button (F5).
- Body: `ListView` in `VirtualMode`, `View = Details`, `FullRowSelect`, columns Time / Level / Category / Message (Message fills the rest). Newest at the bottom; after a refresh it scrolls to the end unless the user scrolled up. Row colour by level (ERROR red text, WARN dark orange, LAUNCH FAILURE red) using `SystemColors`-friendly choices that work in high contrast (high contrast: no custom colours).
- Status bar: "N of M lines", "Showing the last 4 MB" when truncated, file path, last write time.
- Buttons: "Copy" (selected rows, `Raw` lines joined with `\r\n`; Ctrl+C does the same; Ctrl+A selects all), "Copy all shown", "Open in editor" (existing `LoggingService.OpenLogFile(starter, ...)`, current log only), "Open log folder", "Close".
- Missing file: the list is empty and an overlay/label says "No log has been written yet." The viewer never creates the file or the folder.
- Read errors: shown in the status bar ("Could not read the log: <message>"), logged once per error kind, never a crash.
- Reading off the UI thread: `RefreshAsync` runs `ReadTail` + `Parse` in `Task.Run`, then applies the result on the UI thread if the form isn't disposed. A refresh counter drops stale results when refreshes overlap. Filtering runs on the UI thread (in-memory, at most ~40k lines).
- Auto-refresh (Q-H2, recommended: an "Auto-refresh" checkbox, off by default): a `System.Windows.Forms.Timer` every 2 s compares `FileLength`/`LastWriteTimeUtc` (cheap `FileInfo`) and refreshes only on change. `FileSystemWatcher` rejected: it fires on every log line (including the viewer's own), can overflow its buffer, and needs handling for rotation renames; polling is simpler and enough.
- Logging by the viewer: one INFO line "Log viewer opened" per open. Refresh and auto-refresh write nothing (otherwise auto-refresh would see its own write and loop).
- "Open log folder": `starter.Start(new ProcessStartInfo(folder) { UseShellExecute = true })` where `folder = Path.GetDirectoryName(LoggingService.LogFilePath)`, only after `Directory.Exists(folder)`. No `explorer.exe /select,` argument string (rejected: needs the full explorer path and argument quoting for paths with spaces, for little benefit). The path never comes from log content. Failure: warning dialog with the path, like `OpenLogs` did (superseded, implemented: `OpenLogs` no longer exists). Factor this into `LoggingService.OpenLogFolder(IProcessStarter, out string? error)` next to `OpenLogFile` so it is testable with `FakeProcessStarter`.

Rejected alternative for the body: one read-only multi-line `TextBox` with the raw text. Simpler, but no columns, no per-level colour and filtering means rebuilding a multi-MB string. Kept as fallback if the virtual `ListView` turns out awkward (Q-H5).

## Registry impact

None. Neither window reads or writes the registry. No HKCU or HKLM keys, no `StartupApproved`, no `Software\StartupController` values, no new settings. No admin rights. Nothing to migrate.

(If Q-H3 "remember window size/position" is answered yes, that would add HKCU `Software\StartupController` values via `IUserSettings`; not planned.)

## Steps

Part A first (smaller), then Part B. One commit per part (user workflow).

A1. Add `StartupController/Help/Help.md` with sections 1-8 (content as above, accurate to PRD and README). Add the `EmbeddedResource` item with `LogicalName` to `StartupController.csproj`.
A2. Add `StartupController/HelpContent.cs`: `HelpSection`, `Parse`, `LoadEmbedded`, `About`, `Search`.
A3. Add `StartupController/HelpForm.cs`: layout, selection, search, Esc. Constructor takes `IReadOnlyList<HelpSection>` (so it doesn't load resources itself).
A4. `Form1.cs`: remove `HelpText`; `ShowHelp` opens/activates the single `HelpForm` (sections = `LoadEmbedded()` + `About(...)`); override `ProcessCmdKey` for F1; close the child windows in `FormClosed`. Update the comment that pointed at README "Usage" (now Help.md and README are kept in line).
A5. Tests: replace `Form1.HelpText` usages in `Phase4Tests.cs:524-532` and `Phase4FixTests.cs:250-259` with the loaded content (same assertions), add the new Help tests below.

B1. Add `LogFileReader`, `LogLine`, `LogFilter` (+ `LogLevelFilter` enum, `LogReadResult`).
B2. `LoggingService.cs`: add `internal static string LogDirectory` (under `_lock`, like `LogFilePath`) and `OpenLogFolder(IProcessStarter, out string? error)`. Do not change `AppendLine`, rotation or `OpenLogFile` (source-scan tests pin them).
B3. Add `LogViewerForm.cs` (layout, async refresh, filters, copy, open folder/editor, optional auto-refresh timer disposed with the form).
B4. `Form1.cs`: `btnViewLogs` and the tray item open/activate the single `LogViewerForm`; keep `OpenLogs` only as the viewer's "Open in editor" path (or move it into the viewer). (Implemented: `OpenLogs` is gone, the viewer calls `LoggingService.OpenLogFile`.) Tray menu order per Q-H4. Tooltips: btnViewLogs "Show the log of what was launched and any errors"; btnHelp "How StartupController works (F1)".
B5. Tests for the pure classes and `OpenLogFolder` (below).
B6. `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`.

## Test plan

All tests use temp directories and `FakeProcessStarter`; never the real log folder, real registry or real processes (SandboxGuardTests stays green). Use `StringComparison.Ordinal` in string asserts (`Phase4FixTests:280` enforces it).

HelpContent (unit):
- Parse: two sections; text before the first `##` ignored; `<!--` lines skipped; `- ` bullets become `• `; `###` is body text, not a section; trailing blank lines trimmed; empty input gives empty list; Windows `\r\n` and `\n` give the same result; a heading with no body gives an empty body.
- LoadEmbedded: resource present; at least 8 sections; titles unique; arrows `↑ ↓ ⇈ ⇊` and en dash present (encoding guard, replaces `HelpText_HasTheExactArrowAndDashCharacters`); contains `Form1.StatusText(changed)` verbatim, "Task Manager", "Enabled means", "Save Order", "Autosave on change", "Executable not found", "in quotes", "View Logs", "Launch To Tray", "own entry is never listed" (replaces `HelpText_CoversTheMainTopics`).
- Content guard: no `**`, backticks, `[text](url)`, `http://` or `https://` in Help.md (nothing that would look like a link or unrendered Markdown); does not contain "Every program in your Run key".
- About: contains the version passed in and the log folder.
- Search: case-insensitive title and body match, empty query returns all, no match returns none.

LogFileReader (unit, temp files):
- Missing file: `Exists = false`, no exception, file and folder not created.
- Small file: all lines, `Truncated = false`.
- File larger than `maxBytes`: only the tail; first partial line dropped; `Truncated = true`; a multi-byte UTF-8 character (e.g. `æ`, an emoji) split at the seek point doesn't produce garbage in the first returned line.
- Invalid UTF-8 bytes in the file: replaced, no exception.
- Concurrent writer: while a `FileStream` is open with `FileAccess.Write, FileShare.Read` (what `AppendAllText` uses), `ReadTail` still succeeds. And the reverse: `LoggingService` (initialized to a temp dir) can append and rotate (`RotateIfNeeded`) right after/while a `ReadTail` runs; after `ReadTail` returns no handle is held (`File.Move` of the file succeeds).
- Locked exclusively by another stream (`FileShare.None`): returns `Error`, no throw.
- Empty file, file without trailing newline, file of only `\r\n`.

LogLine (unit):
- Normal INFO line; LAUNCH line with 4 fields joined; SESSION header; line with fewer than 3 tabs or bad timestamp gives a raw line; line containing a literal bidi override U+202E or a control char (simulated tampered file) is displayed escaped as `‮`; already-escaped `\t` text stays `\t` (no double escaping); `Raw` is unchanged for copy.

LogFilter (unit):
- Each level flag; Other catches raw lines; search ignores case and matches inside fields; "current session only" with zero, one and several SESSION lines; combined filters.

LoggingService.OpenLogFolder (unit, `FakeProcessStarter`):
- Starts exactly the log directory with `UseShellExecute = true` and no arguments; missing directory returns false, error set, nothing started, and does not create the folder; starter throws: false + logged.

Source guards (cheap, in the style of the existing ones):
- `LogViewerForm.cs` and `HelpForm.cs` never assign `.Rtf` and set `DetectUrls = false` on any `RichTextBox`; neither contains `Process.Start`.
- `Form1.cs` no longer contains `MessageBox.Show(this, HelpText`.

Manual checks (UI, developer/tester on a dev machine; no real startup changes needed):
- M-H1 Help: button and F1 open it; second press activates the same window; list navigation by keyboard; search; Esc closes; resize; 100%, 150% and 200% scaling (move between monitors) without clipped text; high contrast theme readable.
- M-H2 Help opens from the tray while the main window is hidden (Launch To Tray), if Q-H4 adds it.
- M-L1 Viewer: shows the current session; new lines appear after Refresh/F5 and with auto-refresh; level filters, search, current session only.
- M-L2 Copy selected/all into Notepad gives the raw tab-separated lines.
- M-L3 Open log folder opens Explorer at `%LOCALAPPDATA%\StartupController\logs`; Open in editor still works.
- M-L4 Delete the logs folder while the viewer is open: Refresh shows "No log has been written yet" (then the app recreates it on its next line).
- M-L5 With the viewer open and auto-refresh on, force rotation (temporary test build or a copied 1 MiB file in a scratch log dir via `LoggingService.Initialize` in a debug harness, not the real log): rotation succeeds and the viewer follows the new current file.
- M-L6 Closing the main window (Exit) closes both child windows; Windows logoff with them open doesn't block.

## Docs to update (documenter)

- `README.md` and `StartupController/README.MD` (kept identical): "Help" bullet (window with sections, F1); "View Logs" bullet (in-app viewer, filters, Open log folder, Open in editor); tray menu items.
- `StartupController/Help/Help.md`: final wording pass; it is now the in-app help source. Add a maintainer comment at the top: plain text, `## ` headings, `- ` bullets, no other Markdown, keep in line with README "Usage".
- `CHANGELOG.md`: "Help window with sections and search (F1)", "In-app log viewer", fix of the "Which programs are listed" help text.
- `StartupController/PRD.MD` §6: mention the help window with sections; Non-Functional Requirements: logs viewable in the app. (Additive, no contradiction.)
- `.github` issue template `bug_report.md`: point to View Logs > Copy for attaching log lines, if it asks for logs.

## Security notes (for security-analyser)

- Log content is attacker-influenced (Run value names and paths end up in it). The viewer shows it as plain text in a `ListView`; no RTF, no URL detection, no hyperlinks, no "open path" action on a line. Fields are re-escaped on display to neutralise control/bidi characters from tampered or old logs.
- Process launches added: only "Open log folder" (directory from `LocalApplicationData`, checked with `Directory.Exists`, shell-executed through `IProcessStarter`, no arguments). "Open in editor" is the existing `OpenLogFile`. Both use paths from `LoggingService`, never from log content or the UI.
- Read size is capped (4 MiB) to avoid memory exhaustion from a huge or planted file; reads use `FileShare.ReadWrite | FileShare.Delete` and close immediately, so the viewer can't block logging or rotation.
- Help content is a static embedded resource; no external files, no links.
- No registry access, no elevation.

## Risks / open questions

Risks:
- Encoding of Help.md: an editor saving it as ANSI would break the arrows. Mitigated by UTF-8 BOM, explicit UTF-8 read and the arrow test. An `.editorconfig` `charset = utf-8-bom` entry for `*.md` under `Help/` would help (developer's call).
- Unowned child windows: if `FormClosed` cleanup is missed they could keep the message loop busy; covered by M-L6. A form shown with no owner while `Form1` is hidden must not steal startup visibility logic (`SetVisibleCore` only governs `Form1`).
- `ListView` virtual mode with 40k rows and filtering is fine in practice, but owner colours in high contrast need care.
- Removing `Form1.HelpText` breaks two existing tests until A5 is done; do A4 and A5 in the same commit.

Open questions for the user:
- **Q-H1 Help content format:** DECIDED: embedded `Help.md`.
- **Q-H2 Log viewer auto-refresh:** DECIDED: "Auto-refresh" checkbox, off by default; Refresh button and F5 always available.
- **Q-H3 Remember window size/position:** DECIDED (default): no; no new HKCU settings.
- **Q-H4 Tray menu:** DECIDED: "View Logs" (in-app viewer), "Help", separator, "Exit".
- **Q-H5 Viewer layout:** DECIDED: virtual `ListView` with columns and level colours.
- **Q-H6 Keep "Open in editor":** DECIDED (default): keep it in the viewer.
