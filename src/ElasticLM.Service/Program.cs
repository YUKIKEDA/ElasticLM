using ElasticLM.Service;

// elm-service [setup | backup --out <file>] [--config <file>]
// Exit codes: 0 stopped normally, or setup or backup succeeded; 1 cannot start, the database failed, or the backup file
// failed; 2 the arguments or settings are not valid.
const string Usage = "Usage: elm-service [setup | backup --out <file>] [--config <file>]";
string? configPath = null;
string? backupPath = null;
bool setup = false;
bool backup = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "setup" when i == 0:
            setup = true;
            break;
        case "backup" when i == 0:
            backup = true;
            break;
        case "--config" when i + 1 < args.Length && !(backup && configPath is not null):
            configPath = args[++i];
            break;
        case "--out" when backup && backupPath is null && i + 1 < args.Length:
            backupPath = args[++i];
            break;
        default:
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
    }
}

if (backup && backupPath is null)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

if (backup && string.IsNullOrWhiteSpace(backupPath))
{
    // design §14.2: an empty path is named, not left to the file system.
    await Console.Error.WriteLineAsync("The backup file path is empty.").ConfigureAwait(false);
    return 2;
}

ServiceSettings settings;
try
{
    settings = ServiceSettings.Load(configPath, Environment.GetEnvironmentVariable, requireSigning: !(setup || backup));
}
catch (ServiceStartException e)
{
    await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
    return 2;
}

if (backup)
{
    return await Backup.RunAsync(settings, backupPath!, Console.Out, Console.Error).ConfigureAwait(false);
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
