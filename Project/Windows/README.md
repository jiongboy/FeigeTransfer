# FeigeTransfer for Windows

Windows 10/11 client built with WinUI 3 and Windows App SDK. Windows and macOS share the v3 session protocol and are maintained in the same repository and `main` branch.

## Build and test

Use a Windows development environment with .NET 10 and the WinUI application development workload. See [Environment setup](../Docs/Windows/EnvironmentSetup.md).

```powershell
dotnet test .\tests\Feige.Core.Tests\Feige.Core.Tests.csproj
```

Development scripts are under `scripts/`. EXE and MSIX packaging require a Windows host; final outputs go under the repository-adjacent `Release/` folder. Do not create delivery packages unless the user explicitly requests packaging after reviewing changes.

## Project status and release notes

- [Project overview](../Docs/Windows/ProjectOverview.md)
- [Developer guide](../Docs/Windows/DeveloperGuide.md)
- [File receive approval](../Docs/Windows/FileReceiveApproval.md)
- [Update service](../Docs/Windows/UpdateService.md)
- [Version 1.2.0 delivery notes](../Docs/Windows/ReleaseNotes-1.2.0.md)
- [Documentation index](../Docs/Windows/README.md)
