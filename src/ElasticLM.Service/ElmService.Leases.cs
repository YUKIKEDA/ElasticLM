using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The cloud leases of M4d1 (design §12.7.6, decision 0.93): acquisition, renewal, and release with a proof of the
/// installation key, decided by the rules the on-premises server uses (<see cref="LeaseRules"/>, decision 0.64), on rows read
/// under the license's lock; and the administration API's list and forced release.
/// </content>
internal sealed partial class ElmService
{
    private const string UnknownLease = "No lease has this ID; acquire again.";
    private const string LeaseNotSigned = "The service cannot sign a lease now.";

    private void MapLeases()
    {
        Func<HttpContext, Task<ClientResult>> acquire = Counted(AcquireAsync);
        Func<HttpContext, Task<ClientResult>> renew = Counted(RenewAsync);
        Func<HttpContext, Task<ClientResult>> release = Counted(ReleaseAsync);
        _app.MapPost("/v1/leases", (RequestDelegate)(c => ClientAsync(c, acquire)));
        _app.MapPost("/v1/leases/{id}/renew", (RequestDelegate)(c => ClientAsync(c, renew)));
        _app.MapDelete("/v1/leases/{id}", (RequestDelegate)(c => ClientAsync(c, release)));
        _app.MapGet(
            "/v1/admin/licenses/{id}/leases",
            (RequestDelegate)(c => AdminAsync(c, Permissions.LeaseRead, AuditActions.LeaseList, ListLeasesAsync))
        );
        _app.MapDelete(
            "/v1/admin/leases/{id}",
            (RequestDelegate)(c => AdminAsync(c, Permissions.LeaseRelease, AuditActions.LeaseRelease, AdminReleaseAsync))
        );
    }

    // design §12.7.6, acquisition steps 1 to 7.
    private async Task<ClientResult> AcquireAsync(HttpContext context)
    {
        if (await ReadProvenAsync(context, null).ConfigureAwait(false) is not (byte[] bytes, ProofResult.Valid proof))
        {
            return ClientResult.Answered;
        }

        AcquireRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, LeaseRequests.Malformed);
            request = LeaseRequests.ReadAcquire(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        return WithFeature(await AcquireParsedAsync(context, proof, request).ConfigureAwait(false), request.Feature);
    }

    private async Task<ClientResult> AcquireParsedAsync(HttpContext context, ProofResult.Valid proof, AcquireRequest request)
    {
        const string NoKey = "No license has this key.";
        CancellationToken aborted = context.RequestAborted;
        if (
            LicenseKeys.Normalize(request.LicenseKey) is null
            || await Database.FindLicenseByKeyAsync(LicenseKeys.HashOf(request.LicenseKey), aborted).ConfigureAwait(false) is not LicenseRow found
        )
        {
            return ClientResult.Fail(ErrorCode.LicenseNotFound, NoKey);
        }

        PolicyRow policy = (await Database.FindPolicyAsync(found.PolicyId, aborted).ConfigureAwait(false))!;
        ProductRow product = (await Database.FindProductAsync(found.ProductId, aborted).ConfigureAwait(false))!;
        if (product.Code != request.Product)
        {
            return ClientResult.Fail(ErrorCode.WrongProduct, "The license key is for another product.");
        }

        if (policy.Floating != Policies.Cloud)
        {
            return ClientResult.Fail(ErrorCode.Conflict, "Only a license of a cloud-floating policy lends seats.");
        }

        DateTimeOffset now = Time.GetUtcNow();
        Device device = Online(context, proof);
        return await Database
            .WithLicenseAsync(
                found.Id,
                scope =>
                    scope is null
                        ? Task.FromResult(ClientResult.Fail(ErrorCode.LicenseNotFound, NoKey))
                        : AcquireInScopeAsync(scope, device, request, policy, product, now),
                now,
                aborted
            )
            .ConfigureAwait(false);
    }

