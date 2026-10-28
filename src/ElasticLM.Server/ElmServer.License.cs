using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Server;

/// <content>
/// The server license kept up to date (design §13.7, M4c3): with <c>service.url</c>, an <see cref="ActivationClient"/> in
/// the server's data folder refreshes the activation that <c>elm server activate</c> made, writes each new file to the license
/// path, and reloads it.
/// </content>
internal sealed partial class ElmServer
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private ActivationClient? _activation;

    // Whether the server refreshes its license on its own: a service URL and a kept activation (the status, design §13.7).
    private bool AutoRefresh => _activation?.ActivationId is not null;

    // design §13.8: with service.url the license path must be writable, or every refresh would fail; known at the start.
    private static void EnsureWritable(string licensePath)
    {
        string probe = Path.GetFullPath(licensePath) + ".tmp";
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServerStartException($"service.url is set, and the server license path '{licensePath}' cannot be written: {e.Message}");
        }
    }

    // Creates the server's client, or makes it again after a reload so it reads the kept activation and schedules its refresh.
    // The old one is closed first, which cancels a refresh it runs.
    private async Task RestartActivationAsync()
    {
        if (_settings.ServiceUrl is not Uri service)
        {
            return;
        }

        await _activationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activation is not null)
            {
                await _activation.DisposeAsync().ConfigureAwait(false);
                _activation = null;
            }

            if (_stopped)
            {
                return;
            }

            var options = new ActivationClientOptions
            {
                ProductId = _settings.Product,
                ServiceUrl = service,
                Trust = _trust,
                KeyScope = KeyScope.Machine,
                DataDirectory = _settings.DataDirectory,
                TimeProvider = Time,
                ClockGuard = _guard,
                RequiredKind = LicenseKind.Server,
                LicenseKept = OnLicenseKept,
            };
            if (!ActivationClient.TryCreate(options, out ActivationClient? client, out LicenseError? error))
            {
                Failed("start", error!.Code, error.Message);
                return;
            }

            client!.StateChanged += OnActivationStateChanged;
            _activation = client;
        }
        finally
        {
            _activationGate.Release();
        }

        SyncKept();
    }

    private async Task StopActivationAsync()
    {
        await _activationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activation is not null)
            {
                await _activation.DisposeAsync().ConfigureAwait(false);
                _activation = null;
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }

    // A refreshed file: written to the license path, then reloaded with the checks of the start (design §13.1). If either
    // fails, the old license stays, and the hourly sync tries again.
    private void OnLicenseKept(byte[] file)
    {
        try
        {
            LicenseFiles.Write(_settings.LicensePath, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed("write", ErrorCode.InternalError, "The server license cannot be written: " + e.Message);
            return;
        }

        if (ReloadLicense() is LicenseError error)
        {
            Failed("reload", error.Code, error.Message);
            return;
        }

        VerifiedLicense license = Engine.Server.License;
        _usage.Write(
            "license.refreshed",
            w =>
            {
                w.WriteString("lic", license.LicenseId);
                w.WriteString("token", license.TokenId);
                w.WriteNumber("seq", license.Sequence ?? 0);
            }
        );
    }

    // design §13.7: the kept activation's file, when it is of the license in use with a higher sequence number, is written
    // and reloaded: at the start, and every hour after a write or a reload failed.
    private void SyncKept()
    {
        if (_activation is null || new ActivationStore(_store.Directory, Time).Read(out _)?.License is not byte[] kept)
        {
            return;
        }

        VerifiedLicense current = Engine.Server.License;
        var verifier = new LicenseVerifier(_trust, Time, StatelessClockGuard.Instance);
        var requirement = new LicenseRequirement(
            _settings.Product,
            LicenseKind.Server,
            null,
            null,
            _key.Identity(MachineFingerprint.Compute(_settings.Product))
        );
        if (
            verifier.Verify(kept, requirement) is LicenseVerificationResult.Valid valid
            && valid.License.LicenseId == current.LicenseId
            && (valid.License.Sequence ?? 0) > (current.Sequence ?? 0)
        )
        {
            OnLicenseKept(kept);
        }
    }

    private void OnActivationStateChanged(object? sender, ActivationStateChangedEventArgs change)
    {
        if (change.Current is ActivationState.RefreshFailing or ActivationState.Refused or ActivationState.CloneDetected or ActivationState.None)
        {
            ErrorCode code = change.Error?.Code ?? ErrorCode.ActivationNotFound;
            Failed("service", code, change.Error?.Message ?? change.Current.ToString());
        }
    }

    private void Failed(string stage, ErrorCode code, string reason)
    {
        string wire = ErrorCodes.ToWireName(code);
        _usage.Write(
            "license.refresh_failed",
            w =>
            {
                w.WriteString("stage", stage);
                w.WriteString("code", wire);
            }
        );
        ServerLog.LicenseRefreshFailed(_log, wire, reason);
    }
}
