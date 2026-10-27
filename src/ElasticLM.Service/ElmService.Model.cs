using System.Text.Json;
using System.Text.RegularExpressions;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The administration API of M4a2 (design §14.5, decisions 0.82 to 0.84 and 0.87): products, their features, policies, and
/// the signing key's status. Each change is written with its audit row in one transaction.
/// </content>
internal sealed partial class ElmService
{
    private const int MaxNameLength = 64;
    private const string ProductMalformed = "The request body is not a valid product.";
    private const string FeatureMalformed = "The request body is not a valid feature.";
    private const string NoProduct = "No product has this ID.";
    private const string NoFeature = "No feature has this ID.";
    private const string NoPolicy = "No policy has this ID.";
    private const string CodeRule = "code must be 1 to 64 lowercase letters, digits, or '-'.";
    private const string NameRule = "name must be 1 to 64 characters.";
    private static readonly TimeSpan SigningKeyWarning = TimeSpan.FromDays(30);

    // The members a PATCH cannot change: a code never changes (design §9.1), nor the product a feature belongs to.
    private static readonly string[] ProductFixed = ["code"];
    private static readonly string[] FeatureFixed = ["code", "productId"];

    private void MapModel()
    {
        void Map(string method, string path, string permission, string action, Func<HttpContext, Caller, Task<AdminOutcome>> operation) =>
            _app.MapMethods(path, [method], (RequestDelegate)(c => AdminAsync(c, permission, action, operation)));

        Map("POST", "/v1/admin/products", Permissions.ProductWrite, AuditActions.ProductCreate, CreateProductAsync);
        Map("GET", "/v1/admin/products", Permissions.ProductRead, AuditActions.ProductList, ListProductsAsync);
        Map("GET", "/v1/admin/products/{id}", Permissions.ProductRead, AuditActions.ProductGet, GetProductAsync);
        Map("PATCH", "/v1/admin/products/{id}", Permissions.ProductWrite, AuditActions.ProductUpdate, UpdateProductAsync);
        Map("POST", "/v1/admin/products/{id}/features", Permissions.ProductWrite, AuditActions.FeatureCreate, CreateFeatureAsync);
        Map("GET", "/v1/admin/products/{id}/features", Permissions.ProductRead, AuditActions.FeatureList, ListFeaturesAsync);
        Map("PATCH", "/v1/admin/products/{id}/features/{featureId}", Permissions.ProductWrite, AuditActions.FeatureUpdate, UpdateFeatureAsync);
        Map("POST", "/v1/admin/policies", Permissions.PolicyWrite, AuditActions.PolicyCreate, CreatePolicyAsync);
        Map("GET", "/v1/admin/policies", Permissions.PolicyRead, AuditActions.PolicyList, ListPoliciesAsync);
        Map("GET", "/v1/admin/policies/{id}", Permissions.PolicyRead, AuditActions.PolicyGet, GetPolicyAsync);
        Map("PATCH", "/v1/admin/policies/{id}", Permissions.PolicyWrite, AuditActions.PolicyUpdate, UpdatePolicyAsync);
        Map("GET", "/v1/admin/signing-key", Permissions.SigningKeyRead, AuditActions.SigningKeyGet, GetSigningKeyAsync);
    }

    private Task<AdminOutcome> CreateProductAsync(HttpContext context, Caller caller) =>
        WithBodyAsync(
            context,
            ProductMalformed,
            async body =>
            {
                string code = CheckCode(body.RequiredText("code"));
                string name = CheckName(body.RequiredText("name"));
                DateTimeOffset now = Time.GetUtcNow();
                var product = new ProductRow(Guid.CreateVersion7(now), code, name, null, now.ToUnixTimeSeconds());
                string detail = AuditLog.Detail(w =>
                {
                    w.WriteString("code", code);
                    w.WriteString("name", name);
                });
                AuditEntry entry = Entry(context, caller, AuditActions.ProductCreate, product.Id, detail);
                if (!await Database.CreateProductAsync(product, entry, now, context.RequestAborted).ConfigureAwait(false))
                {
                    return await RefuseAsync(context, ErrorCode.Conflict, $"The product code '{code}' is in use.").ConfigureAwait(false);
                }

                await WriteJsonAsync(context, StatusCodes.Status201Created, ToItem(product), ServiceJsonContext.Default.ProductItem)
                    .ConfigureAwait(false);
                return new AdminOutcome("ELM_OK", product.Id.ToString("D"), detail, Recorded: true);
            }
        );

