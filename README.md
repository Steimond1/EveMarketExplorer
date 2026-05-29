# EveParser Avalonia

GUI prototype for the EVE market route finder.

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

Current state:

- Avalonia MVVM shell is created.
- Search inputs are laid out as GUI controls.
- Result table uses `DataGrid` with sortable columns.
- Real market calculation is not connected yet; the table shows a sample row.

Next step is to extract the reusable calculation/cache/ESI code from the console
prototype into a shared core library, then wire this GUI to that library.
