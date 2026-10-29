using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// Offline requests (design §12.8, §12.8.4, M4c1): activation, refresh, and deactivation files carried by people, through
/// the public <c>POST /v1/offline</c> and the administration API's <c>POST /v1/admin/offline</c>. Both run the online
/// handlers' checks; the same request gets the answer it got before, and another request with its nonce is refused.
/// </content>
internal sealed partial class ElmService
{
    private const string OfflineChannel = "offline";

    private void MapOffline()
    {
        _app.MapPost("/v1/offline", (RequestDelegate)PublicOfflineAsync);
        _app.MapPost(
            "/v1/admin/offline",
            (RequestDelegate)(c => AdminAsync(c, Permissions.ActivationOffline, AuditActions.ActivationOffline, AdminOfflineAsync))
        );
    }

    private async Task PublicOfflineAsync(HttpContext context)
    {
        try
        {
            (ClientResult result, _, _) = await ProcessOfflineAsync(context, null).ConfigureAwait(false);
            await WriteOfflineAsync(context, result).ConfigureAwait(false);
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            if (!context.Response.HasStarted)
            {
                await ProblemAsync(context, ErrorCode.InternalError, "The database failed.").ConfigureAwait(false);
            }
        }
    }

    // design §12.8.4: every request through the administration API is audited, refused ones too (design §14.5, §18.2).
    private async Task<AdminOutcome> AdminOfflineAsync(HttpContext context, Caller caller)
    {
        (ClientResult result, string? operation, Guid? target) = await ProcessOfflineAsync(context, caller).ConfigureAwait(false);
        await WriteOfflineAsync(context, result).ConfigureAwait(false);
        string detail = AuditLog.Detail(w =>
        {
            if (operation is null)
            {
                w.WriteNull("operation");
            }
            else
            {
                w.WriteString("operation", operation);
            }

            w.WriteString("channel", OfflineChannel);
        });
        string code = result.Error is ErrorCode error ? ErrorCodes.ToWireName(error) : "ELM_OK";
        Guid? id = result.ActivationId != Guid.Empty ? result.ActivationId : target;
        return new AdminOutcome(code, id?.ToString("D"), detail);
    }

    // A file's answer: an offline file is not a request proof, so a bad signature is 400 and a reused nonce 409 (design
    // §12.8.4, §19). No ELM-Nonce header: the request carries its own nonce.
    private static Task WriteOfflineAsync(HttpContext context, ClientResult result)
    {
        if (result.Error is ErrorCode code)
        {
            int? status = code switch
            {
                ErrorCode.ProofInvalid => StatusCodes.Status400BadRequest,
                ErrorCode.Replayed => StatusCodes.Status409Conflict,
                _ => null,
            };
            return ProblemAsync(context, code, result.Message!, status);
        }

        if (result.Answer is byte[] stored)
        {
            return WriteIssuedAsync(context, result.Status, stored);
        }

        if (result.File is byte[] file)
        {
            return WriteIssuedAsync(context, result.Status, IssuedBody(result.ActivationId, file));
        }

        context.Response.StatusCode = result.Status;
        return Task.CompletedTask;
    }

    // design §12.8.4: the common checks, then those of the file's type. Returns the operation and the activation the file
    // names, for the administration API's audit row.
    private async Task<(ClientResult Result, string? Operation, Guid? Target)> ProcessOfflineAsync(HttpContext context, Caller? admin)
    {
        if (await ReadBodyAsync(context).ConfigureAwait(false) is not byte[] body)
        {
            return (ClientResult.Fail(ErrorCode.PayloadTooLarge, "The request body is larger than 64 KiB."), null, null);
        }

        if (!OfflineRequestFile.TryRead(Encoding.UTF8.GetString(body).Trim(), out OfflineRequestFile? file, out LicenseError? error))
        {
            return (ClientResult.Fail(error!.Code, error.Message), null, null);
        }

        try
        {
            return file!.IsDeactivation
                ? await OfflineDeactivateAsync(context, admin, file).ConfigureAwait(false)
                : await OfflineActivateAsync(context, admin, file).ConfigureAwait(false);
        }
        catch (IssueFailedException e)
        {
            // Thrown inside the write transaction, so nothing of the request was written.
            ServiceLog.IssueFailed(_log, e.Message);
            return (ClientResult.Fail(ErrorCode.InternalError, IssueFailedException.Answer), null, null);
        }
    }

