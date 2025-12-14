# Product Requirements Document (PRD)

## Product Name
StartupController

## Purpose
StartupController is a Windows application that allows users to view, manage, and launch disabled startup programs. The application provides an intuitive interface for enabling/disabling startup entries, launching them individually, and sorting their startup order.

## Target Platform
- Windows Desktop (.NET 8)

## Features

### 1. List Disabled Startup Programs
- Display all currently disabled startup programs.
- Show relevant details: program name, file path, status (enabled/disabled), and description if available.

### 2. Launch Programs Individually
- Allow users to launch any disabled startup program directly from the interface.

### 3. Enable/Disable Startup Programs
- Provide controls to enable or disable each program as a startup entry.
- Changes should be applied locally and reflected in the system's startup configuration.

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
- Users can see all disabled startup programs.
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

## Singleton Application Instance

- The application must enforce a singleton pattern, ensuring that only one instance of the program can run at any given time.
- If a user attempts to start a second instance, the existing instance should be brought to the foreground or activated, and the new instance should exit immediately.
