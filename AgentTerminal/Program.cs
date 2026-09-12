using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    return await AgentTerminalApp.RunAsync(args);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled.");
    return 130;
}
catch (Exception ex)
{
    AgentTerminalApp.WriteFatalError(ex.Message);
    return 1;
}

internal static class AgentTerminalApp
{
    private const string DefaultName = "main";
    private const string DefaultWindow = "main";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static bool JsonOutput { get; private set; }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--json")
        {
            JsonOutput = true;
            args = args[1..];
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            if (JsonOutput)
                WriteJson(new { ok = true, command = "help", commands = new[] { "new-window", "new-tab", "split-pane", "run", "ping", "stop", "mcp" } });
            else
                PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "start" => await OpenSurfaceAsync(args[1..], SurfaceKind.Window),
            "new-window" or "window" => await OpenSurfaceAsync(args[1..], SurfaceKind.Window),
            "new-tab" or "tab" => await OpenSurfaceAsync(args[1..], SurfaceKind.Tab),
            "split-pane" or "split" => await OpenSurfaceAsync(args[1..], SurfaceKind.Pane),
            "host" => await HostAsync(args[1..]),
            "mcp" => await McpServer.RunAsync(),
            "run" => await RunCommandAsync(args[1..]),
            "ping" => await SimpleRequestAsync(args[1..], "ping"),
            "stop" => await SimpleRequestAsync(args[1..], "stop"),
            _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run with --help."),
        };
    }

    private static async Task<int> OpenSurfaceAsync(string[] args, SurfaceKind kind)
    {
        var options = ParseOptions(args);
        string name = ValidateName(options.Value("name") ?? DefaultName);
        string window = ValidateName(options.Value("window") ?? (kind == SurfaceKind.Window ? name : DefaultWindow));
        string cwd = Path.GetFullPath(options.Value("cwd") ?? Environment.CurrentDirectory);
        string title = options.Value("title") ?? $"Agent Terminal · {name}";

        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException($"Working directory does not exist: {cwd}");

        if (await CanConnectAsync(name, 150))
        {
            WriteResult(new { ok = true, action = "open", session = name, window, alreadyRunning = true });
            return 0;
        }

        var launch = CurrentLaunchCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = true,
            WorkingDirectory = cwd,
        };

        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(TerminalWindowName(window));
        if (kind == SurfaceKind.Window && options.Has("maximized")) startInfo.ArgumentList.Add("--maximized");
        startInfo.ArgumentList.Add(kind == SurfaceKind.Pane ? "split-pane" : "new-tab");
        if (kind == SurfaceKind.Pane)
        {
            bool horizontal = options.Has("horizontal");
            bool vertical = options.Has("vertical");
            if (horizontal && vertical)
                throw new ArgumentException("Choose either --horizontal or --vertical, not both.");
            if (horizontal) startInfo.ArgumentList.Add("--horizontal");
            if (vertical) startInfo.ArgumentList.Add("--vertical");
            if (options.Value("size") is { } rawSize)
            {
                if (!double.TryParse(rawSize, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double size) || size <= 0.05 || size >= 0.95)
                    throw new ArgumentException("--size must be a decimal greater than 0.05 and less than 0.95.");
                startInfo.ArgumentList.Add("--size");
                startInfo.ArgumentList.Add(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add("--suppressApplicationTitle");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(cwd);
        foreach (string item in launch)
            startInfo.ArgumentList.Add(item);
        startInfo.ArgumentList.Add("host");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(cwd);
        startInfo.ArgumentList.Add("--window");
        startInfo.ArgumentList.Add(window);

        Process.Start(startInfo);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (await CanConnectAsync(name, 150))
            {
                string surface = kind switch
                {
                    SurfaceKind.Window => "window",
                    SurfaceKind.Tab => "tab",
                    _ => "pane",
                };
                WriteResult(new { ok = true, action = "open", surface, session = name, window, cwd });
                return 0;
            }
            await Task.Delay(100);
        }

        throw new InvalidOperationException("Windows Terminal was started, but its AgentTerminal host did not become reachable.");
    }

    private static async Task<int> HostAsync(string[] args)
    {
        var options = ParseOptions(args);
        string name = ValidateName(options.Value("name") ?? DefaultName);
        string window = ValidateName(options.Value("window") ?? name);
        string cwd = Path.GetFullPath(options.Value("cwd") ?? Environment.CurrentDirectory);
        Directory.SetCurrentDirectory(cwd);
        Console.Title = $"Agent Terminal · {name}";

        WriteBanner(name, window, cwd);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        while (!shutdown.IsCancellationRequested)
        {
            await using var pipe = CreateServer(name);
            try
            {
                await pipe.WaitForConnectionAsync(shutdown.Token);
                bool shouldStop = await HandleConnectionAsync(pipe, name, cwd, shutdown.Token);
                if (shouldStop) break;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[control connection ended: {ex.Message}]");
            }
        }

        Console.WriteLine("\nAgentTerminal host stopped.");
        return 0;
    }

    private static async Task<bool> HandleConnectionAsync(Stream pipe, string name, string hostCwd, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null) return false;

        Request request = JsonSerializer.Deserialize<Request>(line, JsonOptions)
            ?? throw new InvalidDataException("Empty request.");

        switch (request.Action)
        {
            case "ping":
                await SendAsync(writer, new Response("pong", Message: name), cancellationToken);
                return false;
            case "stop":
                await SendAsync(writer, new Response("result", ExitCode: 0), cancellationToken);
                return true;
            case "run":
                await ExecuteAsync(request, writer, hostCwd, cancellationToken);
                return false;
            default:
                await SendAsync(writer, new Response("error", Message: $"Unknown action '{request.Action}'."), cancellationToken);
                return false;
        }
    }

    private static async Task ExecuteAsync(Request request, StreamWriter writer, string hostCwd, CancellationToken cancellationToken)
    {
        if (request.Command is not { Length: > 0 })
        {
            await SendAsync(writer, new Response("error", Message: "No command was supplied."), cancellationToken);
            return;
        }

        string cwd = Path.GetFullPath(request.Cwd ?? hostCwd);
        if (!Directory.Exists(cwd))
        {
            await SendAsync(writer, new Response("error", Message: $"Working directory does not exist: {cwd}"), cancellationToken);
            return;
        }

        string display = request.Shell || request.Wsl
            ? $"{(request.Wsl ? "[WSL] " : string.Empty)}{request.Command[0]}"
            : string.Join(" ", request.Command.Select(QuoteForDisplay));

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"\n[{DateTimeOffset.Now:HH:mm:ss}] {cwd}\n❯ ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(display);
        Console.ResetColor();

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        if (request.Wsl)
        {
            startInfo.FileName = "wsl.exe";
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(request.Command[0]);
        }
        else if (request.Shell)
        {
            startInfo.FileName = FindPowerShell();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(request.Command[0]);
        }
        else
        {
            startInfo.FileName = request.Command[0];
            foreach (string arg in request.Command.Skip(1))
                startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(ex.Message);
            Console.ResetColor();
            await SendAsync(writer, new Response("error", Message: ex.Message), cancellationToken);
            return;
        }

        var writeGate = new SemaphoreSlim(1, 1);
        Task stdout = PumpAsync(process.StandardOutput, "stdout", Console.Out, writer, writeGate, cancellationToken);
        Task stderr = PumpAsync(process.StandardError, "stderr", Console.Error, writer, writeGate, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr);

        Console.ForegroundColor = process.ExitCode == 0 ? ConsoleColor.DarkGray : ConsoleColor.Red;
        Console.WriteLine($"[exit {process.ExitCode}]");
        Console.ResetColor();
        await SendLockedAsync(writer, writeGate, new Response("result", ExitCode: process.ExitCode), cancellationToken);
    }

    private static async Task PumpAsync(StreamReader source, string type, TextWriter console, StreamWriter client,
        SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        char[] buffer = new char[2048];
        while (true)
        {
            int count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            string chunk = new(buffer, 0, count);
            await console.WriteAsync(chunk);
            await console.FlushAsync();
            await SendLockedAsync(client, writeGate, new Response(type, Data: chunk), cancellationToken);
        }
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        var parsed = ParseRun(args);
        string name = ValidateName(parsed.Name ?? DefaultName);
        var request = new Request("run", parsed.Command, parsed.Cwd, parsed.Shell, parsed.Wsl);
        return await SendRequestAsync(name, request, echoOutput: !JsonOutput);
    }

    private static async Task<int> SimpleRequestAsync(string[] args, string action)
    {
        var options = ParseOptions(args);
        string name = ValidateName(options.Value("name") ?? DefaultName);
        return await SendRequestAsync(name, new Request(action), echoOutput: !JsonOutput && action != "ping");
    }

    private static async Task<int> SendRequestAsync(string name, Request request, bool echoOutput)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        await using var pipe = new NamedPipeClientStream(".", PipeName(name), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(2000);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Agent terminal '{name}' is not running. Start it with: agent-terminal start --name {name}");
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

        while (await reader.ReadLineAsync() is { } line)
        {
            Response response = JsonSerializer.Deserialize<Response>(line, JsonOptions)
                ?? throw new InvalidDataException("Invalid response from host.");
            switch (response.Type)
            {
                case "stdout" when echoOutput:
                    Console.Out.Write(response.Data);
                    break;
                case "stdout":
                    stdout.Append(response.Data);
                    break;
                case "stderr" when echoOutput:
                    Console.Error.Write(response.Data);
                    break;
                case "stderr":
                    stderr.Append(response.Data);
                    break;
                case "pong":
                    WriteResult(new { ok = true, action = "ping", session = response.Message, reachable = true });
                    return 0;
                case "result":
                    if (JsonOutput)
                        WriteJson(new
                        {
                            ok = (response.ExitCode ?? 0) == 0,
                            action = request.Action,
                            session = name,
                            exitCode = response.ExitCode ?? 0,
                            stdout = stdout.ToString(),
                            stderr = stderr.ToString(),
                        });
                    return response.ExitCode ?? 0;
                case "error":
                    if (JsonOutput)
                        WriteJson(new { ok = false, action = request.Action, session = name, error = response.Message });
                    else
                        Console.Error.WriteLine(response.Message);
                    return 1;
            }
        }

        throw new IOException("The AgentTerminal host disconnected before returning a result.");
    }

    private static NamedPipeServerStream CreateServer(string name) => new(PipeName(name), PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<bool> CanConnectAsync(string name, int timeoutMs)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(name), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMs);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Request("ping"), JsonOptions));
            return await reader.ReadLineAsync() is not null;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    private static async Task SendAsync(StreamWriter writer, Response response, CancellationToken cancellationToken) =>
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken);

    private static async Task SendLockedAsync(StreamWriter writer, SemaphoreSlim gate, Response response,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await SendAsync(writer, response, cancellationToken); }
        finally { gate.Release(); }
    }

    private static string PipeName(string name) => $"GolemInc.AgentTerminal.{Environment.UserName}.{name}";

    private static string TerminalWindowName(string window) => $"agent-terminal-{window}";

    private static string ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,40}$"))
            throw new ArgumentException("Session names may contain only letters, numbers, dot, underscore, and dash (maximum 40 characters).");
        return name;
    }

    private static string FindPowerShell() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
            : "powershell.exe";

    internal static IReadOnlyList<string> CurrentLaunchCommand()
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine current executable path.");
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return [processPath, Environment.GetCommandLineArgs()[0]];
        return [processPath];
    }

    private static string QuoteForDisplay(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static ParsedOptions ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            string key = args[i][2..];
            if (key is "maximized" or "horizontal" or "vertical") values[key] = null;
            else if (++i < args.Length) values[key] = args[i];
            else throw new ArgumentException($"Missing value for --{key}.");
        }
        return new ParsedOptions(values);
    }

    private static RunOptions ParseRun(string[] args)
    {
        string? name = null;
        string? cwd = null;
        bool shell = false;
        bool wsl = false;
        var command = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                command.AddRange(args[(i + 1)..]);
                break;
            }
            if (args[i] == "--shell")
            {
                shell = true;
                if (++i >= args.Length) throw new ArgumentException("Missing command after --shell.");
                command.Add(args[i]);
                if (i + 1 != args.Length) throw new ArgumentException("--shell accepts one quoted command string.");
                break;
            }
            if (args[i] == "--wsl")
            {
                wsl = true;
                if (++i >= args.Length) throw new ArgumentException("Missing Linux command after --wsl.");
                command.Add(args[i]);
                if (i + 1 != args.Length) throw new ArgumentException("--wsl accepts one quoted command string.");
                break;
            }
            if (args[i] is "--name" or "--cwd")
            {
                string key = args[i];
                if (++i >= args.Length) throw new ArgumentException($"Missing value for {key}.");
                if (key == "--name") name = args[i]; else cwd = args[i];
                continue;
            }
            throw new ArgumentException($"Unexpected argument '{args[i]}'. Put direct commands after --.");
        }
        if (command.Count == 0) throw new ArgumentException("No command supplied. Use: run [options] -- <program> [arguments]");
        return new RunOptions(name, cwd, shell, wsl, command.ToArray());
    }

    private static void WriteBanner(string name, string window, string cwd)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ AgentTerminal — visible, agent-controlled command execution │");
        Console.WriteLine("└──────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine($"Session : {name}");
        Console.WriteLine($"Window  : {window}");
        Console.WriteLine($"Folder  : {cwd}");
        Console.WriteLine("Status  : waiting for an agent command (Ctrl+C closes host)");
    }

    private static void PrintHelp() => Console.WriteLine("""
        AgentTerminal — run agent commands in a Windows Terminal window you can watch

        Usage:
          agent-terminal --json COMMAND [OPTIONS]
          agent-terminal mcp
          agent-terminal start      [--window WINDOW] [--name NAME] [--cwd PATH] [--title TITLE] [--maximized]
          agent-terminal new-window [--window WINDOW] [--name NAME] [--cwd PATH] [--title TITLE] [--maximized]
          agent-terminal new-tab    [--window WINDOW] --name NAME [--cwd PATH] [--title TITLE]
          agent-terminal split-pane [--window WINDOW] --name NAME [--cwd PATH] [--title TITLE]
                                      [--horizontal|--vertical] [--size 0.05-0.95]
          agent-terminal run   [--name NAME] [--cwd PATH] -- PROGRAM [ARGUMENTS...]
          agent-terminal run   [--name NAME] [--cwd PATH] --shell "POWERSHELL COMMAND"
          agent-terminal run   [--name NAME] [--cwd PATH] --wsl "LINUX COMMAND"
          agent-terminal ping  [--name NAME]
          agent-terminal stop  [--name NAME]

        Examples:
          agent-terminal new-window --window work --name shell --cwd C:\src\my-app
          agent-terminal new-tab --window work --name tests --cwd C:\src\my-app
          agent-terminal split-pane --window work --name server --vertical --size 0.4
          agent-terminal run --name tests -- dotnet test
          agent-terminal run --name shell --shell "dotnet build; git status --short"
          agent-terminal run --name hermes --wsl "uname -a && git status --short"

        Commands execute one at a time. Output is streamed both to the visible window and
        back to the calling agent. Named-pipe access is restricted to the current user.
        """);

    private static void WriteResult(object value)
    {
        if (JsonOutput) WriteJson(value);
        else
        {
            JsonElement element = JsonSerializer.SerializeToElement(value, JsonOptions);
            string action = element.TryGetProperty("action", out var actionNode) ? actionNode.GetString() ?? "" : "";
            if (action == "ping")
                Console.WriteLine($"Agent terminal '{element.GetProperty("session").GetString()}' is reachable.");
            else if (element.TryGetProperty("alreadyRunning", out _))
                Console.WriteLine($"Agent terminal '{element.GetProperty("session").GetString()}' is already running.");
            else if (action == "open")
                Console.WriteLine($"Opened visible {element.GetProperty("surface").GetString()} session '{element.GetProperty("session").GetString()}' in window '{element.GetProperty("window").GetString()}'.");
        }
    }

    private static void WriteJson(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    internal static void WriteFatalError(string message)
    {
        if (JsonOutput) WriteJson(new { ok = false, error = message });
        else Console.Error.WriteLine($"agent-terminal: {message}");
    }

    private sealed record Request(string Action, string[]? Command = null, string? Cwd = null, bool Shell = false, bool Wsl = false);
    private sealed record Response(string Type, string? Data = null, int? ExitCode = null, string? Message = null);
    private sealed record RunOptions(string? Name, string? Cwd, bool Shell, bool Wsl, string[] Command);
    private sealed record ParsedOptions(Dictionary<string, string?> Values)
    {
        public string? Value(string key) => Values.GetValueOrDefault(key);
        public bool Has(string key) => Values.ContainsKey(key);
    }

    private enum SurfaceKind
    {
        Window,
        Tab,
        Pane,
    }
}
