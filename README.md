# EVE Market Explorer

The console prototype remains separate at:

```text
C:\Projects\EveParser\EveParser
```

Run this GUI project from this folder:

```powershell
dotnet run
```

Build:

```powershell
dotnet build
```

Release publish:

```powershell
.\scripts\Build-Release.ps1
```

The script creates a self-contained Windows x64 build in:

```text
artifacts\publish\win-x64
```

Standalone build:

```text
artifacts\standalone\EveMarketExplorerStandalone.exe
```

If Inno Setup 6 is installed, the same script also creates the installer:

```text
artifacts\installer\EveMarketExplorerSetup.exe
```

If WiX Toolset CLI is installed, the same script also creates the MSI installer:

```text
artifacts\msi\EveMarketExplorerSetup.msi
```

Install WiX CLI:

```powershell
dotnet tool install --global wix
```

Runtime cache is stored per user in:

```text
%LocalAppData%\EveMarketExplorer\cache
```
