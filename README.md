# fluent-gallery

Fluent Gallery is being implemented incrementally from [PROMPT.md](PROMPT.md).

## Current status

- Step 1 completed: WinUI 3 solution scaffold
- Includes solution structure, dependency injection bootstrap, localized resources, and a basic `NavigationView` shell
- Data and feature services are present as placeholders for the next steps

## Prerequisites

To build the app locally, install:

- .NET 9 SDK
- Windows App SDK 1.8 runtime (bundled via NuGet; no separate installer required)
- Windows 10 SDK 10.0.19041.0 or newer (comes with Visual Studio 2022)

## Build & Run

All commands are run from the `FluentGallery/FluentGallery/` directory.

### Restore packages

```powershell
dotnet restore --runtime win-x64
```

### Build (Debug, x64)

```powershell
dotnet build -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

### Run directly after build

```powershell
.\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

### Build & run in one step

```powershell
dotnet build -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug ; .\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

> **Note:** The project uses `WindowsPackageType=None` (unpackaged) so no MSIX packaging or sideloading is needed during development.  
> You can also open `FluentGallery/FluentGallery.sln` in Visual Studio 2022, set the platform to **x64**, and press **F5**.

## Test

All commands are run from the `FluentGallery/` directory (solution root).

### Run all tests

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug
```

### Run with detailed output

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --logger "console;verbosity=normal"
```

### Run a single test by name

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --filter "FullyQualifiedName~DeletePhoto_CascadeDeletesThumbnail"
```

## Solution layout

- [FluentGallery.sln](FluentGallery.sln)
- [FluentGallery/FluentGallery.csproj](FluentGallery/FluentGallery.csproj)
- [FluentGallery.Tests/FluentGallery.Tests.csproj](FluentGallery.Tests/FluentGallery.Tests.csproj)