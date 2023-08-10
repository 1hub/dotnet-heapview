using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;

namespace OneHub.Tools.HeapView;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Length > 0)
                desktop.MainWindow = new MainWindow(desktop.Args[0]);
            else
                Environment.Exit(1);
        }

        base.OnFrameworkInitializationCompleted();
    }
}