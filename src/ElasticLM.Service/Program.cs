using ElasticLM.Service;

// elm-service [setup] [--config <file>]
// Exit codes: 0 stopped normally or setup succeeded, 1 cannot start or the database failed, 2 the arguments or settings are not valid.
string? configPath = null;
bool setup = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "setup" when i == 0:
            setup = true;
            break;
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        default:
            await Console.Error.WriteLineAsync("Usage: elm-service [setup] [--config <file>]").ConfigureAwait(false);
            return 2;
    }
}

ServiceSettings settings;
try
{
    settings = ServiceSettings.Load(configPath, Environment.GetEnvironmentVariable);
}
catch (ServiceStartException e)
{
    await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
    return 2;
}

if (setup)
{
    return await Setup.RunAsync(settings, TimeProvider.System, Console.Out, Console.Error).ConfigureAwait(false);
}

ElmService service;
try
{
    service = await ElmService.StartAsync(settings, new ServiceHostOptions()).ConfigureAwait(false);
}
catch (ServiceStartException e)
{
    await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
    return 1;
}

await using (service.ConfigureAwait(false))
{
    await service.WaitForShutdownAsync().ConfigureAwait(false);
}

return 0;
