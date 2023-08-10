using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;

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
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            switch (desktop.Args)
            {
                case [string fileName, ..] when File.Exists(fileName):
                    mainWindow.Open(fileName);
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}