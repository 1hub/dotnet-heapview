using Avalonia;
using System;
using System.CommandLine;

namespace OneHub.Tools.HeapView;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var fileNameArgument = new Argument<string?>("filename", () => null, "Path to .gcdump file");
        var cmd = new RootCommand { fileNameArgument };
        cmd.SetHandler(HandleView, fileNameArgument);
        return cmd.Invoke(args);
    }

    static void HandleView(string? inputFileName)
    {
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(inputFileName != null ? new[] { inputFileName } : Array.Empty<string>());
    }
}
