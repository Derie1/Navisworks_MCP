using System.Reflection;
using NavisMcpServer;

// dotnet navis_mcp_server.dll                         run as an MCP server over stdio (what AI clients start)
// dotnet navis_mcp_server.dll --register <client>     add this server to an AI client's config
// dotnet navis_mcp_server.dll --unregister <client>   remove it again
//   <client> = claude-desktop | claude-code | codex | all;  --config <path> overrides the config file
// dotnet navis_mcp_server.dll --version
// Exit codes for --register/--unregister: 0 done, 1 error, 3 nothing to remove / client not found.

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

if (args.Length == 0)
    return await new McpServer(version, new Bridge()).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());

switch (args[0])
{
    case "--version":
        Console.WriteLine(version);
        return 0;
    case "--register":
    case "--unregister":
        if (args.Length < 2) break;
        string? config = null;
        for (int i = 2; i < args.Length - 1; i++)
            if (args[i] == "--config" && args[i + 1].Length > 0) config = args[i + 1];
        // The client starts the server the same way it is running now: this dotnet.exe + this DLL.
        return Registration.Run(args[0] == "--register", args[1], config,
                                Environment.ProcessPath!, typeof(Registration).Assembly.Location);
}

Console.Error.WriteLine("usage: dotnet navis_mcp_server.dll [--version | --register <client> | --unregister <client>] [--config <path>]");
Console.Error.WriteLine("       <client> = claude-desktop | claude-code | codex | all");
return 1;
