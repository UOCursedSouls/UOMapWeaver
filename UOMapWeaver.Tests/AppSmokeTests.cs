using Avalonia;
using Avalonia.Controls;
using UOMapWeaver.App.Views;

namespace UOMapWeaver.Tests;

public sealed class AppSmokeTests
{
    [Fact]
    public void BlankBmpView_Constructs_WithFillControls()
    {
        AvaloniaTestApp.Ensure();

        var view = new BlankBmpView();
        Assert.NotNull(view.FindControl<ComboBox>("FillModeComboBox"));
        Assert.NotNull(view.FindControl<ComboBox>("FillTerrainComboBox"));
        Assert.NotNull(view.FindControl<ComboBox>("PaletteQuickComboBox"));
    }

    [Fact]
    public void MainWindow_Constructs_WithContent()
    {
        AvaloniaTestApp.Ensure();

        var window = new MainWindow();
        Assert.NotNull(window.Content);
    }
}

internal static class AvaloniaTestApp
{
    private static bool _initialized;

    public static void Ensure()
    {
        if (_initialized)
        {
            return;
        }

        AppBuilder.Configure<UOMapWeaver.App.App>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        _initialized = true;
    }
}
