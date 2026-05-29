# EVE Market Explorer

EVE Market Explorer is a desktop tool for finding hauling trade opportunities in EVE Online. It scans public ESI market orders, applies your ISK and cargo limits, accounts for sales tax, route safety, margin, contraband filters, and shows practical routes you can fly.

The app is built for traders and haulers who want fast answers to questions like:

- What can I buy here and sell somewhere else for a good profit?
- Is there a profitable round trip instead of flying back empty?
- How much ISK and cargo space does the route actually need?
- Does the route stay in high-sec, and does Accounting change the result?

## Features

### Trade Deals

Find one-way market opportunities from a selected starting system.

The app looks for items available in your start system, then matches them with profitable buy orders elsewhere. Results include:

- item name
- buy station and sell station
- route jumps
- buy and sell prices
- quantity
- ISK per jump
- total profit
- margin
- cargo volume

### Trade Loops

Find circular trade routes that bring you back to the starting point.

You can search 2, 3, or 4 point loops. Each stop shows:

- the station to visit
- the item to buy at that station
- the quantity to haul
- total jumps for one full loop
- maximum cargo volume used
- ISK per jump for the loop
- total profit for the loop
- loop margin
- peak ISK required for any single leg

If you leave the system field empty, the app searches for good loops globally. If you enter a system, loops start and end there.

## Search Settings

- **System**: starting system for Trade Deals, or optional start/end system for Trade Loops.
- **ISK**: your available budget.
- **Cargo**: available cargo volume.
- **Margin %**: minimum margin threshold.
- **Min. profit**: minimum total profit.
- **Accounting**: Accounting skill level, used to reduce sales tax.
- **Safe routes**: when enabled, routes are checked for high-sec travel.
- **Contraband**: when disabled, known contraband types are filtered out.
- **Loop points**: maximum number of stops in Trade Loops, from 2 to 4.

## Caching

Market and route data are cached locally to keep repeat searches fast and to avoid hammering ESI.

Cache location:

```text
%LocalAppData%\EveMarketExplorer\cache
```

The app also restores the last visible results on startup for both Trade Deals and Trade Loops, so you do not lose the previous search when reopening it.

## Installation

Use the MSI installer from the release artifacts:

```text
artifacts\msi\EveMarketExplorerSetup.msi
```

There is also a standalone self-contained executable:

```text
artifacts\standalone\EveMarketExplorerStandalone.exe
```

The app installs per-user and does not require administrator rights.

## Build From Source

Requirements:

- Windows x64
- .NET 10 SDK
- WiX CLI for MSI packaging

Build the app:

```powershell
dotnet build
```

Run tests:

```powershell
dotnet run --project EveMarketExplorer.Tests\EveMarketExplorer.Tests.csproj
```

Create release artifacts:

```powershell
.\scripts\Build-Release.ps1
```

Release outputs:

```text
artifacts\publish\win-x64\EveMarketExplorer.exe
artifacts\standalone\EveMarketExplorerStandalone.exe
artifacts\msi\EveMarketExplorerSetup.msi
```

## Notes

EVE Market Explorer uses public EVE ESI data. Market conditions can change quickly, and orders may be gone by the time you arrive. Treat results as decision support, not a guarantee.

EVE Online and EVE are registered trademarks of CCP hf. This project is not affiliated with or endorsed by CCP Games.
