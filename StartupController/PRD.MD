# Product Requirements Document (PRD)

## Product Name
StartupController

## Purpose
StartupController is a Windows application that allows users to view, manage, and launch startup programs. It takes over the per-user startup programs that Windows would start itself, so that it is the single place that decides which of them run at login and in what order. The application provides an intuitive interface for enabling/disabling startup entries, launching them individually, and sorting their startup order.

## Target Platform
- Windows Desktop (.NET 10), minimum Windows 10 version 1607

## Features

### 1. List Startup Programs
- Display the startup programs in the current user's Run key that are disabled in Windows, and the ones the application takes over (see §3), which are disabled in Windows as part of the takeover. The application's own entry is never listed.
- Show relevant details: program name, file path, status (enabled/disabled), and description if available.

### 2. Launch Programs Individually
- Allow users to launch any disabled startup program directly from the interface.

### 3. Enable/Disable Startup Programs
- Provide controls to enable or disable each program in the application's own launch list.
- The application launches, in the saved order, the startup entries that are disabled in Windows (StartupApproved). Windows does not order startup programs, so Windows must not start them itself.
- Silent takeover (new, user decision 2026-10-02, reverses the earlier "never changes Windows' startup configuration" rule): each time the application loads the startup list (normal start and `--launch`), it silently takes over every HKCU Run entry that Windows would start. It disables the entry in Windows (`StartupApproved\Run`, the value Task Manager writes), lists it as Enabled and appends it to the end of the saved order. There is no prompt.
  - Gate: it takes over only when "Launch programs on startup" is on and the application's own Run entry is valid (a string value, this exe, with `--launch`, enabled in Windows). Otherwise nothing is taken over.
  - A name the application already stores keeps its position and becomes Enabled. A stored Enabled entry whose Run command differs from the stored fingerprint stays Changed. Entries already disabled in Windows are never recorded or touched by the takeover.
  - Disabling a program in the application means it does not start at all (Windows no longer starts it either).
  - The first logon after a takeover is started by Windows; later logons are started by the application, in order.
  - At logon there is no UAC prompt: programs that require elevation (`requireAdministrator` exes, "Run as administrator" shortcuts) are not started at logon. Manual Launch still elevates.
  - If taken-over programs would start nowhere (the application's own Run entry is missing or disabled), a warning balloon is shown after a normal start, even when notifications are silenced.
- Return to Windows (new, user decisions D-T3/D-T4): there is no in-app action. The uninstaller asks whether to enable all taken-over programs again so that Windows starts them. Yes enables every taken-over entry that still exists in Run, including ones that are Disabled or Changed in the application. Silent uninstall also returns them (override with the installer property `RETURNTOWINDOWS`). Only the uninstalling user's profile is handled.

### 4. Sort Startup Order
- Allow users to reorder the list of startup programs.
- The order should determine the sequence in which programs are started if launched via the application.

### 5. User Interface
- Easy-to-use, modern Windows Forms interface.
- List view with sortable columns (name, status, etc.).
- Buttons or context menu for actions: Enable, Disable, Launch, Move Up/Down.
- Clear indication of each program’s current status.
- Option to save the preferred startup order.

### 6. Explanation/Help
- Provide a help section or tooltip explaining:
  - What startup programs are.
  - How enabling/disabling works.
  - How to use the application features.

## Non-Functional Requirements
- Responsive and intuitive UI.
- Changes should not require a system restart.
- All actions should be logged for troubleshooting.
- Application should not require administrative privileges unless necessary.

## Out of Scope
- Managing startup programs for other users or remote machines.
- Editing program binaries or advanced startup parameters.

## Success Criteria
- Users can see all startup programs that StartupController manages (the disabled ones and the ones it took over).
- Users can enable/disable and launch programs from the UI.
- Users can reorder programs and save their preferences.
- Users understand how to use the application via built-in help.

## Notification Popup and Tray Functionality

- The application must provide a notification popup each time it starts a program.
- The popup should display:
  - The name of the software being started.
  - The current count and the total count of programs being started (e.g., "Starting App1 (1 of 5)").
- There must be a setting to silence (disable) these popups.
- The notification should use the standard Windows notification system (system tray/toast notification).
- The application should be able to launch directly to the notification area (system tray) without displaying any windows, allowing it to run in the background.

## Registry Usage

- The application must use the Windows Registry under the current user (HKCU) to store and retrieve information.
- The order of the startup programs and the names of the programs must be persisted in the registry.
- The application must fetch all relevant startup program information from the registry, including their enabled/disabled status and configuration.
- Order and enabled state are stored under `HKCU\Software\StartupController` as REG_MULTI_SZ values (new, reflects the implemented design):
  - `ProgramOrder`: the program names in the chosen order, including saved names whose Run entry is currently missing (kept hidden).
  - `EnabledPrograms`: the names StartupController launches.
  - `EnabledFingerprints`: `name|sha256hex` for each enabled name. Only a hash of the Run value (data and value type) is stored, never the command.
  - Legacy `StartupOrder` (REG_SZ, `;`-joined): read once to migrate when `ProgramOrder` is absent. It is never written or deleted afterwards.
- `LaunchSession.{session id}` (REG_SZ, `v1:{id}:{logonTime}`): records that `--launch` already ran in that Windows logon session, so it runs at most once per session. An older single `LaunchSession` value may exist on development machines and is ignored.
- `TakenOverPrograms` (REG_MULTI_SZ, new): the names the application has taken over, written before `StartupApproved`. Names only, no commands. The uninstaller reads it to know which entries to return and removes the names it returned. The application reads it only to extend it and to detect stranded entries (the warning balloon); a UI save does not change it.
- Reads: HKCU `Software\Microsoft\Windows\CurrentVersion\Run` and `...\Explorer\StartupApproved\Run`.
- Writes to `StartupApproved\Run\<name>` (new): REG_BINARY, 12 bytes (`03 00 00 00` plus the current FILETIME) for each taken-over entry, exactly the format Task Manager writes for Disabled. Only the takeover writes it, never for the application's own entry, and it never deletes values. The uninstall helper writes `02` plus 11 zero bytes (enabled) for each recorded name still in Run as a string.
- HKCU only: HKLM `Run`, `WOW6432Node`, `Run32` and the Startup folder are not read or written. No admin rights are needed by the application (the uninstaller runs elevated but only changes the uninstalling user's HKCU).
- Own entry: the application's own Run value is used by "Launch programs on startup" and is never taken over.

## Singleton Application Instance

- The application must enforce a singleton pattern, ensuring that only one instance of the program can run at any given time.
- If a user attempts to start a second instance, the existing instance should be brought to the foreground or activated, and the new instance should exit immediately.
