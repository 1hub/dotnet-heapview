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
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewApplicationLifetime)
        {
            singleViewApplicationLifetime.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}