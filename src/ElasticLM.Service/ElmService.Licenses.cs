using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The administration API of M4a3 (design §14.5, decisions 0.85 and 0.86): licenses, their keys, the state operations, and
/// the unbound license file. A change reads the license in its write transaction (<c>engineering.mdc</c>).
/// </content>
internal sealed partial class ElmService
{
    private const string NoLicense = "No license has this ID.";
    private static readonly string[] LicenseFixed = ["productId", "policyId"];

    private void MapLicenses()
    {
        void Map(string method, string path, string permission, string action, Func<HttpContext, Caller, Task<AdminOutcome>> operation) =>
            _app.MapMethods(path, [method], (RequestDelegate)(c => AdminAsync(c, permission, action, operation)));

        Map("POST", "/v1/admin/licenses", Permissions.LicenseCreate, AuditActions.LicenseCreate, CreateLicenseAsync);
        Map("GET", "/v1/admin/licenses", Permissions.LicenseRead, AuditActions.LicenseList, ListLicensesAsync);
        Map("GET", "/v1/admin/licenses/{id}", Permissions.LicenseRead, AuditActions.LicenseGet, GetLicenseAsync);
        Map("PATCH", "/v1/admin/licenses/{id}", Permissions.LicenseUpdate, AuditActions.LicenseUpdate, UpdateLicenseAsync);
        Map(
            "POST",
            "/v1/admin/licenses/{id}/suspend",
            Permissions.LicenseUpdate,
            AuditActions.LicenseSuspend,
            (c, k) => ChangeStateAsync(c, k, AuditActions.LicenseSuspend)
        );
        Map(
            "POST",
            "/v1/admin/licenses/{id}/resume",
            Permissions.LicenseUpdate,
            AuditActions.LicenseResume,
            (c, k) => ChangeStateAsync(c, k, AuditActions.LicenseResume)
        );
        Map(
            "POST",
            "/v1/admin/licenses/{id}/revoke",
            Permissions.LicenseRevoke,
            AuditActions.LicenseRevoke,
            (c, k) => ChangeStateAsync(c, k, AuditActions.LicenseRevoke)
        );
        Map(
            "POST",
            "/v1/admin/licenses/{id}/extend",
            Permissions.LicenseUpdate,
            AuditActions.LicenseExtend,
            (c, k) => ChangeStateAsync(c, k, AuditActions.LicenseExtend)
        );
        Map("POST", "/v1/admin/licenses/{id}/file", Permissions.LicenseFile, AuditActions.LicenseFile, LicenseFileAsync);
    }

