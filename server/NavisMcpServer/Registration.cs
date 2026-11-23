using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace NavisMcpServer;

/// <summary>
/// Adds / removes the "navisworks" server in AI client configurations. The server is started as
/// "&lt;dotnet.exe&gt; &lt;navis_mcp_server.dll&gt;". Only the "navisworks" entry is touched (an older one that ran server_http.py is replaced); a copy
/// "&lt;config&gt;.bak-navisworks" is kept of each file before its first change.
///   claude-desktop  %APPDATA%\Claude\claude_desktop_config.json         (mcpServers.navisworks)
///   codex           %CODEX_HOME% or %USERPROFILE%\.codex\config.toml    ([mcp_servers.navisworks])
///   claude-code     through the "claude" CLI: claude mcp add/remove --scope user navisworks
/// </summary>
internal static class Registration
{
    public const string Name = "navisworks";
    private const int NothingToDo = 3;

    public static int Run(bool register, string client, string? configOverride, string dotnet, string dll)
    {
        string[] clients = client == "all" ? ["claude-desktop", "claude-code", "codex"] : [client];
        int rc = NothingToDo;
        foreach (var c in clients)
        {
            int r;
            try
            {
                r = c switch
                {
                    "claude-desktop" => ClaudeDesktop(register, configOverride ?? ClaudeDesktopPath(), dotnet, dll),
                    "codex" => Codex(register, configOverride ?? CodexPath(), dotnet, dll),
                    "claude-code" => ClaudeCode(register, dotnet, dll),
                    _ => Fail($"unknown client '{c}'; use claude-desktop, claude-code, codex or all"),
                };
            }
            catch (Exception ex) { r = Fail($"{c}: {ex.Message}"); }
            if (r == 1) rc = 1;
            else if (r == 0 && rc != 1) rc = 0;
        }
        return rc;
    }

    public static string ClaudeDesktopPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    public static string CodexPath()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(home, "config.toml");
    }

    // ---------------------------------------------------------------- Claude Desktop (JSON)

    private static int ClaudeDesktop(bool register, string path, string dotnet, string dll)
    {
        JsonObject cfg;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            cfg = string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject
                  ?? throw new InvalidOperationException($"{path} is not a JSON object");
        }
        else
        {
            if (!register) return NothingToDo;
            cfg = [];
        }

        if (cfg["mcpServers"] is not JsonObject servers)
        {
            if (!register) return NothingToDo;
            cfg["mcpServers"] = servers = [];
        }
        if (register)
        {
            var entry = new JsonObject { ["command"] = dotnet, ["args"] = new JsonArray(dll) };
            if (servers[Name] is JsonNode old && JsonNode.DeepEquals(old, entry)) return 0;
            servers[Name] = entry;
        }
        else
        {
            if (!servers.ContainsKey(Name)) return NothingToDo;
            servers.Remove(Name);
        }
        Save(path, cfg.ToJsonString(Json.Indented) + "\n");
        Console.WriteLine($"claude-desktop: {(register ? "registered" : "removed")} in {path}");
        return 0;
    }

    // ---------------------------------------------------------------- Codex (TOML)

    /// <summary>
    /// Rewrites only the [mcp_servers.navisworks] table (and its sub-tables such as
    /// [mcp_servers.navisworks.env]); every other line of config.toml stays as it is.
    /// </summary>
    private static int Codex(bool register, string path, string dotnet, string dll)
    {
        if (!File.Exists(path) && !register) return NothingToDo;
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

        var kept = new List<string>();
        bool inOurs = false, removed = false;
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (t.StartsWith("[["))
                inOurs = false;
            else if (t.StartsWith('['))
            {
                string header = t.TrimStart('[').Split(']')[0].Trim().Replace("\"", "");
                inOurs = header == $"mcp_servers.{Name}" || header.StartsWith($"mcp_servers.{Name}.");
            }
            if (inOurs) { removed = true; continue; }
            kept.Add(line);
        }
        if (!register && !removed) return NothingToDo;

        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        if (register)
        {
            if (dotnet.Contains('\'') || dll.Contains('\''))
                throw new InvalidOperationException("a path contains an apostrophe; TOML literal strings cannot hold it");
            if (kept.Count > 0) kept.Add("");
            kept.Add($"[mcp_servers.{Name}]");
            kept.Add($"command = '{dotnet}'");   // literal strings: backslashes need no escaping
            kept.Add($"args = ['{dll}']");
        }
        Save(path, string.Join("\n", kept) + "\n");
        Console.WriteLine($"codex: {(register ? "registered" : "removed")} in {path}");
        return 0;
    }

    // ---------------------------------------------------------------- Claude Code (CLI)

    private static int ClaudeCode(bool register, string dotnet, string dll)
    {
        // Remove first: "add" fails when the name exists; "not found" is fine.
        var (removeRc, _) = RunClaude($"mcp remove --scope user {Name}");
        if (removeRc == -1) return register ? Fail("claude-code: the 'claude' command was not found") : NothingToDo;
        if (!register)
        {
            if (removeRc != 0) return NothingToDo;
            Console.WriteLine("claude-code: removed (user scope)");
            return 0;
        }
        var (rc, output) = RunClaude($"mcp add --scope user {Name} -- \"{dotnet}\" \"{dll}\"");
        if (rc != 0) return Fail($"claude-code: 'claude mcp add' failed: {output.Trim()}");
        Console.WriteLine("claude-code: registered (user scope)");
        return 0;
    }

    /// <summary>Runs "claude ..." through cmd so that claude.cmd / claude.exe on PATH both work. -1 = not found.</summary>
    private static (int rc, string output) RunClaude(string arguments)
    {
        using (var where = Process.Start(new ProcessStartInfo("where.exe", "claude")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!)
        {
            where.WaitForExit(10_000);
            if (where.ExitCode != 0) return (-1, "");
        }

        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"claude {arguments}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (1, "timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    // ---------------------------------------------------------------- helpers

    private static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".bak-navisworks";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
