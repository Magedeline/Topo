namespace CELMapViewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? map = null, room = null;
#if TOPO_AI
        bool mcp = false, readOnly = false;
        int port = 0;
#endif
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--map" when i + 1 < args.Length: map = args[++i]; break;
                case "--room" when i + 1 < args.Length: room = args[++i]; break;
                case "--selftest": return SelfTest.Run(args.Skip(i + 1).ToArray());
#if TOPO_AI
                case "--mcp": mcp = true; break;
                case "--read-only": readOnly = true; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int pp): port = pp; i++; break;
                case "--mcp-bridge":
                    return Agent.McpServer.RunBridge(i + 1 < args.Length ? args[i + 1] : $"http://localhost:{Agent.McpServer.DefaultPort}/mcp");
#else
                case "--mcp" or "--mcp-bridge":
                    Console.Error.WriteLine("This is the standard Topo edition, which has no AI or MCP features. Use the Topo + AI edition for those.");
                    return 2;
#endif
                default:
                    if (map == null && args[i].EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) map = args[i];
                    break;
            }
        }

#if TOPO_AI
        if (mcp) return RunMcp(map, readOnly, port);
#endif

        ApplicationConfiguration.Initialize();
        Themes.Select(AppSettings.Load().Theme);
        ThemeApplier.Install();
        Application.Run(new MainForm(map, room));
        return 0;
    }

#if TOPO_AI
    /// <summary>Headless MCP server on stdio (or HTTP with <c>--port</c>), for AI clients that launch the viewer themselves.</summary>
    private static int RunMcp(string? map, bool readOnly, int port)
    {
        var host = new Agent.HeadlessHost { ReadOnly = readOnly };
        if (map != null)
        {
            try { host.Open(map); }
            catch (Exception ex) { host.Log($"Could not open {map}: {ex.Message}"); }
        }
        PluginCatalog.StartLoading();
        var server = new Agent.McpServer(new Agent.MapTools(host));
        if (port > 0)
        {
            server.StartHttp(port);
            host.Log($"MCP server listening on {server.Url}");
            Thread.Sleep(Timeout.Infinite);
        }
        server.RunStdio();
        return 0;
    }
#endif
}