    private Task<AdminOutcome> CreateLicenseAsync(HttpContext context, Caller caller) =>
        WithBodyAsync(
            context,
            Licenses.Malformed,
            async body =>
            {
                string policyText = body.RequiredText("policyId");
                PolicyRow policy =
                    Guid.TryParseExact(policyText, "D", out Guid policyId)
                    && await Database.FindPolicyAsync(policyId, context.RequestAborted).ConfigureAwait(false) is PolicyRow found
                        ? found
                        : throw new BodyException(ErrorCode.InvalidArgument, "policyId must name a policy.");
                string name = CheckLicenseName(body.RequiredText("name"));
                DateTimeOffset now = Time.GetUtcNow();
                long at = now.ToUnixTimeSeconds();

                // design §14.5: fromIssue starts at creation, overridden by expiresAt; fromFirstActivation waits for M4b.
                long? expires;
                if (body.TryGet("expiresAt", out JsonElement expiresValue) && expiresValue.ValueKind != JsonValueKind.Null)
                {
                    expires =
                        policy.ExpirationBasis == Policies.FromFirstActivation
                            ? throw new BodyException(
                                ErrorCode.InvalidArgument,
                                "expiresAt cannot be set for a policy whose expiry starts at the first activation."
                            )
                        : Licenses.Time(body, expiresValue) is long sent && sent > at ? sent
                        : throw new BodyException(ErrorCode.InvalidArgument, "expiresAt must be later than now.");
                }
                else
                {
                    expires = policy.ExpirationBasis == Policies.FromIssue && policy.Duration is long duration ? at + duration : null;
                }

                IReadOnlySet<string> codes = await FeatureCodesAsync(policy.ProductId, context.RequestAborted).ConfigureAwait(false);
                IReadOnlyList<LicenseFeature> features = body.TryGet("features", out JsonElement featuresValue)
                    ? Licenses.Features(body, featuresValue, policy, codes, expires, at)
                    : throw new BodyException(ErrorCode.Malformed, Licenses.Malformed);
                long? maxActivations = body.TryGet("maxActivations", out JsonElement maxValue) ? Licenses.MaxActivationsOf(body, maxValue) : null;
                Dictionary<string, string> meta = body.TryGet("meta", out JsonElement metaValue) ? Licenses.Meta(body, metaValue) : [];

                string key = LicenseKeys.NewKey();
                var license = new LicenseRow(
                    Guid.CreateVersion7(now),
                    policy.ProductId,
                    policy.Id,
                    name,
                    LicenseKeys.HashOf(key),
                    maxActivations,
                    expires,
                    null,
                    null,
                    Licenses.Store(features),
                    Licenses.StoreMeta(meta),
                    0,
                    at,
                    at
                );

                // The key is never in the audit row (design §9.5).
                string detail = AuditLog.Detail(w =>
                {
                    w.WriteString("policyId", policy.Id.ToString("D"));
                    w.WriteString("name", name);
                    WriteFeatures(w, features);
                    if (body.Has("maxActivations"))
                    {
                        WriteNumber(w, "maxActivations", maxActivations);
                    }

                    if (body.Has("expiresAt"))
                    {
                        w.WriteString("expiresAt", expires is long e ? AdminApi.Time(e) : null);
                    }

                    if (body.Has("meta"))
                    {
                        WriteMeta(w, meta);
                    }
                });
                AuditEntry entry = Entry(context, caller, AuditActions.LicenseCreate, license.Id, detail);
                await Database.CreateLicenseAsync(license, entry, now, context.RequestAborted).ConfigureAwait(false);
                LicenseItem item = ToItem(license, at) with { Key = key };
                await WriteJsonAsync(context, StatusCodes.Status201Created, item, ServiceJsonContext.Default.LicenseItem).ConfigureAwait(false);
                return new AdminOutcome("ELM_OK", license.Id.ToString("D"), detail, Recorded: true);
            }
        );

