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

1. Download the latest release from the [Releases](#) page.
2. Run the installer and follow the on-screen instructions.
3. (Optional) The application can be set to run at Windows startup via the settings.

## Usage

- **Enable/Disable:** Select a program and click "Enable" or "Disable".
- **What "Enabled" means:** Enabled means StartupController launches the program, in your chosen order, when it runs at login. Disabled means the program is not launched by StartupController. The app changes only its own launch list, never Windows' startup settings.
- **Entries not listed:** programs that Windows already starts (for example enabled in Task Manager's Startup tab) are not listed, so they are never started twice. To manage one here, disable it in Task Manager first. The app's own entry is never listed.
- **"Changed – re-enable to launch":** shown when the command of an enabled program changed (for example after an update or reinstall with different data). It is not launched until you enable it again and save. After the next save it shows as Disabled.
- **Launch:** Select a program and click "Launch" to run it immediately.
- **Reorder:** Use "Move Up" and "Move Down" to change the order, then "Save Order".
- **Settings:** Use the checkboxes at the bottom to control notifications, startup behavior, and tray launch.
- **Help:** Click "Help" for usage instructions.

## Building

This project targets **.NET 8** and uses **C# 12.0**.  
To build from source:

1. Open the solution in Visual Studio 2022 or later.
2. Restore NuGet packages if prompted.
3. Build and run the solution.

## Icon Attribution

App icon made by Rocket icons created by Freepik - Flaticon [www.flaticon.com](https://www.flaticon.com/free-icons/rocket).  
**Please see Flaticon for full license and attribution requirements.**

## License

This project is licensed under the GNU GPLv3 License. See [LICENSE](LICENSE) for details.

