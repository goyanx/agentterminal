using System.Diagnostics;
using System.Text.Json;

internal static class McpServer
{
    private const string LatestProtocolVersion = "2025-11-25";
    private static readonly HashSet<string> SupportedProtocolVersions =
    [
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        LatestProtocolVersion,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        // MCP stdio reserves stdout exclusively for newline-delimited JSON-RPC.
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement? id = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement request = document.RootElement;
                if (request.TryGetProperty("id", out JsonElement idNode)) id = idNode.Clone();
                string method = request.TryGetProperty("method", out JsonElement methodNode)
                    ? methodNode.GetString() ?? ""
                    : "";

                if (id is null)
                    continue; // Notifications do not receive JSON-RPC responses.

                object result = method switch
                {
                    "initialize" => Initialize(request),
                    "ping" => new { },
                    "tools/list" => new { tools = ToolDefinitions() },
                    "tools/call" => await CallToolAsync(request),
                    _ => throw new McpProtocolException(-32601, $"Method not found: {method}"),
                };

                await WriteResponseAsync(new { jsonrpc = "2.0", id, result });
            }
            catch (McpProtocolException ex)
            {
                await WriteResponseAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = ex.Code, message = ex.Message },
                });
            }
            catch (JsonException ex)
            {
                await WriteResponseAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32700, message = $"Parse error: {ex.Message}" },
                });
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32603, message = $"Internal error: {ex.Message}" },
                });
            }
        }

        return 0;
    }

    private static object Initialize(JsonElement request)
    {
        string requested = "";
        if (request.TryGetProperty("params", out JsonElement parameters) &&
            parameters.TryGetProperty("protocolVersion", out JsonElement versionNode))
            requested = versionNode.GetString() ?? "";

        string selected = SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion;
        return new
        {
            protocolVersion = selected,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new
            {
                name = "agentterminal",
                title = "AgentTerminal",
                version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                websiteUrl = "https://github.com/goyanx/agentterminal",
            },
            instructions = "Controls trusted local AgentTerminal sessions. Commands execute as the current Windows user. Require user approval for consequential commands.",
        };
    }

    private static async Task<object> CallToolAsync(JsonElement request)
    {
        if (!request.TryGetProperty("params", out JsonElement parameters) ||
            !parameters.TryGetProperty("name", out JsonElement nameNode))
            throw new McpProtocolException(-32602, "tools/call requires params.name");

        string name = nameNode.GetString() ?? "";
        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement argsNode)
            ? argsNode
            : JsonSerializer.SerializeToElement(new { });

        string[] cliArgs = name switch
        {
            "agent_terminal_open" => BuildOpenArguments(arguments),
            "agent_terminal_run" => BuildRunArguments(arguments),
            "agent_terminal_ping" => ["ping", "--name", RequiredString(arguments, "session")],
            "agent_terminal_stop" => ["stop", "--name", RequiredString(arguments, "session")],
            _ => throw new McpProtocolException(-32602, $"Unknown tool: {name}"),
        };

        ChildResult child = await InvokeCliAsync(cliArgs);
        JsonElement structured;
        try
        {
            structured = JsonSerializer.Deserialize<JsonElement>(child.Stdout);
        }
        catch (JsonException)
        {
            structured = JsonSerializer.SerializeToElement(new
            {
                ok = false,
                exitCode = child.ExitCode,
                stdout = child.Stdout,
                stderr = child.Stderr,
            });
        }

        string text = JsonSerializer.Serialize(structured, new JsonSerializerOptions { WriteIndented = true });
        return new
        {
            content = new[] { new { type = "text", text } },
            structuredContent = structured,
            isError = child.ExitCode != 0,
        };
    }

    private static string[] BuildOpenArguments(JsonElement arguments)
    {
        string action = RequiredString(arguments, "action") switch
        {
            "new_window" => "new-window",
            "new_tab" => "new-tab",
            "split_pane" => "split-pane",
            var value => throw new McpProtocolException(-32602, $"Unsupported open action: {value}"),
        };

        var result = new List<string> { action, "--name", RequiredString(arguments, "session") };
        AddOptional(result, arguments, "window", "--window");
        AddOptional(result, arguments, "cwd", "--cwd");
        AddOptional(result, arguments, "title", "--title");

        if (OptionalBoolean(arguments, "maximized")) result.Add("--maximized");
        if (action == "split-pane")
        {
            string? orientation = OptionalString(arguments, "orientation");
            if (orientation is not null)
                result.Add(orientation == "horizontal" ? "--horizontal" : orientation == "vertical"
                    ? "--vertical"
                    : throw new McpProtocolException(-32602, "orientation must be horizontal or vertical"));
            if (arguments.TryGetProperty("size", out JsonElement sizeNode))
            {
                if (sizeNode.ValueKind != JsonValueKind.Number || !sizeNode.TryGetDouble(out double size))
                    throw new McpProtocolException(-32602, "size must be a number");
                result.Add("--size");
                result.Add(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return result.ToArray();
    }

    private static string[] BuildRunArguments(JsonElement arguments)
    {
        string mode = RequiredString(arguments, "mode");
        var result = new List<string> { "run", "--name", RequiredString(arguments, "session") };
        AddOptional(result, arguments, "cwd", "--cwd");

        switch (mode)
        {
            case "direct":
                result.Add("--");
                result.Add(RequiredString(arguments, "program"));
                if (arguments.TryGetProperty("arguments", out JsonElement argumentList))
                {
                    if (argumentList.ValueKind != JsonValueKind.Array)
                        throw new McpProtocolException(-32602, "arguments must be an array of strings");
                    foreach (JsonElement item in argumentList.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            throw new McpProtocolException(-32602, "arguments must contain only strings");
                        result.Add(item.GetString()!);
                    }
                }
                break;
            case "powershell":
                result.Add("--shell");
                result.Add(RequiredString(arguments, "command"));
                break;
            case "wsl":
                result.Add("--wsl");
                result.Add(RequiredString(arguments, "command"));
                break;
            default:
                throw new McpProtocolException(-32602, "mode must be direct, powershell, or wsl");
        }

        return result.ToArray();
    }

    private static async Task<ChildResult> InvokeCliAsync(string[] args)
    {
        IReadOnlyList<string> launch = AgentTerminalApp.CurrentLaunchCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = launch[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string prefix in launch.Skip(1)) startInfo.ArgumentList.Add(prefix);
        startInfo.ArgumentList.Add("--json");
        foreach (string arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ChildResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    private static object[] ToolDefinitions() =>
    [
        new
        {
            name = "agent_terminal_open",
            title = "Open AgentTerminal Surface",
            description = "Create a visible AgentTerminal window, tab, or split pane. Every live session name must be unique.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "new_window", "new_tab", "split_pane" } },
                    session = new { type = "string", description = "Unique session name used by later run, ping, and stop calls." },
                    window = new { type = "string", description = "Named Windows Terminal window group." },
                    cwd = new { type = "string", description = "Existing Windows working-directory path." },
                    title = new { type = "string", description = "Optional visible tab title." },
                    orientation = new { type = "string", @enum = new[] { "horizontal", "vertical" } },
                    size = new { type = "number", exclusiveMinimum = 0.05, exclusiveMaximum = 0.95 },
                    maximized = new { type = "boolean" },
                },
                required = new[] { "action", "session" },
                additionalProperties = false,
            },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = false, openWorldHint = false },
        },
        new
        {
            name = "agent_terminal_run",
            title = "Run Visible Command",
            description = "Run a command in a named visible AgentTerminal session. This can perform arbitrary actions as the current user; obtain approval when consequential.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    session = new { type = "string" },
                    mode = new { type = "string", @enum = new[] { "direct", "powershell", "wsl" } },
                    program = new { type = "string", description = "Executable for direct mode." },
                    arguments = new { type = "array", items = new { type = "string" }, description = "Argument list for direct mode." },
                    command = new { type = "string", description = "One command string for powershell or wsl mode." },
                    cwd = new { type = "string", description = "Optional existing Windows working-directory path." },
                },
                required = new[] { "session", "mode" },
                additionalProperties = false,
            },
            annotations = new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = true },
        },
        new
        {
            name = "agent_terminal_ping",
            title = "Check AgentTerminal Session",
            description = "Check whether a named AgentTerminal session is reachable.",
            inputSchema = SessionSchema(),
            annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false },
        },
        new
        {
            name = "agent_terminal_stop",
            title = "Stop AgentTerminal Session",
            description = "Stop one named AgentTerminal session without stopping other tabs or panes.",
            inputSchema = SessionSchema(),
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false },
        },
    ];

    private static object SessionSchema() => new
    {
        type = "object",
        properties = new { session = new { type = "string" } },
        required = new[] { "session" },
        additionalProperties = false,
    };

    private static string RequiredString(JsonElement arguments, string name) =>
        OptionalString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new McpProtocolException(-32602, $"{name} is required and must be a non-empty string");

    private static string? OptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement node)) return null;
        if (node.ValueKind != JsonValueKind.String)
            throw new McpProtocolException(-32602, $"{name} must be a string");
        return node.GetString();
    }

    private static bool OptionalBoolean(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement node)) return false;
        if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new McpProtocolException(-32602, $"{name} must be a boolean");
        return node.GetBoolean();
    }

    private static void AddOptional(List<string> result, JsonElement arguments, string property, string option)
    {
        if (OptionalString(arguments, property) is not { } value) return;
        result.Add(option);
        result.Add(value);
    }

    private static async Task WriteResponseAsync(object response)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        await Console.Out.FlushAsync();
    }

    private sealed record ChildResult(int ExitCode, string Stdout, string Stderr);

    private sealed class McpProtocolException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
