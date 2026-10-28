using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The requests devices send (design §12.2 to §12.6, §12.9, M4b1): the nonce, online activation, refresh, deactivation, and
/// trials, each with a proof of the installation key, and clone detection by sequence number (design §10.4).
/// </content>
internal sealed partial class ElmService
{
    private const string ProofHeader = "ELM-Proof";
    private const string NonceHeader = "ELM-Nonce";
    private const string InstallationActor = "installation";
    private const string NoActivation = "No activation has this ID; activate again.";
    private static readonly TimeSpan ProofSkew = TimeSpan.FromMinutes(1);

    private void MapClient()
    {
        _app.MapGet("/v1/nonce", (RequestDelegate)NonceAsync);
        _app.MapPost("/v1/activations", (RequestDelegate)(c => ClientAsync(c, ActivateAsync)));
        _app.MapPost("/v1/activations/{id}/refresh", (RequestDelegate)(c => ClientAsync(c, RefreshAsync)));
        _app.MapPost("/v1/activations/{id}/deactivate", (RequestDelegate)(c => ClientAsync(c, DeactivateAsync)));
        _app.MapPost("/v1/trials", (RequestDelegate)(c => ClientAsync(c, TrialAsync)));
    }

    private Task NonceAsync(HttpContext context)
    {
        string nonce = _nonces.Issue();
        context.Response.Headers[NonceHeader] = nonce;
        return WriteJsonAsync(context, StatusCodes.Status200OK, new NonceAnswer(nonce), ServiceJsonContext.Default.NonceAnswer);
    }

    // Every answer to a device carries a fresh nonce (design §12.1), and a database failure is an internal error.
    private async Task ClientAsync(HttpContext context, Func<HttpContext, Task<ClientResult>> operation)
    {
        context.Response.Headers[NonceHeader] = _nonces.Issue();
        try
        {
            ClientResult result = await operation(context).ConfigureAwait(false);
            if (ReferenceEquals(result, ClientResult.Answered))
            {
                return;
            }

            if (result.Error is ErrorCode code)
            {
                await ProblemAsync(context, code, result.Message!).ConfigureAwait(false);
            }
            else if (result.File is byte[] file)
            {
                await WriteIssuedAsync(context, result.Status, result.ActivationId, file).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = result.Status;
            }
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            if (!context.Response.HasStarted)
            {
                await ProblemAsync(context, ErrorCode.InternalError, "The database failed.").ConfigureAwait(false);
            }
        }
        catch (IssueFailedException e)
        {
            // Thrown inside the write transaction, so nothing of the request was written.
            ServiceLog.IssueFailed(_log, e.Message);
            await ProblemAsync(context, ErrorCode.InternalError, IssueFailedException.Answer).ConfigureAwait(false);
        }
    }

