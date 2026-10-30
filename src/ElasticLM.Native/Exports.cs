using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary>
/// The functions <c>libelasticlm</c> exports (design §17, §17.4; <c>include/elasticlm.h</c>). Functions that return a
/// number empty the thread's message first, write it on a failure, set their outputs to <see langword="null"/> and 0 on
/// every failure, and never let an exception leave them.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "design §17.3: no exception may leave an exported function; each becomes ELM_INTERNAL_ERROR."
)]
public static unsafe class Exports
{
    private const int MaxRootKeys = 16;
    private const int MaxRevokedKeyIds = 1024;
    private const int DefaultClockSkewSeconds = 300;
    private const int MaxClockSkewSeconds = 3600;
    private const int MaxLicenseFileBytes = 64 * 1024;

    private static readonly nint Version = NativeStrings.Allocate(NativeVersion.Text).Pointer;

    /// <summary>Gets or sets the clock of the verifiers made from now on. Only the tests replace it (design §17.4).</summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary><c>elm_installation_open</c>: opens or creates this installation's key and reads the machine fingerprints.</summary>
    /// <param name="options">The options.</param>
    /// <param name="result">Receives the new handle, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_installation_open")]
    public static int InstallationOpen(nint options, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            var o = (InstallationOptions*)options;
            StructSizes.Check(o, "options");
            string product = NativeStrings.Read(o->ProductId, "product_id");
            KeyScope scope = o->Scope switch
            {
                0 => KeyScope.User,
                1 => KeyScope.Machine,
                _ => throw NativeException.Invalid("scope must be ELM_SCOPE_USER or ELM_SCOPE_MACHINE."),
            };
            bool create = Flag(o->Create, "create");
            var store = new InstallationKeyStore(product, scope, NativeStrings.ReadOptional(o->Directory, "directory"));
            InstallationKey? key;
            LicenseError? error;
            if (!(create ? store.TryLoadOrCreate(out key, out error) : store.TryLoad(out key, out error)))
            {
                throw new NativeException(error!.Code, error.Message);
            }

            try
            {
                *result = NativeHandles.Allocate(new NativeInstallation(product, store, key!, MachineFingerprint.Compute(product)));
            }
            catch
            {
                key!.Dispose();
                throw;
            }

            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_installation_thumbprint</c>: the key's JWK thumbprint, valid until the handle is freed.</summary>
    /// <param name="installation">The handle.</param>
    /// <returns>The thumbprint, or <see langword="null"/> for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_installation_thumbprint")]
    public static nint InstallationThumbprint(nint installation) =>
        Guard(() => NativeHandles.Find<NativeInstallation>(installation)?.Thumbprint ?? 0);

    /// <summary><c>elm_installation_free</c>: frees a handle and disposes its key. Null is ignored.</summary>
    /// <param name="installation">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_installation_free")]
    public static void InstallationFree(nint installation) => Guard(() => NativeHandles.Free<NativeInstallation>(installation)?.Dispose());

    /// <summary><c>elm_verifier_create</c>: makes a verifier with the trust store and the clock guard of the options.</summary>
    /// <param name="options">The options.</param>
    /// <param name="result">Receives the new handle, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_verifier_create")]
    public static int VerifierCreate(nint options, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        InstallationClockGuard? owned = null;
        try
        {
            var o = (VerifierOptions*)options;
            StructSizes.Check(o, "options");
            string[] roots = ReadArray(o->RootKeysPem, o->RootKeyCount, 1, MaxRootKeys, "root_keys_pem", NativeStrings.MaxPemBytes);
            string[] revoked = ReadArray(o->RevokedKeyIds, o->RevokedKeyCount, 0, MaxRevokedKeyIds, "revoked_key_ids", NativeStrings.MaxTextBytes);
            int skew = o->ClockSkewSeconds == 0 ? DefaultClockSkewSeconds : o->ClockSkewSeconds;
            if (skew is < 1 or > MaxClockSkewSeconds)
            {
                throw NativeException.Invalid("clock_skew_seconds must be 0 (the default) or 1 to 3600.");
            }

            bool rejectTrial = Flag(o->RejectTrialAsStandard, "reject_trial_as_standard");
            string? path = NativeStrings.ReadOptional(o->ClockGuardPath, "clock_guard_path");
            NativeInstallation? installation = o->Installation == 0 ? null : NativeHandles.Get<NativeInstallation>(o->Installation, "installation");
            TrustStore trust = TrustStore.Create(roots, revoked);

            // design §17.4: the guard gets its own copy of the key, so the installation may be freed after this call.
            IClockGuard guard;
            if (installation is not null)
            {
                InstallationClockGuard signed = installation.Key.CreateClockGuard(path ?? Path.Combine(installation.Directory, "clock"));
                owned = signed;
                guard = signed;
            }
            else
            {
                guard = path is null ? StatelessClockGuard.Instance : new FileClockGuard(path);
            }

            var verifierOptions = new LicenseVerifierOptions { ClockSkew = TimeSpan.FromSeconds(skew), AcceptTrialAsStandard = !rejectTrial };
            *result = NativeHandles.Allocate(new NativeVerifier(new LicenseVerifier(trust, Time, guard, verifierOptions), owned));
            owned = null;
            return 0;
        }
        catch (Exception e)
        {
            owned?.Dispose();
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_verifier_verify</c>: verifies a license file against a requirement.</summary>
    /// <param name="verifier">The verifier.</param>
    /// <param name="file">The license file's bytes, read in place and not kept.</param>
    /// <param name="length">The number of bytes.</param>
    /// <param name="requirement">What the app requires.</param>
    /// <param name="result">Receives the license, or <see langword="null"/> on a failure.</param>
    /// <returns>A design §19 number; 0 when the license passed.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_verifier_verify")]
    public static int VerifierVerify(nint verifier, byte* file, nuint length, nint requirement, nint* result)
    {
        NativeErrors.Clear();
        if (result is null)
        {
            return NativeErrors.Fail(ErrorCode.InvalidArgument, "out is required.");
        }

        *result = 0;
        try
        {
            NativeVerifier v = NativeHandles.Get<NativeVerifier>(verifier, "verifier");
            if (file is null)
            {
                throw NativeException.Invalid("file is required.");
            }

            // design §17.4: the same number as LicenseVerifier's step 1, before any byte is read.
            if (length > MaxLicenseFileBytes)
            {
                throw new NativeException(ErrorCode.Malformed, "The license file is larger than the limit.");
            }

            var r = (Requirement*)requirement;
            StructSizes.Check(r, "requirement");
            string product = NativeStrings.Read(r->ProductId, "product_id");
            LicenseKind kind = r->Kind switch
            {
                0 => LicenseKind.Standard,
                1 => LicenseKind.Trial,
                2 => LicenseKind.Server,
                _ => throw NativeException.Invalid("kind must be ELM_KIND_STANDARD, ELM_KIND_TRIAL, or ELM_KIND_SERVER."),
            };
            string? feature = NativeStrings.ReadOptional(r->FeatureId, "feature_id");
            string? version = NativeStrings.ReadOptional(r->FeatureVersion, "feature_version");
            NativeInstallation? installation = r->Installation == 0 ? null : NativeHandles.Get<NativeInstallation>(r->Installation, "installation");

            // design §17.4: fingerprints are per product, so another product's installation would read as a mismatch.
            if (installation is not null && installation.ProductId != product)
            {
                throw NativeException.Invalid("The installation is for another product than the requirement.");
            }

            LicenseVerificationResult verified = v.Verifier.Verify(
                new ReadOnlySpan<byte>(file, (int)length),
                new LicenseRequirement(product, kind, feature, version, installation?.Identity())
            );
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
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_verifier_destroy</c>: frees a verifier and its clock guard. Null is ignored.</summary>
    /// <param name="verifier">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_verifier_destroy")]
    public static void VerifierDestroy(nint verifier) => Guard(() => NativeHandles.Free<NativeVerifier>(verifier)?.Dispose());

    /// <summary><c>elm_license_id</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The license ID, or <see langword="null"/> for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_id")]
    public static nint LicenseId(nint license) => Guard(() => NativeHandles.Find<NativeLicense>(license)?.Id ?? 0);

    /// <summary><c>elm_license_product</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The product ID, or <see langword="null"/> for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_product")]
    public static nint LicenseProduct(nint license) => Guard(() => NativeHandles.Find<NativeLicense>(license)?.Product ?? 0);

    /// <summary><c>elm_license_kind</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>An <c>ELM_KIND_*</c> value, or -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_kind")]
    public static int LicenseKindOf(nint license) =>
        Guard(() => NativeHandles.Find<NativeLicense>(license) is NativeLicense l ? (int)l.Kind : -1, -1);

    /// <summary><c>elm_license_issued_at</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The issue time in UNIX seconds, or 0 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_issued_at")]
    public static long LicenseIssuedAt(nint license) => Guard(() => NativeHandles.Find<NativeLicense>(license)?.IssuedAt ?? 0);

    /// <summary><c>elm_license_expires_at</c>.</summary>
    /// <param name="license">The license.</param>
    /// <param name="hasValue">Receives 1 when the license expires, 0 when it does not.</param>
    /// <param name="value">Receives the expiry in UNIX seconds, or 0.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_expires_at")]
    public static int LicenseExpiresAt(nint license, int* hasValue, long* value)
    {
        NativeErrors.Clear();
        if (hasValue is not null)
        {
            *hasValue = 0;
        }

        if (value is not null)
        {
            *value = 0;
        }

        try
        {
            if (hasValue is null || value is null)
            {
                throw NativeException.Invalid("has_value and value are required.");
            }

            NativeLicense l = NativeHandles.Get<NativeLicense>(license, "license");
            *hasValue = l.ExpiresAt is null ? 0 : 1;
            *value = l.ExpiresAt ?? 0;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_license_grace_period_seconds</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The grace period in seconds, or 0 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_grace_period_seconds")]
    public static long LicenseGracePeriodSeconds(nint license) => Guard(() => NativeHandles.Find<NativeLicense>(license)?.GracePeriodSeconds ?? 0);

    /// <summary><c>elm_license_in_grace_period</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>1 when the license is past its expiry but inside the grace period, 0 when not, -1 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_in_grace_period")]
    public static int LicenseInGracePeriod(nint license) =>
        Guard(() => NativeHandles.Find<NativeLicense>(license) is NativeLicense l ? (l.InGracePeriod ? 1 : 0) : -1, -1);

    /// <summary><c>elm_license_feature_count</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The number of features, or 0 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_feature_count")]
    public static nuint LicenseFeatureCount(nint license) => Guard(() => (nuint)(NativeHandles.Find<NativeLicense>(license)?.Features.Count ?? 0));

    /// <summary><c>elm_license_feature</c>: writes one feature into the caller's struct.</summary>
    /// <param name="license">The license.</param>
    /// <param name="index">The feature's index.</param>
    /// <param name="feature">The struct to write, with its <c>struct_size</c> set.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_feature")]
    public static int LicenseFeature(nint license, nuint index, nint feature)
    {
        NativeErrors.Clear();
        try
        {
            var f = (Feature*)feature;
            StructSizes.Check(f, "out");
            NativeLicense l = NativeHandles.Get<NativeLicense>(license, "license");
            if (index >= (nuint)l.Features.Count)
            {
                throw NativeException.Invalid($"index must be less than {l.Features.Count}.");
            }

            NativeLicense.FeatureEntry entry = l.Features[(int)index];
            f->Id = (byte*)entry.Id;
            f->VersionRange = (byte*)entry.VersionRange;
            f->HasSeats = entry.Seats is null ? 0 : 1;
            f->Seats = entry.Seats ?? 0;
            f->HasExpiresAt = entry.ExpiresAt is null ? 0 : 1;
            f->ExpiresAt = entry.ExpiresAt ?? 0;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_license_meta_count</c>.</summary>
    /// <param name="license">The license.</param>
    /// <returns>The number of meta pairs, or 0 for a null handle.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_meta_count")]
    public static nuint LicenseMetaCount(nint license) => Guard(() => (nuint)(NativeHandles.Find<NativeLicense>(license)?.Meta.Count ?? 0));

    /// <summary><c>elm_license_meta_at</c>: one meta pair with the byte lengths, which may hold NUL.</summary>
    /// <param name="license">The license.</param>
    /// <param name="index">The pair's index.</param>
    /// <param name="key">Receives the key.</param>
    /// <param name="keyLength">Receives the key's length in bytes.</param>
    /// <param name="value">Receives the value.</param>
    /// <param name="valueLength">Receives the value's length in bytes.</param>
    /// <returns>A design §19 number; 0 on success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_meta_at")]
    public static int LicenseMetaAt(nint license, nuint index, nint* key, nuint* keyLength, nint* value, nuint* valueLength)
    {
        NativeErrors.Clear();
        ClearOutputs(key, keyLength);
        ClearOutputs(value, valueLength);
        try
        {
            if (key is null || keyLength is null || value is null || valueLength is null)
            {
                throw NativeException.Invalid("key, key_len, value, and value_len are required.");
            }

            NativeLicense l = NativeHandles.Get<NativeLicense>(license, "license");
            if (index >= (nuint)l.Meta.Count)
            {
                throw NativeException.Invalid($"index must be less than {l.Meta.Count}.");
            }

            NativeLicense.MetaEntry entry = l.Meta[(int)index];
            *key = entry.KeyPointer;
            *keyLength = entry.KeyLength;
            *value = entry.ValuePointer;
            *valueLength = entry.ValueLength;
            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_license_meta_get</c>: the value of a key, or <see langword="null"/> when the key is not there.</summary>
    /// <param name="license">The license.</param>
    /// <param name="key">The key, NUL-terminated UTF-8.</param>
    /// <param name="value">Receives the value, or <see langword="null"/>.</param>
    /// <param name="valueLength">Receives the value's length in bytes, or 0.</param>
    /// <returns>A design §19 number; 0 on success, also when the key is not there.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_meta_get")]
    public static int LicenseMetaGet(nint license, byte* key, nint* value, nuint* valueLength)
    {
        NativeErrors.Clear();
        ClearOutputs(value, valueLength);
        try
        {
            if (value is null || valueLength is null)
            {
                throw NativeException.Invalid("value and value_len are required.");
            }

            NativeLicense l = NativeHandles.Get<NativeLicense>(license, "license");
            string wanted = NativeStrings.Read(key, "key");
            foreach (NativeLicense.MetaEntry entry in l.Meta)
            {
                if (string.Equals(entry.Key, wanted, StringComparison.Ordinal))
                {
                    *value = entry.ValuePointer;
                    *valueLength = entry.ValueLength;
                    break;
                }
            }

            return 0;
        }
        catch (Exception e)
        {
            return NativeErrors.Fail(e);
        }
    }

    /// <summary><c>elm_license_free</c>: frees a license and its strings. Null is ignored.</summary>
    /// <param name="license">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "elm_license_free")]
    public static void LicenseFree(nint license) => Guard(() => NativeHandles.Free<NativeLicense>(license)?.Dispose());

    /// <summary><c>elm_last_error_message</c>: the message of the last function that returned a number on this thread.</summary>
    /// <returns>The message, empty after a success.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_last_error_message")]
    public static nint LastErrorMessage() => Guard(() => (nint)NativeErrors.Pointer);

    /// <summary><c>elm_version</c>: the library's version.</summary>
    /// <returns>The version, valid until the process ends.</returns>
    [UnmanagedCallersOnly(EntryPoint = "elm_version")]
    public static nint VersionText() => Version;

    private static bool Flag(int value, string name) =>
        value switch
        {
            0 => false,
            1 => true,
            _ => throw NativeException.Invalid($"{name} must be 0 or 1."),
        };

    private static string[] ReadArray(byte** items, nuint count, int min, int max, string name, int maxBytes)
    {
        if (count < (nuint)min || count > (nuint)max)
        {
            throw NativeException.Invalid($"{name} must hold {min} to {max} entries.");
        }

        if (count > 0 && items is null)
        {
            throw NativeException.Invalid($"{name} is required.");
        }

        var result = new string[(int)count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = NativeStrings.Read(items[i], $"{name}[{i}]", maxBytes);
        }

        return result;
    }

    private static void ClearOutputs(nint* pointer, nuint* length)
    {
        if (pointer is not null)
        {
            *pointer = 0;
        }

        if (length is not null)
        {
            *length = 0;
        }
    }

    // A value function never lets an exception out either (design §17.3); it then returns the value of a null handle.
    private static T Guard<T>(Func<T> read, T fallback = default!)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void Guard(Action release)
    {
        try
        {
            release();
        }
        catch (Exception)
        {
            // A release has nowhere to report a failure; the handle is gone either way.
        }
    }
}
