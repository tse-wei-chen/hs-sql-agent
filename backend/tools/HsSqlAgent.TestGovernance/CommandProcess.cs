using System.Diagnostics;

namespace HsSqlAgent.TestGovernance;

public sealed record CommandResult(int ExitCode, string Output, string Error);

public static class CommandProcess
{
    public static CommandResult Run(string command, IEnumerable<string> arguments, string? directory = null,
        bool allowFailure = false, bool echo = false)
    {
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // pnpm is a command shim on Windows. cmd receives only fixed executable names;
        // callers must use the installed pnpm executable rather than interpolated shell code.
        if (OperatingSystem.IsWindows() && command == "pnpm")
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("pnpm");
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new GovernanceException($"Cannot start {command}");
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var result = new CommandResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
            if (echo) { Console.Write(result.Output); Console.Error.Write(result.Error); }
            if (!allowFailure && result.ExitCode != 0)
                throw new GovernanceException($"{command} failed ({result.ExitCode}): {result.Error}\n{result.Output}");
            return result;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
