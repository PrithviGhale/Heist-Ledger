# Building Heist Ledger

## Requirements

- Windows 10 or Windows 11
- .NET 9 SDK
- Microsoft Visual C++ 2015–2022 x64 Redistributable for OCR runtime testing

## Build

```powershell
dotnet restore src\HeistLedger\GTAEarningsTracker.csproj
dotnet build src\HeistLedger\GTAEarningsTracker.csproj -c Release --no-restore
```

## Regression tests

```powershell
dotnet run --project Tests\GeneratedQuickCaptureTests\GeneratedQuickCaptureTests.csproj -c Release
dotnet run --project Tests\PaddleOcrRegressionTests\PaddleOcrRegressionTests.csproj -c Release
dotnet run --project Tests\EarningsSessionTests\EarningsSessionTests.csproj -c Release
dotnet run --project Tests\DiscordWebhookSmokeTests\DiscordWebhookSmokeTests.csproj -c Release
```

The screenshot fixtures under `Tests/Fixtures` are regression inputs. They are not copied into the published application.

## Publish Windows x64

```powershell
dotnet publish src\HeistLedger\GTAEarningsTracker.csproj -p:PublishProfile=WindowsX64 -o artifacts\publish\win-x64
```

The publish profile creates a self-contained, single-file Windows x64 application. The OCR model files are extracted beside the executable at runtime by the .NET single-file host.
