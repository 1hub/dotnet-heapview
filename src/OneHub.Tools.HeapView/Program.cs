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
        var symbolsOption = new Option<string?>("--symbols") { Description = "Matching macOS NativeAOT dSYM or unstripped executable" };
        var cmd = new RootCommand { fileNameArgument, symbolsOption };
        cmd.SetAction(parseResult => HandleView(parseResult.GetValue(fileNameArgument), parseResult.GetValue(symbolsOption)));
        return cmd.Parse(args).Invoke();
    }

    static void HandleView(string? inputFileName, string? symbolFilePath)
    {
        App.SymbolFilePath = symbolFilePath;
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(inputFileName != null ? new[] { inputFileName } : Array.Empty<string>());
    }
}
