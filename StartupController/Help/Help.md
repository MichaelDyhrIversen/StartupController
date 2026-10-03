<!-- In-app help source, shown by the Help window (embedded resource, parsed by HelpContent). -->
<!-- Format: "## " starts a section, "- " starts a bullet, a blank line starts a paragraph. No other Markdown: -->
<!-- no bold, no backticks, no links. Save as UTF-8 with BOM. Keep in line with the Usage section of README.md. -->

## Getting started
Startup programs are the programs Windows starts when you log in. Each one is an entry in the Run key of your user profile in the registry.

Windows starts these programs in no particular order. StartupController gives you one place to decide which of them start and in what order. At login it starts the enabled programs itself, one after the other, in the order of the list.

The main window at a glance:
- The list shows each program's name, status, command and description.
- Enable, Disable and Launch act on the selected program.
- The arrow buttons change the order. Save Order keeps it.
- View Logs shows what was launched and any errors. Help (or F1) opens this window.
- The checkboxes at the bottom are the settings.

## Which programs are listed
The list shows the programs in the Run key of your user profile that are disabled in Windows, plus the ones StartupController has taken over (see "Taking over Windows startup programs"). Taken-over programs are disabled in Windows as part of the takeover, so StartupController starts them instead.

StartupController's own entry is never listed or changed. Values that are not text commands are skipped.

Programs that start for all users (the machine-wide Run key) and shortcuts in the Startup folder are not shown and are never changed.

## Enabled and Disabled
Enabled means StartupController launches the program when you log in, in the order of the list (with "Launch Enabled Programs On System Startup" checked).

Disabled means the program doesn't start at all: Windows no longer starts it and StartupController doesn't either.

Use Enable or Disable, or double-click a row to switch it. Click Save Order to keep the change, or check "Autosave on change".

"Changed – re-enable to launch"
The program's command changed since you enabled it (for example after an update or a reinstall). Nothing launches it until you enable it again and save. After the next save it shows as Disabled.

## Order and how programs are launched
↑ and ↓ move the selected program one step, ⇈ and ⇊ move it to the top or bottom. Click Save Order to keep the order and the Enabled settings. With "Autosave on change" every change is saved right away.

Windows has no startup order, so StartupController launches the enabled programs itself at login, in the order of the list. This happens only while "Launch Enabled Programs On System Startup" is checked.

- The automatic launch runs at most once per Windows logon session. Signing out and in, or restarting, allows a new run.
- If Windows can't confirm the logon session, nothing is launched and a balloon says that startup programs were not launched automatically. Open StartupController and use Launch.
- At login StartupController can't show a UAC prompt, so programs that need administrator rights are not started then and a line is logged. The Launch button still asks for permission and starts them.
- Launch starts the selected program now.

Run command format
StartupController starts each program from its Run command. Put the full path in quotes, for example "C:\Program Files\App\app.exe" --minimized. Relative paths are not started.

"Executable not found" means the command could not be resolved to an existing file, often because an unquoted path with spaces or arguments was used. Quote the path in the Run entry and try again.

## Taking over Windows startup programs
Each time StartupController loads its list (when you open it, and at login), it takes over the programs in your Run key that Windows would start itself. It disables each one in Windows, lists it as Enabled and adds it at the end of the list. A program the list already knows keeps its position. There is no prompt.

- When it happens: only while "Launch Enabled Programs On System Startup" is checked and StartupController's own startup entry is valid and enabled in Windows. Otherwise nothing is taken over, because nothing would start the programs at login.
- Task Manager: Startup apps shows taken-over programs as Disabled. That is expected: StartupController starts them now. If you enable one in Task Manager, StartupController takes it over again the next time it loads.
- First login: the first login after a takeover is still started by Windows. From the next login StartupController starts the program, in your order, so nothing starts twice.
- Warning balloon: if taken-over programs would start nowhere (StartupController's own startup entry is missing or disabled), a warning balloon tells you when you open the app, even with notifications silenced. Enable StartupController again in Task Manager > Startup apps, or enable those programs there.

## Uninstalling
Uninstalling StartupController asks whether to give all taken-over programs back to Windows, so Windows starts them again. Yes returns them. No leaves them disabled. If nobody answers within 120 seconds, or the question can't be shown, they are returned.

- Yes returns every taken-over program that is still in the Run key, also those you disabled in StartupController or that show as Changed. Programs you had disabled in Task Manager before StartupController took over are never touched.
- A silent uninstall shows no question and returns the programs. The installer property RETURNTOWINDOWS=0 leaves them disabled, RETURNTOWINDOWS=1 returns them without a question.
- Only the user who runs the uninstall is handled. Other Windows users keep their programs disabled and can enable them in Task Manager > Startup apps.
- Recovery: if a program is still disabled afterwards, open Task Manager > Startup apps, select it and click Enable.
- Upgrading to a newer version does not ask and does not return anything.

## Settings and the tray
The checkboxes at the bottom of the main window:
- Silence Notifications: no balloon notifications. Warnings about taken-over programs that would start nowhere are still shown.
- Launch Enabled Programs On System Startup: at login, launch the enabled programs in the order of the list. Taking over programs also depends on it.
- Launch To Tray: start hidden in the notification area (tray), and hide to the tray when the window is minimized.
- Autosave on change: save every change right away instead of with Save Order.

Double-click the tray icon to show the window. Right-click it for View Logs, Help and Exit.

## Logs and troubleshooting
View Logs opens the log window. It shows what was launched, settings changes and any errors, newest at the bottom. Errors and failed launches are red, warnings orange. Choose the current log or an older one, pick Show: All levels, Errors and warnings, or Launches, search, or show only the current session. Press F5 or click Refresh to read new lines, or check Auto-refresh (off by default). Copy copies the selected lines, Copy all shown copies everything listed. Open in editor opens the current log in your text editor, Open log folder opens the folder.

The log window shows at most the last 50,000 lines and the last 4 MB of a file, and the status bar says when a limit applied. It only reads the log and never changes it.

Command-line arguments of programs are never written to the log, because they can contain passwords or tokens.

Common problems:
- A program didn't start: look for LAUNCH lines with FAILURE in the log. "Executable not found" usually means the path in the Run entry needs quotes.
- A program started twice or not at all: check Task Manager > Startup apps. A taken-over program should show as Disabled there, and StartupController's own entry as Enabled.
- Nothing was launched at login: check that "Launch Enabled Programs On System Startup" is checked and that the program is Enabled in the list.
