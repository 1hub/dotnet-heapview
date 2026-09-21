using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneHub.Diagnostics.HeapView;

internal static class SpotlightDsymLocator
{
    internal readonly record struct CommandResult(int ExitCode, string Output, string Error);

    public static IReadOnlyList<string> Find(Guid uuid, TextWriter log)
    {
        if (!OperatingSystem.IsMacOS())
            return Array.Empty<string>();
        return Find(uuid, log, startInfo => RunAsync(startInfo, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
    }

    internal static IReadOnlyList<string> Find(Guid uuid, TextWriter log, Func<ProcessStartInfo, CommandResult> run)
    {
        if (uuid == Guid.Empty)
            return Array.Empty<string>();

        var startInfo = new ProcessStartInfo("/usr/bin/mdfind")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        // Null separators preserve paths containing spaces or newlines. ArgumentList
        // passes the entire query as one argument without invoking a shell.
        startInfo.ArgumentList.Add("-0");
        startInfo.ArgumentList.Add($"com_apple_xcode_dsym_uuids == {uuid.ToString("D").ToUpperInvariant()}");
        log.WriteLine($"Searching Spotlight for dSYM UUID {uuid:D}.");
        try
        {
            var result = run(startInfo);
            if (result.ExitCode != 0)
            {
                log.WriteLine($"Spotlight dSYM lookup failed (exit {result.ExitCode}): {result.Error.Trim()}");
                return Array.Empty<string>();
            }
            return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or TimeoutException)
        {
            log.WriteLine($"Spotlight dSYM lookup unavailable: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    internal static async Task<CommandResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var cancellation = new CancellationTokenSource(timeout);
        // Drain both pipes concurrently so a full stderr buffer cannot block exit.
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation.Token)).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) // The process exited while the timeout fired.
            {
            }
            throw new TimeoutException($"mdfind did not complete within {timeout.TotalSeconds:g} seconds.");
        }
    }
}
