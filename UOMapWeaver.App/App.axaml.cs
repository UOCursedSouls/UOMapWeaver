using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UOMapWeaver.App.Views;

namespace UOMapWeaver.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                UOMapWeaverDataBootstrapper.EnsureDataFolders();
            }
            catch
            {
                // Data bootstrapping should not block app startup.
            }

            AppSettings.Load();
            AppStatus.InitFileLogger();

            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += (_, _) => AppStatus.ShutdownFileLogger();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