    // design §12.8.4 steps 4 to 11 of an elm-actreq+jws (its signature was verified with its jwk when it was read).
    private async Task<(ClientResult Result, string? Operation, Guid? Target)> OfflineActivateAsync(
        HttpContext context,
        Caller? admin,
        OfflineRequestFile file
    )
    {
        OfflineActivation request;
        try
        {
            request = OfflineRequests.ReadActivation(file.Payload);
        }
        catch (BodyException e)
        {
            return (ClientResult.Fail(e.Code, e.Message), null, null);
        }

        string operation = request.Refresh ? "refresh" : "activate";
        if (TimeRefusal(request.IssuedAt, request.ExpiresAt) is ClientResult late)
        {
            return (late, operation, request.ActivationId);
        }

        if (admin is null && !_clientPerKey.TryAcquire(file.KeyId))
        {
            return (ClientResult.Fail(ErrorCode.RateLimited, "Too many requests with this installation key."), operation, request.ActivationId);
        }

        Device device = OfflineDevice(context, admin, file.KeyId, file.Key!.Value, request.Nonce);
        var offline = new OfflineContext(request.Nonce, file.SigningInputHash(), request.ExpiresAt);
        if (!request.Refresh)
        {
            var activation = new ActivationRequest(
                request.LicenseKey!,
                request.Product!,
                request.Fingerprints,
                request.Seq,
                request.Retry,
                null,
                null
            );
            return (await ActivateCoreAsync(device, activation, offline).ConfigureAwait(false), operation, null);
        }

        Guid id = request.ActivationId!.Value;
        if (await Database.FindActivationAsync(id, context.RequestAborted).ConfigureAwait(false) is not ActivationRow found)
        {
            return (ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation), operation, id);
        }

        if (found.KeyId != file.KeyId)
        {
            // design §12.5, T19: only the activation's own installation key refreshes it.
            return (ClientResult.Fail(ErrorCode.ProofInvalid, "The request is not made with the activation's installation key."), operation, id);
        }