    private async Task<AdminOutcome> ListLicensesAsync(HttpContext context, Caller caller)
    {
        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument));
        }

        Guid? productId = null;
        Guid? policyId = null;
        string? state = null;
        foreach ((string name, string what) in new[] { ("productId", "product"), ("policyId", "policy") })
        {
            if (context.Request.Query[name] is not { Count: > 0 } text)
            {
                continue;
            }

            if (text.Count != 1 || !Guid.TryParseExact(text[0], "D", out Guid parsed))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, $"{name} must be a {what} ID.").ConfigureAwait(false);
            }

            if (name == "productId")
            {
                productId = parsed;
            }
            else
            {
                policyId = parsed;
            }
        }

        if (context.Request.Query["state"] is { Count: > 0 } stateText)
        {
            if (stateText.Count != 1 || !Licenses.States.Contains(stateText[0], StringComparer.Ordinal))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "state must be one of active, suspended, expired, revoked.")
                    .ConfigureAwait(false);
            }

            state = stateText[0];
        }

        long now = Time.GetUtcNow().ToUnixTimeSeconds();
        IReadOnlyList<LicenseRow> rows = await Database
            .ListLicensesAsync(productId, policyId, state, now, after, limit + 1, context.RequestAborted)
            .ConfigureAwait(false);
        LicenseRow[] page = [.. rows.Take(limit)];
        var answer = new LicensePage([.. page.Select(r => ToItem(r, now))], rows.Count > limit ? page[^1].Id.ToString("D") : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.LicensePage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    private async Task<AdminOutcome> GetLicenseAsync(HttpContext context, Caller caller)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow license)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        LicenseItem item = ToItem(license, Time.GetUtcNow().ToUnixTimeSeconds());
        await WriteJsonAsync(context, StatusCodes.Status200OK, item, ServiceJsonContext.Default.LicenseItem).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", license.Id.ToString("D"));
    }

    private async Task<AdminOutcome> UpdateLicenseAsync(HttpContext context, Caller caller)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow found)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                Licenses.Malformed,
                async body =>
                {
                    RefuseFixed(body, LicenseFixed);
                    if (body.Has("expiresAt"))
                    {
                        // The expiry is changed by extend, which checks it against the state (design §14.5).
                        throw new BodyException(ErrorCode.InvalidArgument, "expiresAt is changed by extend.");
                    }

                    string? name = body.TryGet("name", out JsonElement nameValue)
                        ? CheckLicenseName(body.Text(nameValue) ?? throw new BodyException(ErrorCode.Malformed, Licenses.Malformed))
                        : null;
                    bool setMax = body.TryGet("maxActivations", out JsonElement maxValue);
                    long? maxActivations = setMax ? Licenses.MaxActivationsOf(body, maxValue) : null;
                    Dictionary<string, string>? meta = body.TryGet("meta", out JsonElement metaValue) ? Licenses.Meta(body, metaValue) : null;
                    bool setFeatures = body.TryGet("features", out JsonElement featuresValue);

                    // The policy's kind and expiry basis and the product's feature codes do not change under a license (decision
                    // 0.68, 0.83, 0.87), so they are read before the transaction; the license itself is read inside it.
                    PolicyRow policy = (await Database.FindPolicyAsync(found.PolicyId, context.RequestAborted).ConfigureAwait(false))!;
                    IReadOnlySet<string> codes = await FeatureCodesAsync(found.ProductId, context.RequestAborted).ConfigureAwait(false);
                    DateTimeOffset now = Time.GetUtcNow();
                    long at = now.ToUnixTimeSeconds();
                    LicenseRow Change(LicenseRow row) =>
                        row with
                        {
                            Name = name ?? row.Name,
                            MaxActivations = setMax ? maxActivations : row.MaxActivations,
                            Meta = meta is null ? row.Meta : Licenses.StoreMeta(meta),
                            Features = setFeatures
                                ? Licenses.Store(Licenses.Features(body, featuresValue, policy, codes, row.ExpiresAt, at))
                                : row.Features,
                            UpdatedAt = at,
                        };

                    string detail = string.Empty;
                    LicenseRow? changed = await Database
                        .UpdateLicenseAsync(
                            found.Id,
                            Change,
                            (before, after) =>
                            {
                                detail = ChangeDetail(before, after);
                                return Entry(context, caller, AuditActions.LicenseUpdate, found.Id, detail);
                            },
                            now,
                            context.RequestAborted
                        )
                        .ConfigureAwait(false);
                    if (changed is null)
                    {
                        return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, found.Id.ToString("D")).ConfigureAwait(false);
                    }

                    await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(changed, at), ServiceJsonContext.Default.LicenseItem)
                        .ConfigureAwait(false);
                    return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
                }
            )
            .ConfigureAwait(false);
    }

    // suspend, resume, revoke, extend (design §9.4, §14.5): the state is decided on the row read in the write transaction.
    private async Task<AdminOutcome> ChangeStateAsync(HttpContext context, Caller caller, string action)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow found)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        // design §14.5: only extend has a body; suspend, resume, and revoke read none, so a POST without one works.
        if (action != AuditActions.LicenseExtend)
        {
            return await ApplyStateAsync(context, caller, action, found, null).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                Licenses.Malformed,
                async body =>
                {
                    long to = body.TryGet("expiresAt", out JsonElement value)
                        ? Licenses.Time(body, value)
                        : throw new BodyException(ErrorCode.Malformed, Licenses.Malformed);
                    return await ApplyStateAsync(context, caller, action, found, to).ConfigureAwait(false);
                }
            )
            .ConfigureAwait(false);
    }

    private async Task<AdminOutcome> ApplyStateAsync(HttpContext context, Caller caller, string action, LicenseRow found, long? extendTo)
    {
        DateTimeOffset now = Time.GetUtcNow();
        long at = now.ToUnixTimeSeconds();
        LicenseRow Change(LicenseRow row)
        {
            string state = Licenses.State(row, at);
            return action switch
            {
                AuditActions.LicenseSuspend => state switch
                {
                    Licenses.Revoked => throw Conflict("A revoked license cannot be suspended."),
                    Licenses.Suspended => row,
                    _ => row with { SuspendedAt = at, UpdatedAt = at },
                },
                AuditActions.LicenseResume => state switch
                {
                    Licenses.Revoked => throw Conflict("A revoked license cannot be resumed."),
                    Licenses.Suspended => row with { SuspendedAt = null, UpdatedAt = at },
                    _ => row,
                },
                AuditActions.LicenseRevoke => state == Licenses.Revoked ? row : row with { RevokedAt = at, UpdatedAt = at },
                _ => Extend(row, state, extendTo!.Value, at),
            };
        }

        try
        {
            string detail = string.Empty;
            LicenseRow? changed = await Database
                .UpdateLicenseAsync(
                    found.Id,
                    Change,
                    (before, after) =>
                    {
                        detail = ChangeDetail(before, after);
                        return Entry(context, caller, action, found.Id, detail);
                    },
                    now,
                    context.RequestAborted
                )
                .ConfigureAwait(false);
            if (changed is null)
            {
                return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, found.Id.ToString("D")).ConfigureAwait(false);
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(changed, at), ServiceJsonContext.Default.LicenseItem).ConfigureAwait(false);
            return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
        }
        catch (BodyException e)
        {
            // A transition the §9.4 diagram does not allow (409), or an extension that is too early (400); nothing was written.
            return await RefuseAsync(context, e.Code, e.Message, found.Id.ToString("D")).ConfigureAwait(false);
        }
    }

    // design §8.5, §14.5, decisions 0.76 and 0.86: the file of an active license of an unbound policy; its token ends when the
    // license does, and has no expiry when the license has none.
    private async Task<AdminOutcome> LicenseFileAsync(HttpContext context, Caller caller)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow license)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        string target = license.Id.ToString("D");
        PolicyRow policy = (await Database.FindPolicyAsync(license.PolicyId, context.RequestAborted).ConfigureAwait(false))!;
        if (policy.Binding != Policies.Unbound)
        {
            return await RefuseAsync(context, ErrorCode.Conflict, "Only a license of an unbound policy has a license file.", target)
                .ConfigureAwait(false);
        }

        string state = Licenses.State(license, Time.GetUtcNow().ToUnixTimeSeconds());
        if (state != Licenses.Active)
        {
            return await RefuseAsync(
                    context,
                    ErrorCode.Conflict,
                    $"{(state == Licenses.Expired ? "An" : "A")} {state} license has no license file.",
                    target
                )
                .ConfigureAwait(false);
        }

        ProductRow product = (await Database.FindProductAsync(license.ProductId, context.RequestAborted).ConfigureAwait(false))!;
        var spec = new LicenseSpec
        {
            LicenseId = target,
            ProductId = product.Code,
            Kind = policy.LicenseKind == Policies.Trial ? LicenseKind.Trial : LicenseKind.Standard,
            Features =
            [
                .. Licenses
                    .Load(license.Features)
                    .Select(f => new FeatureSpec(f.Code, f.Ver, null, f.ExpiresAt is long e ? DateTimeOffset.FromUnixTimeSeconds(e) : null)),
            ],
            ExpiresAt = license.ExpiresAt is long end ? DateTimeOffset.FromUnixTimeSeconds(end) : null,

            // The issuer writes grace only with an expiry (design §8.4.2), so a license without expiry gets none.
            GracePeriod = TimeSpan.FromSeconds(policy.GracePeriod),
            Metadata = Licenses.LoadMeta(license.Meta),
        };

        // Checked with the service's own trust store before it is returned, as the CLI does (design §16).
        IssuedLicense issued = LicenseIssuer.Issue(_signingKey, _signingCertificates, spec, Time, _trust);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{target}.elmlic\"";
        await context.Response.Body.WriteAsync(issued.File, context.RequestAborted).ConfigureAwait(false);
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("jti", issued.TokenId);
            w.WriteString("exp", license.ExpiresAt is long e ? AdminApi.Time(e) : null);
        });
        return new AdminOutcome("ELM_OK", target, detail);
    }

    private static LicenseRow Extend(LicenseRow row, string state, long to, long now)
    {
        if (state is Licenses.Revoked or Licenses.Suspended)
        {
            throw Conflict($"A {state} license cannot be extended.");
        }

        if (row.ExpiresAt is not long current)
        {
            throw Conflict("The license has no expiry to extend.");
        }

        return to > Math.Max(current, now)
            ? row with
            {
                ExpiresAt = to,
                UpdatedAt = now,
            }
            : throw new BodyException(ErrorCode.InvalidArgument, "expiresAt must be later than the license's expiry and now.");
    }

    private static BodyException Conflict(string message) => new(ErrorCode.Conflict, message);

    // The members a change wrote, with their new values; an operation that changed nothing says so (design §14.5).
    private static string ChangeDetail(LicenseRow before, LicenseRow after) =>
        AuditLog.Detail(w =>
        {
            bool changed = false;
            void Time(string name, long? was, long? now)
            {
                if (was != now)
                {
                    w.WriteString(name, now is long t ? AdminApi.Time(t) : null);
                    changed = true;
                }
            }

            if (after.Name != before.Name)
            {
                w.WriteString("name", after.Name);
                changed = true;
            }

            if (after.MaxActivations != before.MaxActivations)
            {
                WriteNumber(w, "maxActivations", after.MaxActivations);
                changed = true;
            }

            if (after.Features != before.Features)
            {
                WriteFeatures(w, Licenses.Load(after.Features));
                changed = true;
            }

            if (after.Meta != before.Meta)
            {
                WriteMeta(w, Licenses.LoadMeta(after.Meta));
                changed = true;
            }

            Time("expiresAt", before.ExpiresAt, after.ExpiresAt);
            Time("suspendedAt", before.SuspendedAt, after.SuspendedAt);
            Time("revokedAt", before.RevokedAt, after.RevokedAt);
            if (!changed)
            {
                w.WriteBoolean("unchanged", true);
            }
        });

    private static void WriteFeatures(Utf8JsonWriter w, IReadOnlyList<LicenseFeature> features)
    {
        w.WriteStartArray("features");
        foreach (LicenseFeature f in features)
        {
            w.WriteStartObject();
            w.WriteString("code", f.Code);
            w.WriteString("ver", f.Ver);
            WriteNumber(w, "seats", f.Seats);
            w.WriteString("expiresAt", f.ExpiresAt is long e ? AdminApi.Time(e) : null);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteMeta(Utf8JsonWriter w, IReadOnlyDictionary<string, string> meta)
    {
        w.WriteStartObject("meta");
        foreach ((string key, string value) in meta)
        {
            w.WriteString(key, value);
        }

        w.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, long? value)
    {
        if (value is long number)
        {
            w.WriteNumber(name, number);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private async Task<LicenseRow?> FindLicenseAsync(HttpContext context) =>
        TryRouteId(context, "id", out Guid id) ? await Database.FindLicenseAsync(id, context.RequestAborted).ConfigureAwait(false) : null;

    private async Task<IReadOnlySet<string>> FeatureCodesAsync(Guid productId, CancellationToken cancellationToken) =>
        (await Database.ListFeaturesAsync(productId, null, int.MaxValue, cancellationToken).ConfigureAwait(false))
            .Select(f => f.Code)
            .ToHashSet(StringComparer.Ordinal);

    private static string CheckLicenseName(string name) =>
        name.EnumerateRunes().Count() is >= 1 and <= Licenses.MaxNameLength
            ? name
            : throw new BodyException(ErrorCode.InvalidArgument, "name must be 1 to 256 characters.");

    private static LicenseItem ToItem(LicenseRow l, long now) =>
        new(
            l.Id.ToString("D"),
            l.ProductId.ToString("D"),
            l.PolicyId.ToString("D"),
            l.Name,
            [
                .. Licenses
                    .Load(l.Features)
                    .Select(f => new LicenseFeatureItem(f.Code, f.Ver, f.Seats, f.ExpiresAt is long e ? AdminApi.Time(e) : null)),
            ],
            l.MaxActivations,
            Licenses.State(l, now),
            AdminApi.Time(l.CreatedAt),
            l.ExpiresAt is long expires ? AdminApi.Time(expires) : null,
            l.SuspendedAt is long suspended ? AdminApi.Time(suspended) : null,
            l.RevokedAt is long revoked ? AdminApi.Time(revoked) : null,
            Licenses.LoadMeta(l.Meta),
            l.Deactivations
        );
}
