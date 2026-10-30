using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <content>
/// The activation functions of libelasticlm (design §17.5): <c>elm_activation</c> over <see cref="ActivationClient"/>.
/// </content>
public static unsafe partial class Exports
{
    private const int MaxResponseBytes = 256 * 1024;

    /// <summary><c>elm_activation_create</c>: makes an activation client for this installation.</summary>
    /// <param name="options">The options.</param>
    /// <param name="result">Receives the new handle, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_create")]
    public static int ActivationCreate(nint options, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            var o = (ActivationOptions*)options;
            StructSizes.Check(o, "options");
            string product = NativeStrings.Read(o->ProductId, "product_id");
            string? url = NativeStrings.ReadOptional(o->ServiceUrl, "service_url");
            Uri? service = null;
            if (url is not null && !Uri.TryCreate(url, UriKind.Absolute, out service))
            {
                throw NativeException.Invalid("service_url is not an absolute URL.");
            }

            string[] roots = ReadArray(o->RootKeysPem, o->RootKeyCount, 1, MaxRootKeys, "root_keys_pem", NativeStrings.MaxPemBytes);
            string[] revoked = ReadArray(o->RevokedKeyIds, o->RevokedKeyCount, 0, MaxRevokedKeyIds, "revoked_key_ids", NativeStrings.MaxTextBytes);
            KeyScope scope = Scope(o->Scope);
            int skew = o->ClockSkewSeconds == 0 ? DefaultClockSkewSeconds : o->ClockSkewSeconds;
            if (skew is < 1 or > MaxClockSkewSeconds)
            {
                throw NativeException.Invalid("clock_skew_seconds must be 0 (the default) or 1 to 3600.");
            }

            var handle = new NativeActivation(o->OnState, o->User);
            var clientOptions = new ActivationClientOptions
            {
                ProductId = product,
                ServiceUrl = service,
                Trust = TrustStore.Create(roots, revoked),
                KeyScope = scope,
                DataDirectory = NativeStrings.ReadOptional(o->Directory, "directory"),
                SendClientInfo = Flag(o->SendClientInfo, "send_client_info"),
                AppVersion = NativeStrings.ReadOptional(o->AppVersion, "app_version"),
                VerifierOptions = new LicenseVerifierOptions
                {
                    ClockSkew = TimeSpan.FromSeconds(skew),
                    AcceptTrialAsStandard = !Flag(o->RejectTrialAsStandard, "reject_trial_as_standard"),
                },
                TimeProvider = Time,
                StateChangedFromStart = handle.OnStateChanged,
            };
            if (!ActivationClient.TryCreate(clientOptions, out ActivationClient? client, out LicenseError? error))
            {
                throw new NativeException(error!.Code, error.Message);
            }

            handle.Client = client;
            *result = NativeHandles.Allocate(handle);
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_activation_activate</c>: activates this installation online with a license key.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="licenseKey">The license key.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <param name="retryAfterSeconds">Receives when to try again, in seconds, or 0; may be <see langword="null"/>.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_activate")]
    public static int ActivationActivate(nint activation, byte* licenseKey, nint* result, long* retryAfterSeconds) =>
        Online(activation, result, retryAfterSeconds, a => a.ActivateAsync(NativeStrings.Read(licenseKey, "license_key")).GetAwaiter().GetResult());

    /// <summary><c>elm_activation_start_trial</c>: starts a trial on this installation.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <param name="retryAfterSeconds">Receives when to try again, in seconds, or 0; may be <see langword="null"/>.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_start_trial")]
    public static int ActivationStartTrial(nint activation, nint* result, long* retryAfterSeconds) =>
        Online(activation, result, retryAfterSeconds, a => a.StartTrialAsync().GetAwaiter().GetResult());

    /// <summary><c>elm_activation_refresh</c>: refreshes the kept activation online.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <param name="retryAfterSeconds">Receives when to try again, in seconds, or 0; may be <see langword="null"/>.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_refresh")]
    public static int ActivationRefresh(nint activation, nint* result, long* retryAfterSeconds) =>
        Online(activation, result, retryAfterSeconds, a => a.RefreshAsync().GetAwaiter().GetResult());

    /// <summary><c>elm_activation_import_response</c>: keeps the service's answer to the waiting offline request.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="response">The answer's bytes, UTF-8.</param>
    /// <param name="length">The number of bytes.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_import_response")]
    public static int ActivationImportResponse(nint activation, byte* response, nuint length, nint* result) =>
        Online(
            activation,
            result,
            null,
            a =>
            {
                if (response is null)
                {
                    throw NativeException.Invalid("response is required.");
                }

                // design §17.5: the bound of .NET, before any byte is read (C counts the surrounding white space too).
                if (length > MaxResponseBytes)
                {
                    throw new NativeException(ErrorCode.Malformed, "The text is not an answer of the service.");
                }

                return a.ImportResponseAsync(NativeStrings.ReadBytes(response, (int)length, "response")).GetAwaiter().GetResult();
            }
        );

    /// <summary><c>elm_activation_deactivate</c>: deactivates this installation online.</summary>
    /// <param name="activation">The handle.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_deactivate")]
    public static int ActivationDeactivate(nint activation) => Plain(activation, a => a.DeactivateAsync().GetAwaiter().GetResult());

    /// <summary><c>elm_activation_forget_deactivation_proof</c>: forgets the kept deactivation proof.</summary>
    /// <param name="activation">The handle.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_forget_deactivation_proof")]
    public static int ActivationForgetDeactivationProof(nint activation) =>
        Plain(activation, a => a.ForgetDeactivationProofAsync().GetAwaiter().GetResult());

    /// <summary><c>elm_activation_check</c>: checks the kept activation offline.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="featureId">The required feature, or <see langword="null"/>.</param>
    /// <param name="featureVersion">The required version, or <see langword="null"/>.</param>
    /// <param name="kind">An <c>ELM_KIND_*</c> value.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 when the kept license holds.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_check")]
    public static int ActivationCheck(nint activation, byte* featureId, byte* featureVersion, int kind, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            ActivationClient client = Client(activation, blocking: false);
            LicenseVerificationResult verified = client.Check(
                NativeStrings.ReadOptional(featureId, "feature_id"),
                NativeStrings.ReadOptional(featureVersion, "feature_version"),
                Kind(kind)
            );
            return Keep(verified, result);
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_activation_create_activation_request</c>: makes an offline activation request file.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="licenseKey">The license key.</param>
    /// <param name="renew">1 to make a new request before the waiting one expires.</param>
    /// <param name="result">Receives the file's text, freed with <c>elm_string_free</c>.</param>
    /// <param name="replaced">Receives 1 when a waiting request was replaced.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_create_activation_request")]
    public static int ActivationCreateActivationRequest(nint activation, byte* licenseKey, int renew, nint* result, int* replaced) =>
        Offline(
            activation,
            result,
            replaced,
            a => a.CreateActivationRequestAsync(NativeStrings.Read(licenseKey, "license_key"), Flag(renew, "renew")).GetAwaiter().GetResult()
        );

    /// <summary><c>elm_activation_create_refresh_request</c>: makes an offline refresh request file.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="renew">1 to make a new request before the waiting one expires.</param>
    /// <param name="result">Receives the file's text, freed with <c>elm_string_free</c>.</param>
    /// <param name="replaced">Receives 1 when a waiting request was replaced.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_create_refresh_request")]
    public static int ActivationCreateRefreshRequest(nint activation, int renew, nint* result, int* replaced) =>
        Offline(activation, result, replaced, a => a.CreateRefreshRequestAsync(Flag(renew, "renew")).GetAwaiter().GetResult());

    /// <summary><c>elm_activation_create_deactivation_proof</c>: makes an offline deactivation proof.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="result">Receives the proof's text, freed with <c>elm_string_free</c>.</param>
    /// <param name="replaced">Receives 1 when a kept proof was replaced.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_create_deactivation_proof")]
    public static int ActivationCreateDeactivationProof(nint activation, nint* result, int* replaced) =>
        Offline(activation, result, replaced, a => a.CreateDeactivationProofAsync().GetAwaiter().GetResult());

    /// <summary><c>elm_activation_id</c>: copies the current activation ID into the caller's buffer.</summary>
    /// <param name="activation">The handle.</param>
    /// <param name="buffer">The buffer, or <see langword="null"/> when <paramref name="size"/> is 0.</param>
    /// <param name="size">The buffer's size in bytes.</param>
    /// <param name="needed">Receives the size needed, with the NUL.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_id")]
    public static int ActivationId(nint activation, byte* buffer, nuint size, nuint* needed)
    {
        NativeErrors.Clear();
        if (needed is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "needed is required.");
        }

        *needed = 0;
        try
        {
            ActivationClient client = Client(activation, blocking: false);
            byte[] id = System.Text.Encoding.UTF8.GetBytes(client.ActivationId ?? string.Empty);
            *needed = (nuint)id.Length + 1;
            if (size < *needed || buffer is null)
            {
                throw NativeException.Invalid($"The buffer must hold {*needed} bytes.");
            }

            id.CopyTo(new Span<byte>(buffer, id.Length));
            buffer[id.Length] = 0;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_activation_state</c>: the state of the activation.</summary>
    /// <param name="activation">The handle.</param>
    /// <returns>An <c>ELM_STATE_*</c> value, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_state")]
    public static int ActivationState(nint activation) =>
        Guard(() => NativeHandles.Find<NativeActivation>(activation)?.Client is ActivationClient c ? (int)c.State : -1, -1);

    /// <summary><c>elm_activation_destroy</c>: waits for running callbacks, then disposes the client. Null is ignored.</summary>
    /// <param name="activation">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_activation_destroy")]
    public static void ActivationDestroy(nint activation) =>
        Guard(() =>
        {
            // design §17.5: from inside its own callback, destroy does nothing; the message stays as it is.
            if (NativeHandles.Find<NativeActivation>(activation) is not NativeActivation handle || handle.InOwnCallback)
            {
                return;
            }

            // The handle is freed only after the running callbacks have returned: they may still call check, ID, and state.
            handle.Close();
            NativeHandles.Free<NativeActivation>(activation);
        });

    /// <summary><c>elm_string_free</c>: zeroes and frees a string the library returned for this function. Null is ignored.</summary>
    /// <param name="text">The string.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_string_free")]
    public static void StringFree(byte* text) =>
        Guard(() =>
        {
            if (text is null)
            {
                return;
            }

            // design §17.5: a request file holds the license key; the copy is cleared before it is freed.
            // The length comes from the runtime's strlen, which does not read past the NUL into an unmapped page.
            int length = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text).Length;
            new Span<byte>(text, length).Clear();
            NativeStrings.Free((nint)text);
        });

    private static KeyScope Scope(int value) =>
        value switch
        {
            0 => KeyScope.User,
            1 => KeyScope.Machine,
            _ => throw NativeException.Invalid("scope must be ELM_SCOPE_USER or ELM_SCOPE_MACHINE."),
        };

    private static LicenseKind Kind(int value) =>
        value switch
        {
            0 => LicenseKind.Standard,
            1 => LicenseKind.Trial,
            2 => LicenseKind.Server,
            _ => throw NativeException.Invalid("kind must be ELM_KIND_STANDARD, ELM_KIND_TRIAL, or ELM_KIND_SERVER."),
        };

    // design §17.5: inside its own callback, the blocking calls of a handle are refused.
    private static ActivationClient Client(nint activation, bool blocking)
    {
        NativeActivation handle = NativeHandles.Get<NativeActivation>(activation, "activation");
        if (blocking && handle.InOwnCallback)
        {
            throw new NativeException(ErrorCode.InvalidState, "This call cannot be made inside the state callback of the same activation.");
        }

        return handle.Client!;
    }

    private static int Online(nint activation, nint* result, long* retryAfterSeconds, Func<ActivationClient, ActivationResult> run)
    {
        NativeErrors.Clear();
        if (retryAfterSeconds is not null)
        {
            *retryAfterSeconds = 0;
        }

        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            switch (run(Client(activation, blocking: true)))
            {
                case ActivationResult.Activated activated:
                    *result = NativeHandles.Allocate(new NativeLicense(activated.License, activated.InGracePeriod));
                    return 0;
                case ActivationResult.Failed failed:
                    if (retryAfterSeconds is not null && failed.RetryAfter is TimeSpan wait)
                    {
                        *retryAfterSeconds = (long)Math.Ceiling(wait.TotalSeconds);
                    }

                    return NativeErrors.Fail(failed.Error.Code, failed.Error.Message);
                default:
                    throw new InvalidOperationException("The activation client returned an unknown result.");
            }
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    private static int Plain(nint activation, Func<ActivationClient, LicenseError?> run)
    {
        NativeErrors.Clear();
        try
        {
            return run(Client(activation, blocking: true)) is LicenseError error ? NativeErrors.Fail(error.Code, error.Message) : 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    private static int Offline(nint activation, nint* result, int* replaced, Func<ActivationClient, OfflineRequestResult> run)
    {
        NativeErrors.Clear();
        if (result is null || replaced is null)
        {
            if (result is not null)
            {
                *result = 0;
            }

            if (replaced is not null)
            {
                *replaced = 0;
            }

            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out and replaced are required.");
        }

        *result = 0;
        *replaced = 0;
        try
        {
            switch (run(Client(activation, blocking: true)))
            {
                case OfflineRequestResult.Created created:
                    *result = NativeStrings.Allocate(created.File).Pointer;
                    *replaced = created.Replaced ? 1 : 0;
                    return 0;
                case OfflineRequestResult.Failed failed:
                    return NativeErrors.Fail(failed.Error.Code, failed.Error.Message);
                default:
                    throw new InvalidOperationException("The activation client returned an unknown result.");
            }
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    private static int Keep(LicenseVerificationResult verified, nint* result)
    {
        switch (verified)
        {
            case LicenseVerificationResult.Valid valid:
                *result = NativeHandles.Allocate(new NativeLicense(valid.License, valid.InGracePeriod));
                return 0;
            case LicenseVerificationResult.Invalid invalid:
                return NativeErrors.Fail(invalid.Error.Code, invalid.Error.Message);
            default:
                throw new InvalidOperationException("The verifier returned an unknown result.");
        }
    }
}