        var sequence = new SequenceRequest(request.Seq!.Value, request.Retry);
        return (await RefreshCoreAsync(device, found, sequence, offline).ConfigureAwait(false), operation, id);
    }

    // design §12.8.4 deactivation steps 4 to 8: the activation named by act gives the key that checks the signature (design
    // §8.2 rule 6, the exception for elm-deact+jws); no other claim is used before.
    private async Task<(ClientResult Result, string? Operation, Guid? Target)> OfflineDeactivateAsync(
        HttpContext context,
        Caller? admin,
        OfflineRequestFile file
    )
    {
        const string Operation = "deactivate";
        Guid id;
        try
        {
            id = OfflineRequests.ReadDeactivationTarget(file.Payload);
        }
        catch (BodyException e)
        {
            return (ClientResult.Fail(e.Code, e.Message), Operation, null);
        }

        if (await Database.FindActivationAsync(id, context.RequestAborted).ConfigureAwait(false) is not ActivationRow found)
        {
            return (ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation), Operation, id);
        }

        ECParameters key = KeyOf(found);
        if (found.KeyId != file.KeyId || !file.Verify(key))
        {
            return (ClientResult.Fail(ErrorCode.ProofInvalid, "The proof is not signed with the activation's installation key."), Operation, id);
        }

        OfflineDeactivation proof;
        try
        {
            proof = OfflineRequests.ReadDeactivation(file.Payload);
        }
        catch (BodyException e)
        {
            return (ClientResult.Fail(e.Code, e.Message), Operation, id);
        }

        if (TimeRefusal(proof.IssuedAt, null) is ClientResult late)
        {
            return (late, Operation, id);
        }

        if (admin is null && !_clientPerKey.TryAcquire(file.KeyId))
        {
            return (ClientResult.Fail(ErrorCode.RateLimited, "Too many requests with this installation key."), Operation, id);
        }

        Device device = OfflineDevice(context, admin, file.KeyId, key, string.Empty);
        return (await DeactivateCoreAsync(device, found, new SequenceRequest(proof.Seq, proof.Retry)).ConfigureAwait(false), Operation, id);
    }

    // design §12.8.4 step 6: a request made more than the skew ahead of the service's clock, or past its expiry.
    private ClientResult? TimeRefusal(long issuedAt, long? expiresAt)
    {
        DateTimeOffset now = Time.GetUtcNow();
        if (issuedAt > (now + OfflineRequests.FutureSkew).ToUnixTimeSeconds())
        {
            return ClientResult.Fail(ErrorCode.Malformed, "The request is issued in the future.");
        }

        return expiresAt is long expires && now.ToUnixTimeSeconds() >= expires
            ? ClientResult.Fail(ErrorCode.RequestExpired, "The request has expired; make a new one.")
            : null;
    }

    private static Device OfflineDevice(HttpContext context, Caller? admin, string keyId, ECParameters key, string nonce) =>
        admin is null
            ? new(keyId, key, nonce, InstallationActor, keyId, Ip(context), OfflineChannel, context.RequestAborted)
            : new(keyId, key, nonce, admin.ActorType, admin.ActorId, Ip(context), OfflineChannel, context.RequestAborted);

    private static ECParameters KeyOf(ActivationRow activation) =>
        new()
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = activation.KeyX, Y = activation.KeyY },
        };

    // design §12.8.4 steps 9 and 11, in the license's write transaction: the answer kept for the same request is sent again
    // when the license and the activation still allow it; another request with the key and nonce is refused; a new answer
    // replaces the activation's kept one.
    private static async Task<ClientResult> OfflineAwareAsync(
        LicenseScope scope,
        Device device,
        OfflineContext? offline,
        long at,
        Func<Task<ClientResult>> work
    )
    {
        if (offline is null)
        {
            return await work().ConfigureAwait(false);
        }

        IReadOnlyList<OfflineRequestRow> kept = await scope
            .FindOfflineRequestsAsync(device.KeyId, offline.Nonce, device.Aborted)
            .ConfigureAwait(false);
        if (kept.Count > 0)
        {
            if (kept.Any(r => !CryptographicOperations.FixedTimeEquals(r.RequestHash, offline.RequestHash)))
            {
                return ClientResult.Fail(ErrorCode.Replayed, "Another request was made with this request's nonce.");
            }

            OfflineRequestRow row = kept[0];
            if (Refusal(scope.License, at) is ClientResult refused)
            {
                return refused;
            }

            if (await scope.FindActivationAsync(row.ActivationId, device.Aborted).ConfigureAwait(false) is not ActivationRow activation)
            {
                return ClientResult.Fail(ErrorCode.ActivationNotFound, NoActivation);
            }

            return activation.BlockedAt is not null
                ? ClientResult.Fail(ErrorCode.CloneDetected, "The activation was stopped because a clone of the installation was detected.")
                : ClientResult.Stored(row.ActivationId, row.Response);
        }

        ClientResult result = await work().ConfigureAwait(false);
        if (result.Error is null && result.File is byte[] file)
        {
            var row = new OfflineRequestRow(
                result.ActivationId,
                device.KeyId,
                offline.Nonce,
                offline.RequestHash,
                scope.License.Id,
                IssuedBody(result.ActivationId, file),
                offline.ExpiresAt
            );
            await scope.ReplaceOfflineRequestAsync(row, device.Aborted).ConfigureAwait(false);
        }

        return result;
    }
}