    // design §12.4: a new activation, or the same installation key again, judged by its sequence number (design §10.4).
    private async Task<ClientResult> ActivateAsync(HttpContext context)
    {
        if (await ReadProvenAsync(context, null).ConfigureAwait(false) is not (byte[] bytes, ProofResult.Valid proof))
        {
            return ClientResult.Answered;
        }

        ActivationRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, Activations.Malformed);
            request = Activations.ReadActivation(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        const string NoKey = "No license has this key.";
        if (
            LicenseKeys.Normalize(request.LicenseKey) is null
            || await Database.FindLicenseByKeyAsync(LicenseKeys.HashOf(request.LicenseKey), context.RequestAborted).ConfigureAwait(false)
                is not LicenseRow found
        )
        {
            return ClientResult.Fail(ErrorCode.LicenseNotFound, NoKey);
        }

        PolicyRow policy = (await Database.FindPolicyAsync(found.PolicyId, context.RequestAborted).ConfigureAwait(false))!;
        ProductRow product = (await Database.FindProductAsync(found.ProductId, context.RequestAborted).ConfigureAwait(false))!;
        if (policy.Binding == Policies.Unbound)
        {
            // Round 1 of the M4b Grill: an activation not bound to a key could not be counted, so an unbound license only has
            // its file (decision 0.76).
            return ClientResult.Fail(ErrorCode.Conflict, "A license of an unbound policy is not activated; its file comes from the vendor.");
        }

        if (policy.FingerprintMatching != "ignore" && request.Fingerprints.Count == 0)
        {
            return ClientResult.Fail(ErrorCode.FingerprintUnavailable, "The policy compares fingerprints, and the request has none.");
        }

        DateTimeOffset now = Time.GetUtcNow();
        long at = now.ToUnixTimeSeconds();
        ClientInfo? client = policy.ClientInfoStrategy == "hostAndUser" ? request.Client : null;
        return await Database
            .WithLicenseAsync(
                found.Id,
                async scope =>
                {
                    if (scope is null)
                    {
                        return ClientResult.Fail(ErrorCode.LicenseNotFound, NoKey);
                    }

                    LicenseRow license = scope.License;
                    if (Refusal(license, at) is ClientResult refused)
                    {
                        return refused;
                    }

                    if (await scope.FindActivationByKeyAsync(proof.KeyId, context.RequestAborted).ConfigureAwait(false) is ActivationRow existing)
                    {
                        return await NextTokenAsync(
                                context,
                                scope,
                                policy,
                                product,
                                existing,
                                request.Seq,
                                request.Retry,
                                proof,
                                a =>
                                    a with
                                    {
                                        Fingerprints = Activations.Store(request.Fingerprints),
                                        ClientHost = client?.Host,
                                        ClientUser = client?.User,
                                        App = request.App,
                                    },
                                "activate",
                                at
                            )
                            .ConfigureAwait(false);
                    }

                    long limit = license.MaxActivations ?? policy.MaxActivations;
                    if (await scope.CountActivationsAsync(context.RequestAborted).ConfigureAwait(false) >= limit)
                    {
                        return ClientResult.Fail(ErrorCode.ActivationLimit, $"The license has reached its limit of {limit} activations.");
                    }

                    // design §9.3 FromFirstActivation: the first activation fixes the expiry; later ones leave it.
                    if (policy.ExpirationBasis == Policies.FromFirstActivation && license.ExpiresAt is null && policy.Duration is long duration)
                    {
                        license = license with { ExpiresAt = at + duration, UpdatedAt = at };
                        await scope.UpdateLicenseAsync(license, context.RequestAborted).ConfigureAwait(false);
                    }

                    byte[] file = IssueFile(license, policy, product, proof.KeyId, request.Fingerprints, 1);
                    var activation = new ActivationRow(
                        Guid.CreateVersion7(now),
                        license.Id,
                        proof.KeyId,
                        proof.Key.Q.X!,
                        proof.Key.Q.Y!,
                        Activations.Store(request.Fingerprints),
                        client?.Host,
                        client?.User,
                        request.App,
                        1,
                        file,
                        proof.ProofId,
                        null,
                        at,
                        at
                    );
                    await scope.InsertActivationAsync(activation, context.RequestAborted).ConfigureAwait(false);
                    await scope
                        .AppendAuditAsync(
                            DeviceEntry(context, proof, AuditActions.ActivationActivate, activation, ActivateDetail(license, 1, false)),
                            context.RequestAborted
                        )
                        .ConfigureAwait(false);
                    return ClientResult.Issued(StatusCodes.Status201Created, activation.Id, file);
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);
    }

    // design §12.5: a new token with the next sequence number, or the last one again for a retry (design §10.4).
    private async Task<ClientResult> RefreshAsync(HttpContext context)
    {
        if (await FindActivationAsync(context).ConfigureAwait(false) is not ActivationRow found)
        {
            return ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation);
        }

        if (await ReadProvenAsync(context, found).ConfigureAwait(false) is not (byte[] bytes, ProofResult.Valid proof))
        {
            return ClientResult.Answered;
        }

        SequenceRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, Activations.Malformed);
            request = Activations.ReadSequence(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        (PolicyRow policy, ProductRow product) = await PolicyAndProductAsync(found.LicenseId, context.RequestAborted).ConfigureAwait(false);
        DateTimeOffset now = Time.GetUtcNow();
        long at = now.ToUnixTimeSeconds();
        return await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    if (
                        scope is null
                        || await scope.FindActivationAsync(found.Id, context.RequestAborted).ConfigureAwait(false) is not ActivationRow activation
                    )
                    {
                        return ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation);
                    }