    private async Task<ClientResult> AcquireInScopeAsync(
        LicenseScope scope,
        Device device,
        AcquireRequest request,
        PolicyRow policy,
        ProductRow product,
        DateTimeOffset now
    )
    {
        long at = now.ToUnixTimeSeconds();
        LicenseRow license = scope.License;
        if (Refusal(license, at) is ClientResult refused)
        {
            return refused;
        }

        LicenseFeature? feature = LeasedFeature(license, request.Feature);
        if (CheckFeature(feature, request.Version, at) is LicenseError featureError)
        {
            return ClientResult.Fail(featureError.Code, featureError.Message);
        }

        int seats = (int)feature!.Seats!.Value;
        int count = (int)Math.Clamp(request.Count, 0, int.MaxValue);
        TimeSpan? borrow = request.Borrow is long b ? TimeSpan.FromSeconds(b) : null;
        if (
            LeaseRules.CheckRequest(count, seats, borrow, TimeSpan.FromSeconds(policy.BorrowLimit ?? 0), request.Fingerprints.Count > 0)
            is LicenseError requestError
        )
        {
            return ClientResult.Fail(requestError.Code, requestError.Message);
        }

        long held = await scope.SeatsHeldAsync(feature.Code, at, device.Aborted).ConfigureAwait(false);
        if (!LeaseRules.Fits(held, count, LeaseRules.Allowed(seats, (int)(policy.Overage ?? 0)), 0, borrow is not null))
        {
            return ClientResult.Fail(ErrorCode.NoSeats, "No seat of the feature is free.");
        }

        // design §9.3 FromFirstActivation: the first lease fixes the expiry, as the first activation does.
        if (policy.ExpirationBasis == Policies.FromFirstActivation && license.ExpiresAt is null && policy.Duration is long duration)
        {
            license = license with { ExpiresAt = at + duration, UpdatedAt = at };
            await scope.SetExpiryAsync(at + duration, at, device.Aborted).ConfigureAwait(false);
        }

        long period = request.Borrow ?? policy.LeaseDuration;
        if (LeaseRules.Expiry(at, period, LeaseLimit(license, feature)) is not long expires)
        {
            return ClientResult.Fail(ErrorCode.Expired, "The license has expired.");
        }

        ClientInfo? client = policy.ClientInfoStrategy == "hostAndUser" ? request.Client : null;
        var lease = new LeaseRow(
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            license.Id,
            feature.Code,
            count,
            device.KeyId,
            device.Key.Q.X!,
            device.Key.Q.Y!,
            1,
            string.Empty,
            device.RequestId,
            expires,
            borrow is not null,
            borrow is not null ? Activations.Store(request.Fingerprints) : null,
            borrow is not null ? seats : null,
            borrow is not null ? policy.BorrowLimit ?? 0 : null,
            false,
            client?.Host,
            client?.User,
            request.App,
            at
        );
        lease = lease with { LastToken = IssueLease(lease, product, now) };
        await scope.InsertLeaseAsync(lease, device.Aborted).ConfigureAwait(false);
        return LeaseAnswer(StatusCodes.Status201Created, lease.LastToken) with
        {
            After = () => _usage?.Lease(lease.Borrow ? "borrow.started" : "lease.acquired", lease, product.Code, w => Acquired(w, lease)),
        };
    }

