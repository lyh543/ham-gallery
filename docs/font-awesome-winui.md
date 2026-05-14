# Font Awesome in FluentGallery

This project uses `FontAwesome6.Fonts.WinUI` for WinUI 3 icon rendering.

## Why this package

- It provides a WinUI control that can be used directly in XAML.
- The free package ships its own font files and loads them at runtime.
- It works for the current `FluentGallery` WinUI 3 app without adding custom converters or icon wrappers.

## 1. Add the package

Add the NuGet package to [FluentGallery/FluentGallery.csproj](c:/Users/lyh54/git/github/ham-gallery/FluentGallery/FluentGallery.csproj):

```xml
<PackageReference Include="FontAwesome6.Fonts.WinUI" Version="2.5.1" />
```

## 2. Add the XAML namespace

In the page or control that uses the icons, add this namespace:

```xml
xmlns:fa="using:FontAwesome6.Fonts"
```

Do not use the XML schema URI form here. In this project, WinUI XAML resolves the control correctly through the `using:` namespace.

## 3. Use the control in XAML

Example from [FluentGallery/Views/PhotoDetailPage.xaml](c:/Users/lyh54/git/github/ham-gallery/FluentGallery/Views/PhotoDetailPage.xaml):

```xml
<Button Click="RotateCcw_Click"
        Width="40" Height="40"
        IsEnabled="{x:Bind ViewModel.CanRotate, Mode=OneWay}"
        ToolTipService.ToolTip="逆时针旋转 90°">
    <fa:FontAwesome Icon="Solid_RotateLeft" FontSize="14" />
</Button>

<Button Click="RotateCw_Click"
        Width="40" Height="40"
        IsEnabled="{x:Bind ViewModel.CanRotate, Mode=OneWay}"
        ToolTipService.ToolTip="顺时针旋转 90°">
    <fa:FontAwesome Icon="Solid_RotateRight" FontSize="14" />
</Button>
```

Common usage pattern:

```xml
<fa:FontAwesome Icon="Solid_RotateRight" FontSize="14" />
```

## 4. Runtime note for this repository

`FontAwesome6.Fonts.WinUI` extracts font files and builds `FontFamily` values from a runtime path.

In unpackaged runs, the library may fall back to `Directory.GetCurrentDirectory()`. If the app is launched from the repository root, that can create a top-level `Fonts/` folder and may lead to incorrect font loading behavior.

To keep the runtime path stable, this project sets the working directory to `AppContext.BaseDirectory` at startup in [FluentGallery/App.xaml.cs](c:/Users/lyh54/git/github/ham-gallery/FluentGallery/App.xaml.cs).

## 5. Why the repository ignores `/Fonts/`

The root-level `/Fonts/` directory is a runtime-generated artifact in this repository, not a source asset.

- Ignore `/Fonts/` in the repository root.
- Do not ignore `FluentGallery/Fonts/` globally unless the project later decides to store committed app resources there.

Current `.gitignore` rule:

```gitignore
/Fonts/
```

## 6. When you would commit fonts instead

If the project later switches to a setup that intentionally loads fonts from app content, for example a committed `FluentGallery/Fonts/` directory, those files should be added explicitly to the project and should not be covered by the root-only ignore rule above.

## 7. Quick checklist

1. Add the NuGet package.
2. Add `xmlns:fa="using:FontAwesome6.Fonts"`.
3. Use `<fa:FontAwesome Icon="..." />` in XAML.
4. Keep startup working directory stable for unpackaged runs.
5. Ignore only the generated repository-root `/Fonts/` directory.