# Tide

Tide is a calm, native Windows reader for website updates. Register a site or
feed URL and Tide collects new stories into one focused inbox.

The app is built with C# and WinUI 3 on the stable Windows App SDK. It does not
embed a browser engine and no longer carries cross-platform runtime code.

## Features

- RSS and Atom parsing with feed autodiscovery
- HTML article-card fallback for sites without feeds
- Unread, saved, source, and text filters
- Portable JSON storage under the `Data` folder beside `Tide.App.exe`
- Native WinUI 3 controls and Windows visual language

## Develop

Requirements:

- Visual Studio Community 2026 with WinUI application development tools
- .NET SDK 10
- Windows SDK 10.0.26100 or later

Build:

```powershell
dotnet restore Tide.slnx
dotnet build Tide.slnx -c Release
```

Run:

```powershell
dotnet run --project src/Tide.App/Tide.App.csproj
```

Run the dependency-free Core verification suite:

```powershell
dotnet run --project tests/Tide.Core.Tests/Tide.Core.Tests.csproj -c Release
```

## Delivery

Pull requests run the lightweight Core verification suite. Merges to `main`
publish the native Windows x64 app as a GitHub Actions artifact and create a
versioned GitHub Release automatically.

The initial app is unpackaged and framework-dependent to keep output small.
Extract it into a writable folder before running it. Settings, diagnostics, and
collected update metadata stay inside its adjacent `Data` folder. Existing data
from older builds is moved out of `%LOCALAPPDATA%\Tide` on the first launch.
