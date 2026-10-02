# StartupController

**StartupController** is a Windows application that allows users to manage, enable, disable, reorder, and launch startup programs easily. It provides a user-friendly interface to control which programs run when your system starts, and offers additional features such as notification silencing and launching programs to the system tray.

## Features

- View all startup programs in a sortable list
- Enable or disable startup programs
- Launch selected programs manually
- Reorder the startup sequence and save your preferred order
- Option to silence notifications
- Option to launch enabled programs automatically on system startup
- Option to start the application minimized to the system tray

## Installation

Requirements: Windows 10 version 1607 or later (or Windows 11) and the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0). The installer checks for the runtime and links to the download if it is missing. Keep the runtime patched: turn on "Receive updates for other Microsoft products" in Windows Update settings so Microsoft Update delivers .NET security fixes, or install them manually.

1. Download the latest release from the [Releases](#) page.
2. Run the installer and follow the on-screen instructions. It installs for all users. If an older version was installed "Just me" (for this user only), setup asks you to uninstall it first in Settings > Apps > Installed apps.
3. (Optional) The application can be set to run at Windows startup via the settings.

## Usage

- **Enable/Disable:** Select a program and click "Enable" or "Disable".
- **What "Enabled" means:** Enabled means StartupController launches the program, in your chosen order, when it runs at login. Disabled means the program does not start at all: Windows no longer starts it and StartupController does not launch it.
- **Silent takeover:** Windows itself can't order startup apps, so StartupController takes over the ones Windows would start. Each time the app loads its list (when you open it, and at login), every program in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that Windows would start is disabled in Windows (`...\Explorer\StartupApproved\Run`, the same value Task Manager writes) and shown here as Enabled. No prompt is shown. Nothing is changed in `HKLM` or in the Startup folder, and no administrator rights are needed.
  - **When it happens (the gate):** only while "Launch Enabled Programs On System Startup" is checked **and** StartupController's own Run entry is valid: a string value that points to this exe, has the `--launch` argument and is enabled in Windows. Otherwise nothing is taken over, because nothing would start the programs at login.
  - **Order:** newly taken-over programs go at the end of the list. A program the list already knows keeps its position. A program shown as Changed stays Changed.
  - **Task Manager:** Startup apps shows taken-over programs as Disabled. That is expected: StartupController starts them now. If you enable one in Task Manager, the app takes it over again the next time it loads.
  - **First login:** the first login after a takeover is still started by Windows. Later logins are started by StartupController, in your order.
  - **No UAC at login:** StartupController does not show UAC prompts at login. Programs whose exe requires administrator rights (a `requireAdministrator` manifest) and shortcuts set to "Run as administrator" are not started at login and a line is logged. The Launch button still asks for permission and starts them.
  - **Warning balloon:** if taken-over programs would start nowhere (StartupController's own Run entry is missing or disabled), a warning balloon appears when you open the app, even with "Silence Notifications" on. Re-enable StartupController in Task Manager > Startup apps, or enable those programs there.
  - The app's own entry is never listed or changed.
- **"Changed – re-enable to launch":** shown when the command of an enabled program changed (for example after an update or reinstall with different data). Neither Windows nor StartupController starts it until you enable it again and save. After the next save it shows as Disabled.
- **Launch:** Select a program and click "Launch" to run it immediately.
- **Run command format:** StartupController starts each program from its Run command. Put the full path in quotes, for example `"C:\Program Files\App\app.exe" --minimized`. Relative paths are not started. "Executable not found" means the command could not be resolved to an existing file, often because an unquoted path with spaces or arguments was used. Quote the path in the Run entry and try again.
- **Reorder:** Use the arrow buttons (↑ and ↓ to move one step, ⇈ and ⇊ to move to the top or bottom) to change the order, then click "Save Order".
- **Settings:** Use the checkboxes at the bottom: "Silence Notifications", "Launch Enabled Programs On System Startup", "Launch To Tray" and "Autosave on change".
- **Once per logon:** the automatic launch at login (`--launch`) runs at most once per Windows logon session. Signing out and in, or rebooting, allows a new run. If Windows can't confirm the session, nothing is launched and a balloon says "Startup programs were not launched automatically. Open StartupController and use Launch." The Launch button always works.
- **View Logs:** Click "View Logs" to see what was launched and any errors.
- **Help:** Click "Help" for usage instructions.

## Uninstalling

StartupController can give the taken-over programs back to Windows when you uninstall it. Without this, they would stay disabled in Windows and start nowhere.

- **The prompt:** when you uninstall with the installer window (Windows Settings > Apps, or Programs and Features), a question asks whether all taken-over programs should be enabled and started by Windows again. Yes returns them. No leaves them disabled. If nobody answers within 120 seconds, or the question can't be shown, they are returned.
- **Yes returns every taken-over program that is still in the Run key**, also those you switched off in StartupController, disabled again in Task Manager after the takeover, or that show as Changed. Programs you had disabled in Task Manager before StartupController took over are never touched.
- **Silent uninstall:** `msiexec /x ... /qn` (and an uninstall run as SYSTEM, in session 0 or without a desktop) shows no question and returns the programs. Set the installer property `RETURNTOWINDOWS=0` to leave them disabled, or `RETURNTOWINDOWS=1` to return them without a question.
- **Only your profile:** only the user who runs the uninstall is handled. Other Windows users who used StartupController keep their programs disabled and can enable them in Task Manager > Startup apps.
- **Log:** the step writes `%LOCALAPPDATA%\StartupController\logs\uninstall.log` with program names and what happened. No log is written when the uninstall runs elevated.
- **Recovery:** if a program is still disabled afterwards, open Task Manager > Startup apps, select it and click Enable.
- Upgrading to a newer version does not ask and does not return anything.

## Building

This project targets **.NET 10** and uses **C# 14**.  
To build from source:

1. Open the folder in VS Code (with the C# Dev Kit extension) or the solution in Visual Studio 2026 or later. The .NET 10 SDK is required.
2. Restore NuGet packages if prompted.
3. Build and run the solution.

From the command line: `dotnet build` and `dotnet test`.

The installer project `SetupStartupController` is a [WiX Toolset](https://wixtoolset.org) 6 SDK project (`SetupStartupController.wixproj`, `Package.wxs`). It needs only the .NET SDK, not Visual Studio: `dotnet build SetupStartupController -c Release` (in VS Code: Terminal > Run Task > "Build installer (MSI)"). The output is `SetupStartupController\bin\Release\StartupController.msi`. The solution builds it only in the **Release** configuration, so `dotnet build` and `dotnet test` (Debug) skip it. It publishes the app framework-dependent, builds the uninstall helper for net462, and fails if the published app has files `Package.wxs` doesn't install. After linking it runs `tools\Patch-UninstallCustomAction.ps1 -Verify`, which checks that the uninstall step runs as the uninstalling user, ignores its exit code and runs before files are removed. If the check fails, the build fails and the MSI is deleted. To re-check a built MSI: `powershell -File tools\Patch-UninstallCustomAction.ps1 -Msi <path> -Verify`. The installer must be built on an x64 machine.

## Icon Attribution

App icon made by Rocket icons created by Freepik - Flaticon [www.flaticon.com](https://www.flaticon.com/free-icons/rocket).  
**Please see Flaticon for full license and attribution requirements.**

## License

This project is licensed under the GNU GPLv3 License. See [LICENSE](LICENSE) for details.