    // design §12.7.6 renewal: the lease's key proves it; the next token, the last one again for a retry, or a clone.
    private async Task<ClientResult> RenewAsync(HttpContext context)
    {
        if (await FindLeaseAsync(context).ConfigureAwait(false) is not LeaseRow found)
        {
            return ClientResult.Fail(ErrorCode.UnknownLease, UnknownLease);
        }

        if (
            await ReadProvenAsync(context, (found.KeyId, found.KeyX, found.KeyY), "The proof is not made with the lease's installation key.")
                .ConfigureAwait(false)
            is not
            (byte[] bytes, ProofResult.Valid proof)
        )
        {
            return ClientResult.Answered;
        }

        SequenceRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, LeaseRequests.Malformed);
            request = Activations.ReadSequence(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        (PolicyRow policy, ProductRow product) = await PolicyAndProductAsync(found.LicenseId, context.RequestAborted).ConfigureAwait(false);
        DateTimeOffset now = Time.GetUtcNow();
        Device device = Online(context, proof);
        ClientResult result = await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    long at = now.ToUnixTimeSeconds();
                    if (scope is null || await scope.FindLeaseAsync(found.Id, at, device.Aborted).ConfigureAwait(false) is not LeaseRow lease)
                    {
                        return ClientResult.Fail(ErrorCode.UnknownLease, UnknownLease);
                    }

                    return await RenewInScopeAsync(scope, device, lease, request, policy, product, now).ConfigureAwait(false);
                },
                now,
                device.Aborted
            )
            .ConfigureAwait(false);
        return WithFeature(result, found.Feature);
    }

    private async Task<ClientResult> RenewInScopeAsync(
        LicenseScope scope,
        Device device,
        LeaseRow lease,
        SequenceRequest request,
        PolicyRow policy,
        ProductRow product,
        DateTimeOffset now
    )
    {
        long at = now.ToUnixTimeSeconds();
        if (lease.Stopped)
        {
            return ClientResult.Fail(ErrorCode.CloneDetected, "The lease was stopped because a clone of the installation was detected.");
        }

        if (lease.Borrow)
        {
            return ClientResult.Fail(ErrorCode.InvalidArgument, "A borrowed lease is not renewed.");
        }

        if (policy.Floating != Policies.Cloud)
        {
            return ClientResult.Fail(ErrorCode.Conflict, "Only a license of a cloud-floating policy lends seats.");
        }

        LicenseRow license = scope.License;
        if (Refusal(license, at) is ClientResult refused)
        {
            return refused;
        }

        LicenseFeature? feature = LeasedFeature(license, lease.Feature);
        if (CheckFeature(feature, null, at) is LicenseError featureError)
        {
            return ClientResult.Fail(featureError.Code, featureError.Message);
        }

        // design §13.1, §9.3: the seats held count this lease too, so a license that lost seats refuses renewals until the
        // seats held fit again.
        long held = await scope.SeatsHeldAsync(lease.Feature, at, device.Aborted).ConfigureAwait(false);
        if (held > LeaseRules.Allowed((int)feature!.Seats!.Value, (int)(policy.Overage ?? 0)))
        {
            return ClientResult.Fail(ErrorCode.NoSeats, "The seats held are more than the license allows now.");
        }

        switch (LeaseRules.Judge(request.Seq, lease.Seq, lease.IssuedBy, request.Retry))
        {
            case LeaseSequence.Retry:
                // The answer to the request that got the last token was lost: the same token again, nothing changes.
                return LeaseAnswer(StatusCodes.Status200OK, lease.LastToken);
            case LeaseSequence.Clone:
                if (await LeaseCloneAsync(device, scope, policy, lease, request.Seq).ConfigureAwait(false) is ClientResult stopped)
                {
                    return stopped;
                }

                break;
        }

        if (LeaseRules.Expiry(at, policy.LeaseDuration, LeaseLimit(license, feature)) is not long expires)
        {
            return ClientResult.Fail(ErrorCode.Expired, "The license has expired.");
        }

        LeaseRow next = lease with { Seq = lease.Seq + 1, IssuedBy = device.RequestId, ExpiresAt = expires };
        next = next with { LastToken = IssueLease(next, product, now) };
        await scope.UpdateLeaseAsync(next, device.Aborted).ConfigureAwait(false);
        return LeaseAnswer(StatusCodes.Status200OK, next.LastToken) with
        {
            After = () => _usage?.Lease("lease.renewed", next, product.Code, w => w.WriteNumber("exp", next.ExpiresAt)),
        };
    }

    // design §12.7.6 release: a lease that is not there is released already; a borrow only within the early-return limit.
    private async Task<ClientResult> ReleaseAsync(HttpContext context)
    {
        if (await FindLeaseAsync(context).ConfigureAwait(false) is not LeaseRow found)
        {
            return Released;
        }

        if (
            await ReadProvenAsync(context, (found.KeyId, found.KeyX, found.KeyY), "The proof is not made with the lease's installation key.")
                .ConfigureAwait(false)
            is not
            (_, ProofResult.Valid proof)
        )
        {
            return ClientResult.Answered;
        }

        (PolicyRow policy, ProductRow product) = await PolicyAndProductAsync(found.LicenseId, context.RequestAborted).ConfigureAwait(false);
        DateTimeOffset now = Time.GetUtcNow();
        Device device = Online(context, proof);
        ClientResult result = await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    long at = now.ToUnixTimeSeconds();
                    if (scope is null || await scope.FindLeaseAsync(found.Id, at, device.Aborted).ConfigureAwait(false) is not LeaseRow lease)
                    {
                        return Released;
                    }

                    if (lease.Stopped)
                    {
                        return ClientResult.Fail(ErrorCode.CloneDetected, "The lease was stopped because a clone of the installation was detected.");
                    }

                    IReadOnlyList<string> counters = [];
                    if (lease.Borrow)
                    {
                        counters = [.. LeaseRules.ReturnCounters(lease.KeyId, Activations.Load(lease.BorrowFingerprints ?? "[]"))];
                        long since = at - (long)LeaseRules.ReturnWindow.TotalSeconds;
                        int returns = await scope.CountReturnsAsync(counters, since, device.Aborted).ConfigureAwait(false);

                        // design §12.7.4: a feature the license no longer grants has 0 seats, so it counts as fewer seats.
                        long seatsNow = LeasedFeature(scope.License, lease.Feature)?.Seats ?? 0;
                        if (
                            !LeaseRules.MayReturnEarly(
                                returns,
                                (int)policy.BorrowReturnLimit,
                                true,
                                (int)seatsNow,
                                (int)(lease.BorrowFeatureSeats ?? 0),
                                TimeSpan.FromSeconds(policy.BorrowLimit ?? 0),
                                TimeSpan.FromSeconds(lease.BorrowLimit ?? 0)
                            )
                        )
                        {
                            return ClientResult.Fail(ErrorCode.ReturnNotAllowed, "The borrow cannot be returned early now; it ends at its expiry.");
                        }
                    }

                    await scope.DeleteLeaseAsync(lease.Id, device.Aborted).ConfigureAwait(false);
                    if (lease.Borrow)
                    {
                        await scope.RecordReturnAsync(counters, at, device.Aborted).ConfigureAwait(false);
                    }

                    return Released with
                    {
                        After = () => ReleasedLine(lease, product.Code, "device"),
                    };
                },
                now,
                device.Aborted
            )
            .ConfigureAwait(false);
        return WithFeature(result, found.Feature);
    }

    // design §18.1, 0.95: every refusal of a lease request is counted for the minute's lease.denied line.
    private Func<HttpContext, Task<ClientResult>> Counted(Func<HttpContext, Task<ClientResult>> operation) =>
        async context =>
        {
            ClientResult result = await operation(context).ConfigureAwait(false);
            if (result.Error is ErrorCode code)
            {
                _usage?.Denied(result.Feature, code);
            }

            return result;
        };

    private static ClientResult WithFeature(ClientResult result, string feature) => result.Error is null ? result : result with { Feature = feature };

    // The members of an acquisition's line: the expiry, and the client and app the policy keeps.
    private static void Acquired(Utf8JsonWriter writer, LeaseRow lease)
    {
        writer.WriteNumber("exp", lease.ExpiresAt);
        if (lease.ClientHost is string host && lease.ClientUser is string user)
        {
            writer.WriteString("host", host);
            writer.WriteString("user", user);
        }

        if (lease.App is string app)
        {
            writer.WriteString("app", app);
        }
    }

    // A release by the device or an administrator: lease.released, or borrow.returned for a borrow.
    private void ReleasedLine(LeaseRow lease, string productCode, string by) =>
        _usage?.Lease(lease.Borrow ? "borrow.returned" : "lease.released", lease, productCode, w => w.WriteString("by", by));

    // A detected clone of a lease (design §10.4, §12.7.3): Deny stops the lease until its expiry, Flag lets the renewal go on;
    // both write lease.clone, the only device request of a lease with an audit row (M4d1's design review).
    private static async Task<ClientResult?> LeaseCloneAsync(Device device, LicenseScope scope, PolicyRow policy, LeaseRow lease, long seq)
    {
        bool deny = policy.CloneStrategy == "deny";
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("licenseId", lease.LicenseId.ToString("D"));
            w.WriteString("feature", lease.Feature);
            w.WriteNumber("seq", seq);
            w.WriteNumber("lastSeq", lease.Seq);
            w.WriteString("strategy", policy.CloneStrategy);
        });
        string result = deny ? ErrorCodes.ToWireName(ErrorCode.CloneDetected) : "ELM_OK";
        var entry = new AuditEntry(device.ActorType, device.ActorId, AuditActions.LeaseClone, lease.Id, result, device.Ip, detail);
        await scope.AppendAuditAsync(entry, device.Aborted).ConfigureAwait(false);
        if (!deny)
        {
            return null;
        }

        await scope.UpdateLeaseAsync(lease with { Stopped = true }, device.Aborted).ConfigureAwait(false);
        return ClientResult.Fail(ErrorCode.CloneDetected, "Another copy of the installation went ahead; the lease is stopped until it expires.");
    }

    private async Task<AdminOutcome> ListLeasesAsync(HttpContext context, Caller caller)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow license)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        string target = license.Id.ToString("D");
        if (!TryReadLimit(context, out int limit))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.", target).ConfigureAwait(false);
        }

        string? after = null;
        if (context.Request.Query["after"] is { Count: > 0 } afterText)
        {
            if (afterText.Count != 1 || !LeaseRequests.IsLeaseId(afterText[0]))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be a lease ID.", target).ConfigureAwait(false);
            }

            after = afterText[0];
        }

        long now = Time.GetUtcNow().ToUnixTimeSeconds();
        IReadOnlyList<LeaseRow> rows = await Database
            .ListLeasesAsync(license.Id, after, limit + 1, now, context.RequestAborted)
            .ConfigureAwait(false);
        LeaseRow[] page = [.. rows.Take(limit)];
        var answer = new LeasePage([.. page.Select(ToItem)], rows.Count > limit ? page[^1].Id : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.LeasePage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", target);
    }

    // design §12.7.6: an administrator frees a lease's seats without its proof; in the cloud a borrow too (M4d1's design review).
    private async Task<AdminOutcome> AdminReleaseAsync(HttpContext context, Caller caller)
    {
        if (await FindLeaseAsync(context).ConfigureAwait(false) is not LeaseRow found)
        {
            return await RefuseAsync(context, ErrorCode.UnknownLease, "No lease has this ID.", RouteText(context, "id")).ConfigureAwait(false);
        }

        DateTimeOffset now = Time.GetUtcNow();
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("licenseId", found.LicenseId.ToString("D"));
            w.WriteString("feature", found.Feature);
            w.WriteNumber("seats", found.Seats);
        });
        bool removed = await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    if (
                        scope is null
                        || await scope.FindLeaseAsync(found.Id, now.ToUnixTimeSeconds(), context.RequestAborted).ConfigureAwait(false) is null
                    )
                    {
                        return false;
                    }

                    await scope.DeleteLeaseAsync(found.Id, context.RequestAborted).ConfigureAwait(false);
                    var entry = new AuditEntry(
                        "apikey",
                        caller.Key.Id.ToString("D"),
                        AuditActions.LeaseRelease,
                        found.Id,
                        "ELM_OK",
                        Ip(context),
                        detail
                    );
                    await scope.AppendAuditAsync(entry, context.RequestAborted).ConfigureAwait(false);
                    return true;
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);
        if (!removed)
        {
            return await RefuseAsync(context, ErrorCode.UnknownLease, "No lease has this ID.", found.Id).ConfigureAwait(false);
        }

        if (_usage is not null)
        {
            (_, ProductRow product) = await PolicyAndProductAsync(found.LicenseId, context.RequestAborted).ConfigureAwait(false);
            ReleasedLine(found, product.Code, "admin");
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", found.Id, detail, Recorded: true);
    }

    // A route ID that cannot be a lease is answered without a query (design §12.7.6); the per-source limit already counted it.
    private async Task<LeaseRow?> FindLeaseAsync(HttpContext context) =>
        RouteText(context, "id") is string id && LeaseRequests.IsLeaseId(id)
            ? await Database.FindLeaseAsync(id, Time.GetUtcNow().ToUnixTimeSeconds(), context.RequestAborted).ConfigureAwait(false)
            : null;

    private static ClientResult Released { get; } = new(StatusCodes.Status204NoContent, null, null, Guid.Empty, null);

    // design §12.7.6 step 5: a feature without seats is not leased, as one the license does not grant.
    private static LicenseFeature? LeasedFeature(LicenseRow license, string code) =>
        Licenses.Load(license.Features).FirstOrDefault(f => f.Code == code && f.Seats is not null);

    private static LicenseError? CheckFeature(LicenseFeature? feature, string? version, long at) =>
        LeaseRules.CheckFeature(
            feature is not null,
            feature?.ExpiresAt is long e ? DateTimeOffset.FromUnixTimeSeconds(e) : null,
            feature?.Ver,
            version,
            at
        );

    // M4d1's design review: a lease ends no later than the license and the feature, with no grace period added.
    private static long? LeaseLimit(LicenseRow license, LicenseFeature feature) =>
        (license.ExpiresAt, feature.ExpiresAt) switch
        {
            (long l, long f) => Math.Min(l, f),
            (long l, null) => l,
            (null, long f) => f,
            _ => null,
        };

    // design §8.4.3: a lease token signed with the service's signing key, checked with the service's trust store before it
    // is returned; a token that does not verify is an internal error, and the transaction is rolled back.
    private string IssueLease(LeaseRow lease, ProductRow product, DateTimeOffset now)
    {
        var spec = new LeaseSpec
        {
            LeaseId = lease.Id,
            ProductId = product.Code,
            LicenseId = lease.LicenseId.ToString("D"),
            FeatureId = lease.Feature,
            Seats = (int)lease.Seats,
            InstallationKeyThumbprint = lease.KeyId,
            Duration = TimeSpan.FromSeconds(lease.ExpiresAt - now.ToUnixTimeSeconds()),
            IsBorrow = lease.Borrow,
            Sequence = lease.Seq,
        };
        string token;
        try
        {
            token = LeaseIssuer.Issue(_signingKey, spec, now);
        }
        catch (ArgumentException e)
        {
            throw new IssueFailedException(e.Message, LeaseNotSigned);
        }

        if (
            _leaseCheck.VerifyHosted(token, _signingCertificates, new LeaseExpectation(product.Code, lease.KeyId))
            is LeaseVerificationResult.Invalid invalid
        )
        {
            throw new IssueFailedException(invalid.Error.Message, LeaseNotSigned);
        }

        return token;
    }

    // design §12.7.6: {lease, certificates}.
    private ClientResult LeaseAnswer(int status, string token)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("lease", token);
            w.WriteStartArray("certificates");
            foreach (string certificate in _signingCertificates)
            {
                w.WriteStringValue(certificate);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return new ClientResult(status, null, null, Guid.Empty, null, buffer.ToArray());
    }

    private static LeaseItem ToItem(LeaseRow l) =>
        new(
            l.Id,
            l.Feature,
            l.Seats,
            l.KeyId,
            l.Seq,
            AdminApi.Time(l.ExpiresAt),
            l.Borrow,
            l.Stopped,
            l.ClientHost is string host && l.ClientUser is string user ? new ClientItem(host, user) : null,
            l.App,
            AdminApi.Time(l.CreatedAt)
        );
}