    private async Task<AdminOutcome> ListProductsAsync(HttpContext context, Caller caller)
    {
        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument));
        }

        IReadOnlyList<ProductRow> rows = await Database.ListProductsAsync(after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        ProductRow[] page = [.. rows.Take(limit)];
        var answer = new ProductPage([.. page.Select(ToItem)], rows.Count > limit ? page[^1].Id.ToString("D") : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.ProductPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    private async Task<AdminOutcome> GetProductAsync(HttpContext context, Caller caller)
    {
        if (await FindProductAsync(context).ConfigureAwait(false) is not ProductRow product)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoProduct, RouteText(context, "id")).ConfigureAwait(false);
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(product), ServiceJsonContext.Default.ProductItem).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", product.Id.ToString("D"));
    }

    private async Task<AdminOutcome> UpdateProductAsync(HttpContext context, Caller caller)
    {
        if (await FindProductAsync(context).ConfigureAwait(false) is not ProductRow found)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoProduct, RouteText(context, "id")).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                ProductMalformed,
                async body =>
                {
                    RefuseFixed(body, ProductFixed);
                    string? name = body.TryGet("name", out JsonElement nameValue)
                        ? CheckName(body.Text(nameValue) ?? throw new BodyException(ErrorCode.Malformed, ProductMalformed))
                        : null;
                    bool setTrial = body.TryGet("trialPolicyId", out JsonElement trialValue);
                    Guid? trial = setTrial ? await TrialPolicyAsync(context, found, body.Text(trialValue)).ConfigureAwait(false) : null;

                    // The change is applied to the row read in the write transaction, so a change of another member at the
                    // same time stays (design §14.5).
                    string detail = string.Empty;
                    ProductRow? changed = await Database
                        .UpdateProductAsync(
                            found.Id,
                            row => row with { Name = name ?? row.Name, TrialPolicyId = setTrial ? trial : row.TrialPolicyId },
                            (before, after) =>
                            {
                                detail = AuditLog.Detail(w =>
                                {
                                    if (after.Name != before.Name)
                                    {
                                        w.WriteString("name", after.Name);
                                    }

                                    if (after.TrialPolicyId != before.TrialPolicyId)
                                    {
                                        w.WriteString("trialPolicyId", after.TrialPolicyId?.ToString("D"));
                                    }
                                });
                                return Entry(context, caller, AuditActions.ProductUpdate, found.Id, detail);
                            },
                            Time.GetUtcNow(),
                            context.RequestAborted
                        )
                        .ConfigureAwait(false);
                    if (changed is null)
                    {
                        return await RefuseAsync(context, ErrorCode.NotFound, NoProduct, found.Id.ToString("D")).ConfigureAwait(false);
                    }

                    await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(changed), ServiceJsonContext.Default.ProductItem)
                        .ConfigureAwait(false);
                    return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
                }
            )
            .ConfigureAwait(false);
    }

    private async Task<AdminOutcome> CreateFeatureAsync(HttpContext context, Caller caller)
    {
        if (await FindProductAsync(context).ConfigureAwait(false) is not ProductRow product)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoProduct, RouteText(context, "id")).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                FeatureMalformed,
                async body =>
                {
                    string code = CheckCode(body.RequiredText("code"));
                    string name = CheckName(body.RequiredText("name"));
                    DateTimeOffset now = Time.GetUtcNow();
                    var feature = new FeatureRow(Guid.CreateVersion7(now), product.Id, code, name, now.ToUnixTimeSeconds());
                    string detail = AuditLog.Detail(w =>
                    {
                        w.WriteString("productId", product.Id.ToString("D"));
                        w.WriteString("code", code);
                        w.WriteString("name", name);
                    });
                    AuditEntry entry = Entry(context, caller, AuditActions.FeatureCreate, feature.Id, detail);
                    if (!await Database.CreateFeatureAsync(feature, entry, now, context.RequestAborted).ConfigureAwait(false))
                    {
                        return await RefuseAsync(context, ErrorCode.Conflict, $"The feature code '{code}' is in use in this product.")
                            .ConfigureAwait(false);
                    }

                    await WriteJsonAsync(context, StatusCodes.Status201Created, ToItem(feature), ServiceJsonContext.Default.FeatureItem)
                        .ConfigureAwait(false);
                    return new AdminOutcome("ELM_OK", feature.Id.ToString("D"), detail, Recorded: true);
                }
            )
            .ConfigureAwait(false);
    }

    private async Task<AdminOutcome> ListFeaturesAsync(HttpContext context, Caller caller)
    {
        if (await FindProductAsync(context).ConfigureAwait(false) is not ProductRow product)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoProduct, RouteText(context, "id")).ConfigureAwait(false);
        }

        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument));
        }

        IReadOnlyList<FeatureRow> rows = await Database.ListFeaturesAsync(product.Id, after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        FeatureRow[] page = [.. rows.Take(limit)];
        var answer = new FeaturePage([.. page.Select(ToItem)], rows.Count > limit ? page[^1].Id.ToString("D") : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.FeaturePage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", product.Id.ToString("D"));
    }

    private async Task<AdminOutcome> UpdateFeatureAsync(HttpContext context, Caller caller)
    {
        FeatureRow? found =
            TryRouteId(context, "id", out Guid productId) && TryRouteId(context, "featureId", out Guid featureId)
                ? await Database.FindFeatureAsync(productId, featureId, context.RequestAborted).ConfigureAwait(false)
                : null;
        if (found is null)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoFeature, RouteText(context, "featureId")).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                FeatureMalformed,
                async body =>
                {
                    RefuseFixed(body, FeatureFixed);
                    string? name = body.TryGet("name", out JsonElement nameValue)
                        ? CheckName(body.Text(nameValue) ?? throw new BodyException(ErrorCode.Malformed, FeatureMalformed))
                        : null;
                    string detail = string.Empty;
                    FeatureRow? changed = await Database
                        .UpdateFeatureAsync(
                            found.ProductId,
                            found.Id,
                            row => row with { Name = name ?? row.Name },
                            (before, after) =>
                            {
                                detail = AuditLog.Detail(w =>
                                {
                                    if (after.Name != before.Name)
                                    {
                                        w.WriteString("name", after.Name);
                                    }
                                });
                                return Entry(context, caller, AuditActions.FeatureUpdate, found.Id, detail);
                            },
                            Time.GetUtcNow(),
                            context.RequestAborted
                        )
                        .ConfigureAwait(false);
                    if (changed is null)
                    {
                        return await RefuseAsync(context, ErrorCode.NotFound, NoFeature, found.Id.ToString("D")).ConfigureAwait(false);
                    }

                    await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(changed), ServiceJsonContext.Default.FeatureItem)
                        .ConfigureAwait(false);
                    return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
                }
            )
            .ConfigureAwait(false);
    }

    private Task<AdminOutcome> CreatePolicyAsync(HttpContext context, Caller caller) =>
        WithBodyAsync(
            context,
            PolicyMember.Malformed,
            async body =>
            {
                string productText = body.RequiredText("productId");
                ProductRow product =
                    Guid.TryParseExact(productText, "D", out Guid productId)
                    && await Database.FindProductAsync(productId, context.RequestAborted).ConfigureAwait(false) is ProductRow found
                        ? found
                        : throw new BodyException(ErrorCode.InvalidArgument, "productId must name a product.");
                string name = CheckName(body.RequiredText("name"));
                PolicySettings settings = Policies.Apply(body, PolicySettings.Defaults);
                if (Policies.Problem(settings) is string problem)
                {
                    throw new BodyException(ErrorCode.InvalidArgument, problem);
                }

                DateTimeOffset now = Time.GetUtcNow();
                long at = now.ToUnixTimeSeconds();
                PolicyRow policy = Policies.ToRow(Guid.CreateVersion7(now), product.Id, name, settings, at, at);
                string detail = AuditLog.Detail(w =>
                {
                    w.WriteString("productId", product.Id.ToString("D"));
                    w.WriteString("name", name);
                    Policies.Write(w, settings, m => body.Has(m.Name));
                });
                AuditEntry entry = Entry(context, caller, AuditActions.PolicyCreate, policy.Id, detail);
                await Database.CreatePolicyAsync(policy, entry, now, context.RequestAborted).ConfigureAwait(false);
                await WriteJsonAsync(context, StatusCodes.Status201Created, ToItem(policy), ServiceJsonContext.Default.PolicyItem)
                    .ConfigureAwait(false);
                return new AdminOutcome("ELM_OK", policy.Id.ToString("D"), detail, Recorded: true);
            }
        );

    private async Task<AdminOutcome> ListPoliciesAsync(HttpContext context, Caller caller)
    {
        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument));
        }

        Guid? productId = null;
        if (context.Request.Query["productId"] is { Count: > 0 } productText)
        {
            if (productText.Count != 1 || !Guid.TryParseExact(productText[0], "D", out Guid parsed))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "productId must be a product ID.").ConfigureAwait(false);
            }

            productId = parsed;
        }

        IReadOnlyList<PolicyRow> rows = await Database.ListPoliciesAsync(productId, after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        PolicyRow[] page = [.. rows.Take(limit)];
        var answer = new PolicyPage([.. page.Select(ToItem)], rows.Count > limit ? page[^1].Id.ToString("D") : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.PolicyPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    private async Task<AdminOutcome> GetPolicyAsync(HttpContext context, Caller caller)
    {
        if (await FindPolicyAsync(context).ConfigureAwait(false) is not PolicyRow policy)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoPolicy, RouteText(context, "id")).ConfigureAwait(false);
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(policy), ServiceJsonContext.Default.PolicyItem).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", policy.Id.ToString("D"));
    }

    private async Task<AdminOutcome> UpdatePolicyAsync(HttpContext context, Caller caller)
    {
        if (await FindPolicyAsync(context).ConfigureAwait(false) is not PolicyRow found)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoPolicy, RouteText(context, "id")).ConfigureAwait(false);
        }

        return await WithBodyAsync(
                context,
                PolicyMember.Malformed,
                async body =>
                {
                    RefuseFixed(body, Policies.Fixed);
                    string? name = body.TryGet("name", out JsonElement nameValue)
                        ? CheckName(body.Text(nameValue) ?? throw new BodyException(ErrorCode.Malformed, PolicyMember.Malformed))
                        : null;
                    DateTimeOffset now = Time.GetUtcNow();

                    // The members are applied to the row read in the write transaction and the whole result is checked there,
                    // so a change of other members at the same time stays and the rules hold for the row written.
                    PolicyRow Change(PolicyRow row)
                    {
                        PolicySettings settings = Policies.Apply(body, Policies.Of(row));
                        return Policies.Problem(settings) is string problem
                            ? throw new BodyException(ErrorCode.InvalidArgument, problem)
                            : Policies.ToRow(row.Id, row.ProductId, name ?? row.Name, settings, row.CreatedAt, now.ToUnixTimeSeconds());
                    }

                    string detail = string.Empty;
                    PolicyRow? changed = await Database
                        .UpdatePolicyAsync(
                            found.Id,
                            Change,
                            (before, after) =>
                            {
                                PolicySettings was = Policies.Of(before);
                                PolicySettings next = Policies.Of(after);
                                detail = AuditLog.Detail(w =>
                                {
                                    if (after.Name != before.Name)
                                    {
                                        w.WriteString("name", after.Name);
                                    }

                                    Policies.Write(w, next, m => !Equals(m.Value(was), m.Value(next)));
                                });
                                return Entry(context, caller, AuditActions.PolicyUpdate, found.Id, detail);
                            },
                            now,
                            context.RequestAborted
                        )
                        .ConfigureAwait(false);
                    if (changed is null)
                    {
                        return await RefuseAsync(context, ErrorCode.NotFound, NoPolicy, found.Id.ToString("D")).ConfigureAwait(false);
                    }

                    await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(changed), ServiceJsonContext.Default.PolicyItem)
                        .ConfigureAwait(false);
                    return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
                }
            )
            .ConfigureAwait(false);
    }

    private async Task<AdminOutcome> GetSigningKeyAsync(HttpContext context, Caller caller)
    {
        string[] uses = SigningKeyCertificates.UseNames(Signing.Uses);
        bool soon = Signing.ExpiresAt - Time.GetUtcNow() < SigningKeyWarning;
        var answer = new SigningKeyAnswer(Signing.KeyId, uses, AdminApi.Time(Signing.ExpiresAt.ToUnixTimeSeconds()), soon);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.SigningKeyAnswer).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", Signing.KeyId);
    }

    // Reads the body as one JSON object and runs the operation; a body that cannot be used is answered here.
    private static async Task<AdminOutcome> WithBodyAsync(HttpContext context, string malformed, Func<RequestBody, Task<AdminOutcome>> operation)
    {
        if (await ReadBodyAsync(context).ConfigureAwait(false) is not byte[] bytes)
        {
            return await RefuseAsync(context, ErrorCode.PayloadTooLarge, "The request body is larger than 64 KiB.").ConfigureAwait(false);
        }

        try
        {
            using RequestBody body = RequestBody.Parse(bytes, malformed);
            return await operation(body).ConfigureAwait(false);
        }
        catch (BodyException e)
        {
            return await RefuseAsync(context, e.Code, e.Message).ConfigureAwait(false);
        }
    }

    // Reads limit and after (an ID); on a bad value the 400 is already written and the answer is null.
    private static async Task<(int Limit, Guid? After)?> ReadPageAsync(HttpContext context)
    {
        if (!TryReadLimit(context, out int limit))
        {
            await ProblemAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.").ConfigureAwait(false);
            return null;
        }

        if (context.Request.Query["after"] is not { Count: > 0 } afterText)
        {
            return (limit, null);
        }

        if (afterText.Count != 1 || !Guid.TryParseExact(afterText[0], "D", out Guid after))
        {
            await ProblemAsync(context, ErrorCode.InvalidArgument, "after must be an ID.").ConfigureAwait(false);
            return null;
        }

        return (limit, after);
    }

    private async Task<ProductRow?> FindProductAsync(HttpContext context) =>
        TryRouteId(context, "id", out Guid id) ? await Database.FindProductAsync(id, context.RequestAborted).ConfigureAwait(false) : null;

    private async Task<PolicyRow?> FindPolicyAsync(HttpContext context) =>
        TryRouteId(context, "id", out Guid id) ? await Database.FindPolicyAsync(id, context.RequestAborted).ConfigureAwait(false) : null;

    // The product's trial policy must be a trial policy of that product (design §9.3); null removes it.
    private async Task<Guid?> TrialPolicyAsync(HttpContext context, ProductRow product, string? text)
    {
        if (text is null)
        {
            return null;
        }

        PolicyRow? policy = Guid.TryParseExact(text, "D", out Guid id)
            ? await Database.FindPolicyAsync(id, context.RequestAborted).ConfigureAwait(false)
            : null;
        return policy is { LicenseKind: Policies.Trial } && policy.ProductId == product.Id
            ? policy.Id
            : throw new BodyException(ErrorCode.InvalidArgument, "trialPolicyId must name a trial policy of this product.");
    }

    // A known member that cannot change is refused, not ignored like an unknown one (design §14.5).
    private static void RefuseFixed(RequestBody body, string[] fixedMembers)
    {
        if (fixedMembers.FirstOrDefault(body.Has) is string member)
        {
            throw new BodyException(ErrorCode.InvalidArgument, $"{member} cannot be changed.");
        }
    }

    private static AuditEntry Entry(HttpContext context, Caller caller, string action, Guid target, string detail) =>
        new("apikey", caller.Key.Id.ToString("D"), action, target.ToString("D"), "ELM_OK", context.Connection.RemoteIpAddress?.ToString(), detail);

    private static bool TryRouteId(HttpContext context, string name, out Guid id) =>
        Guid.TryParseExact(context.Request.RouteValues[name] as string, "D", out id);

    private static string? RouteText(HttpContext context, string name) => context.Request.RouteValues[name] as string;

    private static string CheckCode(string code) => CodePattern().IsMatch(code) ? code : throw new BodyException(ErrorCode.InvalidArgument, CodeRule);

    private static string CheckName(string name) =>
        name.EnumerateRunes().Count() is >= 1 and <= MaxNameLength ? name : throw new BodyException(ErrorCode.InvalidArgument, NameRule);

    private static ProductItem ToItem(ProductRow p) =>
        new(p.Id.ToString("D"), p.Code, p.Name, p.TrialPolicyId?.ToString("D"), AdminApi.Time(p.CreatedAt));

    private static FeatureItem ToItem(FeatureRow f) => new(f.Id.ToString("D"), f.ProductId.ToString("D"), f.Code, f.Name, AdminApi.Time(f.CreatedAt));

    private static PolicyItem ToItem(PolicyRow p) =>
        new(
            p.Id.ToString("D"),
            p.ProductId.ToString("D"),
            p.Name,
            p.LicenseKind,
            p.Binding,
            p.MaxActivations,
            p.DeactivationLimit,
            p.ExpirationBasis,
            p.Duration,
            p.TokenLifetime,
            p.RefreshWindow,
            p.GracePeriod,
            p.FingerprintMatching,
            p.CloneStrategy,
            p.Floating,
            p.LeaseDuration,
            p.Overage,
            p.BorrowLimit,
            p.BorrowReturnLimit,
            p.ClientInfoStrategy,
            p.TrialDuration,
            AdminApi.Time(p.CreatedAt),
            AdminApi.Time(p.UpdatedAt)
        );

    // design §9.1: lowercase letters, digits, and '-', 1 to 64 characters.
    [GeneratedRegex(@"^[a-z0-9-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
