using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <content>
/// The lease functions of libelasticlm (design §17.6): <c>elm_client</c> over <see cref="LicenseClient"/> and
/// <c>elm_lease</c> over <see cref="Lease"/>.
/// </content>
public static unsafe partial class Exports
{
    // design §17.6: 36,600 days, the bound of design §8.6 that the server enforces. Far larger values would also overflow
    // the conversion to TimeSpan, which would become ELM_INTERNAL_ERROR.
    private const long MaxBorrowSeconds = 3_162_240_000;

    /// <summary><c>elm_client_create</c>: makes a lease client for this installation. It does not contact the server.</summary>
    /// <param name="options">The options.</param>
    /// <param name="result">Receives the new handle, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_create")]
    public static int ClientCreate(nint options, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            var o = (ClientOptions*)options;
            StructSizes.Check(o, "options");
            string product = NativeStrings.Read(o->ProductId, "product_id");
            if (!Uri.TryCreate(NativeStrings.Read(o->ServerUrl, "server_url"), UriKind.Absolute, out Uri? server))
            {
                throw NativeException.Invalid("server_url is not an absolute URL.");
            }

            string[] roots = ReadArray(o->RootKeysPem, o->RootKeyCount, 1, MaxRootKeys, "root_keys_pem", NativeStrings.MaxPemBytes);
            string[] revoked = ReadArray(o->RevokedKeyIds, o->RevokedKeyCount, 0, MaxRevokedKeyIds, "revoked_key_ids", NativeStrings.MaxTextBytes);
            KeyScope scope = Scope(o->Scope);
            int skew = o->ClockSkewSeconds == 0 ? DefaultClockSkewSeconds : o->ClockSkewSeconds;
            if (skew is < 1 or > MaxClockSkewSeconds)
            {
                throw NativeException.Invalid("clock_skew_seconds must be 0 (the default) or 1 to 3600.");
            }

            var clientOptions = new LicenseClientOptions
            {
                ProductId = product,
                ServerUrl = server,
                LicenseKey = NativeStrings.ReadOptional(o->LicenseKey, "license_key"),
                Trust = TrustStore.Create(roots, revoked),
                ExpectedServerLicenseId = NativeStrings.ReadOptional(o->ExpectedServerLicenseId, "expected_server_license_id"),
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
            };
            if (!LicenseClient.TryCreate(clientOptions, out LicenseClient? client, out LicenseError? error))
            {
                throw new NativeException(error!.Code, error.Message);
            }

            // A new client holds no lease, so no event can come before the handler is attached.
            var handle = new NativeClient(o->OnLease, o->User) { Client = client };
            client!.LeaseStateChanged += handle.OnLeaseChanged;
            *result = NativeHandles.Allocate(handle);
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_client_acquire_lease</c>: acquires a lease and starts renewing it.</summary>
    /// <param name="client">The handle.</param>
    /// <param name="featureId">The feature.</param>
    /// <param name="featureVersion">The app's version of the feature, or <see langword="null"/>.</param>
    /// <param name="count">The number of seats, at least 1.</param>
    /// <param name="result">Receives the lease, or <see langword="null"/> on a failure.</param>
    /// <param name="retryAfterSeconds">Receives when to try again, in seconds, or 0; may be <see langword="null"/>.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_acquire_lease")]
    public static int ClientAcquireLease(nint client, byte* featureId, byte* featureVersion, int count, nint* result, long* retryAfterSeconds) =>
        Leased(
            client,
            result,
            retryAfterSeconds,
            c =>
                c.AcquireLeaseAsync(NativeStrings.Read(featureId, "feature_id"), NativeStrings.ReadOptional(featureVersion, "feature_version"), count)
                    .GetAwaiter()
                    .GetResult()
        );

    /// <summary><c>elm_client_borrow_lease</c>: borrows a lease for a period.</summary>
    /// <param name="client">The handle.</param>
    /// <param name="featureId">The feature.</param>
    /// <param name="periodSeconds">The borrow period in seconds, 1 to 3,162,240,000.</param>
    /// <param name="featureVersion">The app's version of the feature, or <see langword="null"/>.</param>
    /// <param name="count">The number of seats, at least 1.</param>
    /// <param name="result">Receives the lease, or <see langword="null"/> on a failure.</param>
    /// <param name="retryAfterSeconds">Receives when to try again, in seconds, or 0; may be <see langword="null"/>.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_borrow_lease")]
    public static int ClientBorrowLease(
        nint client,
        byte* featureId,
        long periodSeconds,
        byte* featureVersion,
        int count,
        nint* result,
        long* retryAfterSeconds
    ) =>
        Leased(
            client,
            result,
            retryAfterSeconds,
            c =>
            {
                if (periodSeconds is < 1 or > MaxBorrowSeconds)
                {
                    throw NativeException.Invalid("period_seconds must be 1 to 3162240000.");
                }

                return c.BorrowLeaseAsync(
                        NativeStrings.Read(featureId, "feature_id"),
                        TimeSpan.FromSeconds(periodSeconds),
                        NativeStrings.ReadOptional(featureVersion, "feature_version"),
                        count
                    )
                    .GetAwaiter()
                    .GetResult();
            }
        );

    /// <summary><c>elm_client_release_lease</c>: returns a lease this client made and stops renewing it.</summary>
    /// <param name="client">The handle.</param>
    /// <param name="lease">The lease.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_release_lease")]
    public static int ClientReleaseLease(nint client, nint lease)
    {
        NativeErrors.Clear();
        try
        {
            LicenseClient c = LeaseClientOf(client, blocking: true);
            Lease target = NativeHandles.Get<NativeLease>(lease, "lease").Lease;
            return c.ReleaseLeaseAsync(target).GetAwaiter().GetResult() is LicenseError error ? NativeErrors.Fail(error.Code, error.Message) : 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_client_get_borrowed_leases</c>: reads the saved borrows that are still valid.</summary>
    /// <param name="client">The handle.</param>
    /// <param name="result">Receives the array of new lease handles, or <see langword="null"/> when there are none.</param>
    /// <param name="count">Receives the number of leases.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_get_borrowed_leases")]
    public static int ClientGetBorrowedLeases(nint client, nint* result, nuint* count)
    {
        NativeErrors.Clear();
        if (result is null || count is null)
        {
            ClearOutputs(result, count);
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out and count are required.");
        }

        *result = 0;
        *count = 0;
        try
        {
            IReadOnlyList<Lease> borrowed = LeaseClientOf(client, blocking: true).GetBorrowedLeases();
            if (borrowed.Count == 0)
            {
                return 0;
            }

            var list = (nint*)NativeMemory.Alloc((nuint)borrowed.Count, (nuint)sizeof(nint));
            for (int i = 0; i < borrowed.Count; i++)
            {
                list[i] = NativeHandles.Allocate(new NativeLease(borrowed[i]));
            }

            *result = (nint)list;
            *count = (nuint)borrowed.Count;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_client_reset_server_pin</c>: deletes the pin of a server (design §13.5).</summary>
    /// <param name="client">The handle.</param>
    /// <param name="serverUrl">The server URL, an absolute http or https URL.</param>
    /// <param name="deleted">Receives 1 when a pin was deleted, otherwise 0.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_reset_server_pin")]
    public static int ClientResetServerPin(nint client, byte* serverUrl, int* deleted)
    {
        NativeErrors.Clear();
        if (deleted is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "deleted is required.");
        }

        *deleted = 0;
        try
        {
            LicenseClient c = LeaseClientOf(client, blocking: false);

            // design §17.6: checked here, so a hosted client, which answers false without looking, refuses it too.
            if (
                !Uri.TryCreate(NativeStrings.Read(serverUrl, "server_url"), UriKind.Absolute, out Uri? url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            )
            {
                throw NativeException.Invalid("server_url is not an absolute http or https URL.");
            }

            *deleted = c.ResetServerPin(url) ? 1 : 0;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_lease_id</c>: the lease ID, valid until <c>elm_lease_free</c>.</summary>
    /// <param name="lease">The handle.</param>
    /// <param name="result">Receives the ID.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_id")]
    public static int LeaseId(nint lease, nint* result) => LeaseText(lease, result, l => l.Id);

    /// <summary><c>elm_lease_feature_id</c>: the feature, valid until <c>elm_lease_free</c>.</summary>
    /// <param name="lease">The handle.</param>
    /// <param name="result">Receives the feature ID.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_feature_id")]
    public static int LeaseFeatureId(nint lease, nint* result) => LeaseText(lease, result, l => l.FeatureId);

    /// <summary><c>elm_lease_expires_at</c>: the expiry of the current token, in UNIX seconds.</summary>
    /// <param name="lease">The handle.</param>
    /// <param name="result">Receives the expiry.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_expires_at")]
    public static int LeaseExpiresAt(nint lease, long* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            *result = NativeHandles.Get<NativeLease>(lease, "lease").Lease.ExpiresAt.ToUnixTimeSeconds();
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_lease_seats</c>: the number of seats.</summary>
    /// <param name="lease">The handle.</param>
    /// <returns>The seats, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_seats")]
    public static int LeaseSeats(nint lease) => Guard(() => NativeHandles.Find<NativeLease>(lease) is NativeLease l ? l.Lease.Seats : -1, -1);

    /// <summary><c>elm_lease_is_borrow</c>: whether the lease is a borrow.</summary>
    /// <param name="lease">The handle.</param>
    /// <returns>1 for a borrow, 0 otherwise, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_is_borrow")]
    public static int LeaseIsBorrow(nint lease) =>
        Guard(() => NativeHandles.Find<NativeLease>(lease) is NativeLease l ? (l.Lease.IsBorrow ? 1 : 0) : -1, -1);

    /// <summary><c>elm_lease_state</c>: the state of the lease.</summary>
    /// <param name="lease">The handle.</param>
    /// <returns>An <c>ELM_LEASE_*</c> value, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_state")]
    public static int LeaseStateOf(nint lease) => Guard(() => NativeHandles.Find<NativeLease>(lease) is NativeLease l ? (int)l.Lease.State : -1, -1);

    /// <summary><c>elm_lease_last_error</c>: the number of the last failed renewal, reclaim, or release.</summary>
    /// <param name="lease">The handle.</param>
    /// <returns>A design §19 number, 0 when none failed, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_last_error")]
    public static int LeaseLastError(nint lease) =>
        Guard(() => NativeHandles.Find<NativeLease>(lease) is NativeLease l ? (int)(l.Lease.LastError?.Code ?? ErrorCode.Ok) : -1, -1);

    /// <summary><c>elm_lease_free</c>: frees a lease handle; it does not return the lease. Null is ignored.</summary>
    /// <param name="lease">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_free")]
    public static void LeaseFree(nint lease) => Guard(() => NativeHandles.Free<NativeLease>(lease)?.Dispose());

    /// <summary><c>elm_lease_list_free</c>: frees the handles of <c>elm_client_get_borrowed_leases</c> and the array.</summary>
    /// <param name="leases">The array, or <see langword="null"/>.</param>
    /// <param name="count">The number of handles in it.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_lease_list_free")]
    public static void LeaseListFree(nint* leases, nuint count) =>
        Guard(() =>
        {
            if (leases is null)
            {
                return;
            }

            for (nuint i = 0; i < count; i++)
            {
                NativeHandles.Free<NativeLease>(leases[i])?.Dispose();
            }

            NativeMemory.Free(leases);
        });

    /// <summary>
    /// <c>elm_client_destroy</c>: waits for running callbacks, then disposes the client, which returns its leases. Null is
    /// ignored.
    /// </summary>
    /// <param name="client">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_client_destroy")]
    public static void ClientDestroy(nint client) =>
        Guard(() =>
        {
            // design §17.6: from inside its own callback, destroy does nothing; the message stays as it is.
            if (NativeHandles.Find<NativeClient>(client) is not NativeClient handle || handle.InOwnCallback)
            {
                return;
            }

            // The handle is freed only after the running callbacks have returned.
            handle.Close();
            NativeHandles.Free<NativeClient>(client);
        });

    // design §17.6: inside its own callback, the blocking calls of a handle are refused.
    private static LicenseClient LeaseClientOf(nint client, bool blocking)
    {
        NativeClient handle = NativeHandles.Get<NativeClient>(client, "client");
        if (blocking && handle.InOwnCallback)
        {
            throw new NativeException(ErrorCode.InvalidState, "This call cannot be made inside the lease callback of the same client.");
        }

        return handle.Client!;
    }

    private static int Leased(nint client, nint* result, long* retryAfterSeconds, Func<LicenseClient, LeaseResult> run)
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
            switch (run(LeaseClientOf(client, blocking: true)))
            {
                case LeaseResult.Acquired acquired:
                    *result = NativeHandles.Allocate(new NativeLease(acquired.Lease));
                    return 0;
                case LeaseResult.Failed failed:
                    if (retryAfterSeconds is not null && failed.RetryAfter is TimeSpan wait)
                    {
                        *retryAfterSeconds = (long)Math.Ceiling(wait.TotalSeconds);
                    }

                    return NativeErrors.Fail(failed.Error.Code, failed.Error.Message);
                default:
                    throw new InvalidOperationException("The lease client returned an unknown result.");
            }
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    private static int LeaseText(nint lease, nint* result, Func<NativeLease, nint> read)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            *result = read(NativeHandles.Get<NativeLease>(lease, "lease"));
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }
}
