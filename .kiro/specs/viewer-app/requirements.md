# Requirements Document

## Introduction

A cross-platform desktop application built with C# .NET 10, using Photino.Blazor as the frontend layer with Angular framework integration. This initial version establishes the application stub that displays a "Hello World" message, serving as the foundation for future feature development.

## Glossary

- **Application**: The cross-platform desktop application built on .NET 10 using Photino.Blazor
- **Photino_Window**: The native OS window rendered by the Photino framework that hosts web content
- **Blazor_Host**: The Blazor server-side or client-side host that serves Angular-rendered content within the Photino window
- **Angular_Frontend**: The Angular framework application responsible for rendering the user interface
- **Hello_World_View**: The initial view displayed to the user upon application launch
- **Published_Executable**: The single-file self-contained binary produced by `dotnet publish`, containing all runtime dependencies and embedded resources

## Requirements

### Requirement 1: Application Launch

**User Story:** As a user, I want to launch the application on my operating system, so that I can see the desktop window appear.

#### Acceptance Criteria

1. WHEN the user starts the Application, THE Photino_Window SHALL open and display the Angular_Frontend content
2. THE Application SHALL target .NET 10 as the runtime framework
3. THE Application SHALL run on Windows, macOS, and Linux without platform-specific builds beyond the standard .NET runtime identifiers

### Requirement 2: Hello World Display

**User Story:** As a user, I want to see a "Hello World" message when the application starts, so that I can confirm the application is working correctly.

#### Acceptance Criteria

1. WHEN the Photino_Window finishes loading, THE Angular_Frontend SHALL display the text "Hello World" in the Hello_World_View
2. THE Hello_World_View SHALL be the default view rendered on application startup

### Requirement 3: Photino.Blazor Integration

**User Story:** As a developer, I want the application to use Photino.Blazor as the bridge between .NET and the frontend, so that I can leverage native OS windowing with web-based UI.

#### Acceptance Criteria

1. THE Application SHALL use Photino.Blazor to host the Angular_Frontend inside the Photino_Window
2. WHEN the Application starts, THE Blazor_Host SHALL initialize and serve the Angular_Frontend to the Photino_Window

### Requirement 4: Angular Frontend Setup

**User Story:** As a developer, I want the frontend to use Angular framework, so that I can build a component-based UI for future features.

#### Acceptance Criteria

1. THE Angular_Frontend SHALL be an Angular application served through the Blazor_Host
2. THE Angular_Frontend SHALL contain a root component that renders the Hello_World_View
3. WHEN the Angular_Frontend is built, THE Application SHALL include the compiled Angular assets in its output

### Requirement 5: Application Window Configuration

**User Story:** As a user, I want the application window to have a reasonable default size and title, so that it looks like a proper desktop application.

#### Acceptance Criteria

1. THE Photino_Window SHALL display a title of "Text Viewer" in the window title bar
2. THE Photino_Window SHALL open with a default size that is appropriate for the user's display
3. THE Photino_Window SHALL be resizable by the user

### Requirement 6: Self-Contained Single-File Deployment

**User Story:** As a developer, I want the application to be a single self-contained executable with no external file dependencies, so that deployment requires copying only one file.

#### Acceptance Criteria

1. WHEN `dotnet publish` is executed, THE Application SHALL produce a self-contained single-file Published_Executable
2. THE Published_Executable SHALL embed all Angular_Frontend compiled assets (JavaScript, CSS, and static files from wwwroot/) as embedded resources within the binary
3. THE Published_Executable SHALL include the .NET runtime and all managed dependencies without requiring separate DLL files at runtime
4. THE Published_Executable SHALL execute without requiring any external files, folders, or installed runtimes on the target machine