                    return Refusal(scope.License, at)
                        ?? await NextTokenAsync(context, scope, policy, product, activation, request.Seq, request.Retry, proof, a => a, "refresh", at)
                            .ConfigureAwait(false);
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);
    }

    // design §12.6, §12.6.1: frees the installation's place; counted against DeactivationLimit. A retry naming a refresh whose
    // answer was lost is the same copy. An activation stopped as a clone is deactivated only with the last sequence number, by
    // the copy that went ahead, or by an administrator: any number would let the other copy free it and activate again
    // (decision 0.88, the design review of M4b1).
    private async Task<ClientResult> DeactivateAsync(HttpContext context)
    {
        if (await FindActivationAsync(context).ConfigureAwait(false) is not ActivationRow found)
        {
            return ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation);
        }

        if (await ReadProvenAsync(context, found).ConfigureAwait(false) is not (byte[] bytes, ProofResult.Valid proof))
        {
            return ClientResult.Answered;
        }

        SequenceRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, Activations.Malformed);
            request = Activations.ReadSequence(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        (PolicyRow policy, _) = await PolicyAndProductAsync(found.LicenseId, context.RequestAborted).ConfigureAwait(false);
        DateTimeOffset now = Time.GetUtcNow();
        long at = now.ToUnixTimeSeconds();
        return await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    if (
                        scope is null
                        || await scope.FindActivationAsync(found.Id, context.RequestAborted).ConfigureAwait(false) is not ActivationRow activation
                    )
                    {
                        return ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation);
                    }

                    switch (Activations.Judge(activation, request.Seq, request.Retry))
                    {
                        case SequenceJudgement.Blocked when request.Seq != activation.Seq:
                            return ClientResult.Fail(
                                ErrorCode.CloneDetected,
                                "The activation was stopped as a clone; only the copy with the last sequence number, or an administrator, deactivates it."
                            );
                        case SequenceJudgement.Clone:
                            if (
                                await CloneAsync(context, scope, policy, activation, request.Seq, proof, "deactivate", at).ConfigureAwait(false)
                                is ClientResult stopped
                            )
                            {
                                return stopped;
                            }

                            break;
                    }

                    LicenseRow license = scope.License;
                    if (policy.DeactivationLimit is long most && license.Deactivations >= most)
                    {
                        return ClientResult.Fail(ErrorCode.DeactivationLimit, $"The license has reached its limit of {most} deactivations.");
                    }

                    license = license with { Deactivations = license.Deactivations + 1, UpdatedAt = at };
                    await scope.DeleteActivationAsync(activation.Id, context.RequestAborted).ConfigureAwait(false);
                    await scope.UpdateLicenseAsync(license, context.RequestAborted).ConfigureAwait(false);
                    string detail = AuditLog.Detail(w =>
                    {
                        w.WriteString("licenseId", license.Id.ToString("D"));
                        w.WriteNumber("deactivations", license.Deactivations);
                    });
                    await scope
                        .AppendAuditAsync(DeviceEntry(context, proof, AuditActions.ActivationDeactivate, activation, detail), context.RequestAborted)
                        .ConfigureAwait(false);
                    return new ClientResult(StatusCodes.Status204NoContent, null, null, activation.Id, null);
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);
    }

    // design §12.9: one trial a product and fingerprint; the trial is a license of the product's trial policy, activated at once.
    private async Task<ClientResult> TrialAsync(HttpContext context)
    {
        if (await ReadProvenAsync(context, null).ConfigureAwait(false) is not (byte[] bytes, ProofResult.Valid proof))
        {
            return ClientResult.Answered;
        }

        TrialRequest request;
        try
        {
            using RequestBody body = RequestBody.Parse(bytes, Activations.TrialMalformed);
            request = Activations.ReadTrial(body);
        }
        catch (BodyException e)
        {
            return ClientResult.Fail(e.Code, e.Message);
        }

        if (request.Fingerprints.Count == 0)
        {
            return ClientResult.Fail(ErrorCode.FingerprintUnavailable, "A trial needs at least one fingerprint (design §12.9).");
        }

        const string NotOffered = "The product does not offer a trial.";
        if (
            await Database.FindProductByCodeAsync(request.Product, context.RequestAborted).ConfigureAwait(false) is not ProductRow product
            || product.TrialPolicyId is not Guid policyId
            || await Database.FindPolicyAsync(policyId, context.RequestAborted).ConfigureAwait(false) is not PolicyRow policy
        )
        {
            return ClientResult.Fail(ErrorCode.TrialNotOffered, NotOffered);
        }

        // design §12.6.1: a trial whose answer was lost is sent again with the first request's jti in retry; the same
        // installation key gets the file it was issued, as a retried activation does (the design review of M4b1).
        if (
            request.Retry.Count > 0
            && await Database.FindTrialActivationAsync(product.Id, proof.KeyId, context.RequestAborted).ConfigureAwait(false) is ActivationRow issued
            && issued.Seq == 1
            && request.Retry.Contains(issued.LastProof, StringComparer.Ordinal)
        )
        {
            return ClientResult.Issued(StatusCodes.Status200OK, issued.Id, issued.LastFile);
        }

        DateTimeOffset now = Time.GetUtcNow();
        long at = now.ToUnixTimeSeconds();

        // Round 3 of the M4b Grill: every feature of the product, without a version range or an expiry of its own.
        IReadOnlyList<LicenseFeature> features =
        [
            .. (await Database.ListFeaturesAsync(product.Id, null, int.MaxValue, context.RequestAborted).ConfigureAwait(false)).Select(
                f => new LicenseFeature(f.Code, null, null, null)
            ),
        ];
        var license = new LicenseRow(
            Guid.CreateVersion7(now),
            product.Id,
            policy.Id,
            "trial",
            null,
            null,
            at + policy.TrialDuration,
            null,
            null,
            Licenses.Store(features),
            Licenses.StoreMeta(new Dictionary<string, string>()),
            0,
            at,
            at
        );
        ClientInfo? client = policy.ClientInfoStrategy == "hostAndUser" ? request.Client : null;
        byte[] file = IssueFile(license, policy, product, proof.KeyId, request.Fingerprints, 1);
        var activation = new ActivationRow(
            Guid.CreateVersion7(now),
            license.Id,
            proof.KeyId,
            proof.Key.Q.X!,
            proof.Key.Q.Y!,
            Activations.Store(request.Fingerprints),
            client?.Host,
            client?.User,
            request.App,
            1,
            file,
            proof.ProofId,
            null,
            at,
            at
        );
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("productId", product.Id.ToString("D"));
            w.WriteString("activationId", activation.Id.ToString("D"));
            w.WriteString("expiresAt", AdminApi.Time(license.ExpiresAt!.Value));
        });
        var entry = new AuditEntry(InstallationActor, proof.KeyId, AuditActions.TrialCreate, license.Id.ToString("D"), "ELM_OK", Ip(context), detail);
        return await Database.CreateTrialAsync(license, activation, request.Fingerprints, entry, now, context.RequestAborted).ConfigureAwait(false)
            ? ClientResult.Issued(StatusCodes.Status201Created, activation.Id, file)
            : ClientResult.Fail(ErrorCode.TrialUsed, "A fingerprint of this installation already had a trial of the product.");
    }

    // design §10.4 step 3 for a refresh or a reactivation, in the license's write transaction.
    private async Task<ClientResult> NextTokenAsync(
        HttpContext context,
        LicenseScope scope,
        PolicyRow policy,
        ProductRow product,
        ActivationRow activation,
        long? seq,
        IReadOnlyList<string> retry,
        ProofResult.Valid proof,
        Func<ActivationRow, ActivationRow> change,
        string operation,
        long at
    )
    {
        bool reactivated = operation == "activate";
        switch (Activations.Judge(activation, seq, retry))
        {
            case SequenceJudgement.Blocked:
                return ClientResult.Fail(ErrorCode.CloneDetected, "The activation was stopped because a clone of the installation was detected.");
            case SequenceJudgement.Retry:
                // The answer to the request that got the last token was lost: the same file again, nothing changes.
                return ClientResult.Issued(StatusCodes.Status200OK, activation.Id, activation.LastFile);
            case SequenceJudgement.Clone:
                if (await CloneAsync(context, scope, policy, activation, seq, proof, operation, at).ConfigureAwait(false) is ClientResult stopped)
                {
                    return stopped;
                }

                break;
        }

        ActivationRow next = change(activation) with { Seq = activation.Seq + 1, LastProof = proof.ProofId, UpdatedAt = at };
        byte[] file = IssueFile(scope.License, policy, product, next.KeyId, Activations.Load(next.Fingerprints), next.Seq);
        next = next with { LastFile = file };
        await scope.UpdateActivationAsync(next, context.RequestAborted).ConfigureAwait(false);
        if (reactivated)
        {
            await scope
                .AppendAuditAsync(
                    DeviceEntry(context, proof, AuditActions.ActivationActivate, next, ActivateDetail(scope.License, next.Seq, true)),
                    context.RequestAborted
                )
                .ConfigureAwait(false);
        }

        return ClientResult.Issued(StatusCodes.Status200OK, next.Id, file);
    }

    // A detected clone (design §10.4): Deny stops the activation and refuses; Flag records it and lets the request go on.
    private static async Task<ClientResult?> CloneAsync(
        HttpContext context,
        LicenseScope scope,
        PolicyRow policy,
        ActivationRow activation,
        long? seq,
        ProofResult.Valid proof,
        string operation,
        long at
    )
    {
        bool deny = policy.CloneStrategy == "deny";
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("licenseId", activation.LicenseId.ToString("D"));
            w.WriteString("operation", operation);
            if (seq is long sent)
            {
                w.WriteNumber("seq", sent);
            }
            else
            {
                w.WriteNull("seq");
            }

            w.WriteNumber("lastSeq", activation.Seq);
            w.WriteString("strategy", policy.CloneStrategy);
        });
        string result = deny ? ErrorCodes.ToWireName(ErrorCode.CloneDetected) : "ELM_OK";
        var entry = new AuditEntry(
            InstallationActor,
            proof.KeyId,
            AuditActions.ActivationClone,
            activation.Id.ToString("D"),
            result,
            Ip(context),
            detail
        );
        await scope.AppendAuditAsync(entry, context.RequestAborted).ConfigureAwait(false);
        if (!deny)
        {
            return null;
        }

        await scope.UpdateActivationAsync(activation with { BlockedAt = at, UpdatedAt = at }, context.RequestAborted).ConfigureAwait(false);
        return ClientResult.Fail(
            ErrorCode.CloneDetected,
            "Another copy of the installation went ahead; the activation is stopped. Deactivate it and activate again."
        );
    }

    // The checks of design §12.2, then the per-key limit (design §14.7) and the jti, recorded in the database after every
    // other check passed (rule 5): the verifier's own store accepts every jti, so the database is the only record.
    private async Task<(byte[] Body, ProofResult.Valid Proof)?> ReadProvenAsync(HttpContext context, ActivationRow? known)
    {
        if (await ReadBodyAsync(context).ConfigureAwait(false) is not byte[] body)
        {
            await ProblemAsync(context, ErrorCode.PayloadTooLarge, "The request body is larger than 64 KiB.").ConfigureAwait(false);
            return null;
        }

        string path = context.Request.Path.Value ?? "/";
        Uri[] targets = [.. _publicUrls.Select(u => new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + path }.Uri)];
        ECParameters? KnownKey(string kid) =>
            known is not null && kid == known.KeyId
                ? new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = known.KeyX, Y = known.KeyY },
                }
                : null;
        ProofResult result = _proofs.Verify(context.Request.Headers[ProofHeader].ToString(), context.Request.Method, targets, body, KnownKey);
        if (result is ProofResult.Invalid invalid)
        {
            await ProblemAsync(context, invalid.Error.Code, invalid.Error.Message).ConfigureAwait(false);
            return null;
        }

        var proof = (ProofResult.Valid)result;
        if (known is not null && proof.KeyId != known.KeyId)
        {
            // design §12.5, T19: only the activation's own installation key refreshes or deactivates it.
            await ProblemAsync(context, ErrorCode.ProofInvalid, "The proof is not made with the activation's installation key.")
                .ConfigureAwait(false);
            return null;
        }

        if (!_clientPerKey.TryAcquire(proof.KeyId))
        {
            await ProblemAsync(context, ErrorCode.RateLimited, "Too many requests with this installation key.").ConfigureAwait(false);
            return null;
        }

        // A minute past the nonce's lifetime, for the clocks of several nodes (design §14.8).
        long keepUntil = (Time.GetUtcNow() + NonceService.Lifetime + ProofSkew).ToUnixTimeSeconds();
        if (!await Database.RecordProofAsync(proof.ProofId, keepUntil, context.RequestAborted).ConfigureAwait(false))
        {
            await ProblemAsync(context, ErrorCode.Replayed, "The request proof was already used.").ConfigureAwait(false);
            return null;
        }

        return (body, proof);
    }

    // design §8.4.2: the token of an activation; its expiry is the earlier of TokenLifetime and the license's (Round 1 of the
    // M4b Grill), and every member follows the policy and the license as they are now (decision 0.68).
    private byte[] IssueFile(LicenseRow license, PolicyRow policy, ProductRow product, string keyId, IReadOnlyList<string> fingerprints, long seq)
    {
        long now = Time.GetUtcNow().ToUnixTimeSeconds();
        long expires = Math.Min(now + policy.TokenLifetime, license.ExpiresAt ?? long.MaxValue);
        FingerprintMatching matching = policy.FingerprintMatching switch
        {
            "all" => FingerprintMatching.All,
            "ignore" => FingerprintMatching.Ignore,
            _ => FingerprintMatching.Any,
        };
        bool server = policy.LicenseKind == Policies.Server;
        var spec = new LicenseSpec
        {
            LicenseId = license.Id.ToString("D"),
            ProductId = product.Code,
            Kind = policy.LicenseKind switch
            {
                Policies.Trial => LicenseKind.Trial,
                Policies.Server => LicenseKind.Server,
                _ => LicenseKind.Standard,
            },
            Features =
            [
                .. Licenses
                    .Load(license.Features)
                    .Select(f => new FeatureSpec(
                        f.Code,
                        f.Ver,
                        f.Seats is long seats ? (int)seats : null,
                        f.ExpiresAt is long e ? DateTimeOffset.FromUnixTimeSeconds(e) : null
                    )),
            ],
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires),
            GracePeriod = TimeSpan.FromSeconds(policy.GracePeriod),
            RefreshWindow = TimeSpan.FromSeconds(policy.RefreshWindow),
            InstallationKeyThumbprint = keyId,
            Fingerprints = matching == FingerprintMatching.Ignore ? [] : fingerprints,
            FingerprintMatching = matching,
            Sequence = seq,
            LeaseDuration = server ? TimeSpan.FromSeconds(policy.LeaseDuration) : null,
            BorrowLimit = server ? TimeSpan.FromSeconds(policy.BorrowLimit ?? 0) : null,
            OveragePercent = server ? (int)(policy.Overage ?? 0) : null,
            BorrowReturnLimit = server ? (int)policy.BorrowReturnLimit : null,
            CloneStrategy = server ? (policy.CloneStrategy == "flag" ? CloneStrategy.Flag : CloneStrategy.Deny) : null,
            Metadata = Licenses.LoadMeta(license.Meta),
        };

        // Checked with the service's own trust store before it is returned, as the CLI does (design §16). The issuer refuses
        // when no certificate covers now (one ran out after the start) or the file does not verify: an internal error.
        try
        {
            return LicenseIssuer.Issue(_signingKey, _signingCertificates, spec, Time, _trust).File;
        }
        catch (ArgumentException e)
        {
            throw new IssueFailedException(e.Message);
        }
    }

    private static ClientResult? Refusal(LicenseRow license, long at) =>
        Licenses.State(license, at) switch
        {
            Licenses.Revoked => ClientResult.Fail(ErrorCode.LicenseRevoked, "The license is revoked."),
            Licenses.Suspended => ClientResult.Fail(ErrorCode.LicenseSuspended, "The license is suspended."),
            Licenses.Expired => ClientResult.Fail(ErrorCode.Expired, "The license has expired."),
            _ => null,
        };

    private async Task<(PolicyRow Policy, ProductRow Product)> PolicyAndProductAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        LicenseRow license = (await Database.FindLicenseAsync(licenseId, cancellationToken).ConfigureAwait(false))!;
        PolicyRow policy = (await Database.FindPolicyAsync(license.PolicyId, cancellationToken).ConfigureAwait(false))!;
        ProductRow product = (await Database.FindProductAsync(license.ProductId, cancellationToken).ConfigureAwait(false))!;
        return (policy, product);
    }

    private async Task<ActivationRow?> FindActivationAsync(HttpContext context) =>
        TryRouteId(context, "id", out Guid id) ? await Database.FindActivationAsync(id, context.RequestAborted).ConfigureAwait(false) : null;

    private static AuditEntry DeviceEntry(HttpContext context, ProofResult.Valid proof, string action, ActivationRow activation, string detail) =>
        new(InstallationActor, proof.KeyId, action, activation.Id.ToString("D"), "ELM_OK", Ip(context), detail);

    private static string ActivateDetail(LicenseRow license, long seq, bool again) =>
        AuditLog.Detail(w =>
        {
            w.WriteString("licenseId", license.Id.ToString("D"));
            w.WriteNumber("seq", seq);
            w.WriteBoolean("reactivated", again);
        });

    private static string? Ip(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    // design §12.4: {activationId, license}, the license file as it was signed.
    private static Task WriteIssuedAsync(HttpContext context, int status, Guid activationId, byte[] file)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("activationId", activationId.ToString("D"));
            w.WritePropertyName("license");
            w.WriteRawValue(file);
            w.WriteEndObject();
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted).AsTask();
    }

    /// <summary>The signing of a license file failed; the request answers <c>ELM_INTERNAL_ERROR</c>.</summary>
    private sealed class IssueFailedException(string message) : Exception(message)
    {
        public const string Answer = "The service cannot sign a license file now.";
    }

    /// <summary>What a device request answers: a problem, a license file, or a status alone; or nothing more, when the
    /// proof check already answered.</summary>
    private sealed record ClientResult(int Status, ErrorCode? Error, string? Message, Guid ActivationId, byte[]? File)
    {
        public static ClientResult Answered { get; } = new(0, null, null, Guid.Empty, null);

        public static ClientResult Fail(ErrorCode code, string message) => new(0, code, message, Guid.Empty, null);

        public static ClientResult Issued(int status, Guid activationId, byte[] file) => new(status, null, null, activationId, file);
    }
}
