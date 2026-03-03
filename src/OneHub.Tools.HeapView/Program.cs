using Avalonia;
using System;
using System.CommandLine;

namespace OneHub.Tools.HeapView;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var fileNameArgument = new Argument<string?>("filename") { Description = "Path to .gcdump file", Arity = ArgumentArity.ZeroOrOne };
        var cmd = new RootCommand { fileNameArgument };
        cmd.SetAction(parseResult => HandleView(parseResult.GetValue(fileNameArgument)));
        return cmd.Parse(args).Invoke();
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
