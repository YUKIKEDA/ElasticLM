using ElasticLM.Server;

// elm-server [--config <settings.json>]: the on-premises license server (design §13). Exit codes: 0 after a normal stop,
// 1 when the server cannot start, 2 for a usage or settings error.
string? configPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: elm-server [--config <settings.json>]");
            Console.WriteLine("Settings: design §13.8; each key can also be set as ELM_<KEY>, for example ELM_PRODUCT.");
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown argument '{args[i]}'. Usage: elm-server [--config <settings.json>]");
            return 2;
    }
}

ServerSettings settings;
try
{
    settings = ServerSettings.Load(configPath, Environment.GetEnvironmentVariable);
}
catch (ServerStartException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}

ElmServer server;
try
{
    server = await ElmServer.StartAsync(settings, new ServerHostOptions());
}
catch (ServerStartException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

await using (server)
{
    await server.WaitForShutdownAsync();
}

return 0;
