using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <content>
/// Answers checked against the request they answer (design §12.6.2, §12.8.5, V1d5a): a relay on the path between the client
/// and a real <c>elm-service</c> hands out the service's genuine answers to other requests (design §5.3 T17), or changes the
/// unsigned activation ID; and files signed with the service's signing key whose claims differ from the request's.
/// </content>
public sealed partial class ActivationClientTests
{
    private const string AnswersAnother = "The service's license file answers another request.";
    private const string AnotherActivation = "The service's answer names another activation than its license file.";
    private const string RefreshOfAnother = "The service's refresh is for another activation than the one kept.";
    private const string NoLaterSequence = "The service's refresh has no later sequence number than the activation kept.";

    /// <summary>
    /// An activation answer, or an imported offline answer, whose text cannot be read (a lone surrogate escape, a pair in the
    /// wrong order, bytes that are not UTF-8) is ELM_MALFORMED and keeps nothing, without an exception (design §8.3, V1d23,
    /// finding F20).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client behind a relay that replaces the service's genuine answer to the next activation; a second
    ///   client with a waiting offline activation request.
    ///
    /// Steps:
    /// - Activate, the relay giving the genuine answer with its activationId "\ud800", with a member "\ud800" added at its
    ///   end, with its activationId "x" and the byte 0xFF, or with its activationId "\udc00\ud800"; import the same answer
    ///   text in the second client, for each shape a string can carry.
    ///
    /// Expected:
    /// - ELM_MALFORMED "The service's answer is not an issued license.", no activation; the import ELM_MALFORMED "The text
    ///   is not an answer of the service.", no activation.
    /// </remarks>
    [Theory]
    [InlineData("value")]
    [InlineData("name")]
    [InlineData("not-utf8")]
    [InlineData("reversed")]
    public async Task Answer_TextThatIsNotUnicode_IsMalformed(string shape)
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":5");
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        string? changed = null;
        relay.ReplaceNextBytes(genuine =>
        {
            using JsonDocument answer = JsonDocument.Parse(genuine);
            string id = answer.RootElement.GetProperty("activationId").GetString()!;
            changed = shape switch
            {
                "value" => genuine.Replace(id, "\\ud800", StringComparison.Ordinal),
                "name" => genuine[..genuine.LastIndexOf('}')] + ",\"\\ud800\":1}",
                "reversed" => genuine.Replace(id, "\\udc00\\ud800", StringComparison.Ordinal),
                _ => null,
            };
            if (changed is not null)
            {
                return Encoding.UTF8.GetBytes(changed);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(genuine.Replace(id, "x\u00ff", StringComparison.Ordinal));
            int at = Array.IndexOf(bytes, (byte)0xC3);
            return [.. bytes[..at], 0xFF, .. bytes[(at + 2)..]];
        });

        Assert.Equal((ErrorCode.Malformed, "The service's answer is not an issued license."), Code(Failed(await client.ActivateAsync(key, Cancel))));
        Assert.Null(client.ActivationId);

        if (changed is not null)
        {
            await using ActivationClient offline = vendor.Client("b", offline: true);
            Created(await offline.CreateActivationRequestAsync(key, cancellationToken: Cancel));
            Assert.Equal(
                (ErrorCode.Malformed, "The text is not an answer of the service."),
                Code(Failed(await offline.ImportResponseAsync(changed, Cancel)))
            );
            Assert.Null(offline.ActivationId);
        }
    }

    /// <summary>
    /// A refresh answered with the service's genuine answer to an earlier refresh is refused and leaves the kept activation
    /// as it was, with the request's jti in the retry list; the next refresh gets the file the service issued for that
    /// request, whose areq is in the list (design §10.4 step 3, §12.6.2; the Issue's case R1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client behind a relay that keeps the service's answers and can replace the next one after passing the
    ///   request on.
    ///
    /// Steps:
    /// - Activate; refresh (sequence 2, its answer kept); refresh (sequence 3); have the relay answer the next refresh with
    ///   the answer of sequence 2; refresh; read the kept activation; refresh again; read the audit log.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH "…no later sequence number…"; the kept activation still has sequence 3, the same license
    ///   file and ID, the replaced request's jti alone in its retry list, and no unkept hash; state RefreshFailing. The next
    ///   refresh is
    ///   Activated with sequence 4 and areq the replaced request's jti; the list is empty, state Active, no clone recorded.
    /// </remarks>
    [Fact]
    public async Task Refresh_ReplayedEarlierAnswer_IsRefusedAndTheRetryGetsTheServicesFile()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        string earlier = relay.LastAnswer!;
        Assert.Equal(3L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        StoredActivation before = vendor.Kept("a");

        relay.ReplaceNext((_, _) => earlier);
        Assert.Equal((ErrorCode.RequestMismatch, NoLaterSequence), Code(Failed(await client.RefreshAsync(Cancel))));
        string replaced = relay.LastProofId!;
        StoredActivation after = vendor.Kept("a");
        Assert.Equal((before.ActivationId, 3L, Text(before.License)), (after.ActivationId, after.Seq, Text(after.License)));
        Assert.Equal([replaced], after.Retry);
        Assert.Null(after.Unkept);
        Assert.Equal((ActivationState.RefreshFailing, before.ActivationId), (client.State, client.ActivationId));

        VerifiedLicense next = Activated(await client.RefreshAsync(Cancel));
        Assert.Equal((4L, replaced, before.ActivationId), (next.Sequence, next.AnsweredRequestId, next.ActivationId));
        Assert.Equal((0, ActivationState.Active), (vendor.Kept("a").Retry.Count, client.State));
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// Under cloneStrategy flag, a service restored from an older backup, whose activation is behind the device's sequence
    /// number, records the refresh as a clone and answers with a file later than the device's, which the client keeps (design
    /// §10.4, the owner's answer 5a-6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with cloneStrategy flag; a client activated and refreshed until it holds sequence gap + 1.
    ///
    /// Steps:
    /// - Set the service's activation back by the gap (1, then 20) in the database; refresh; read the activation and the
    ///   audit log.
    ///
    /// Expected:
    /// - Activated with one more than the device's sequence number; the service's activation has that number; one
    ///   activation.clone row with ELM_OK.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Refresh_ServiceRestoredBehindUnderFlag_GetsALaterFile(int gap)
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"cloneStrategy\":\"flag\"");
        await using ActivationClient client = vendor.Client("a");
        Activated(await client.ActivateAsync(key, Cancel));
        for (int i = 0; i < gap; i++)
        {
            Activated(await client.RefreshAsync(Cancel));
        }

        long held = vendor.Kept("a").Seq;
        Assert.Equal(gap + 1, held);
        await vendor.Test.ExecuteAsync($"UPDATE activations SET seq = {held - gap}");

        Assert.Equal(held + 1, Activated(await client.RefreshAsync(Cancel)).Sequence);
        Assert.Equal(held + 1, (await vendor.ActivationAsync(client.ActivationId!)).GetProperty("seq").GetInt64());
        JsonElement clone = Assert.Single(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
        Assert.Equal("ELM_OK", clone.GetProperty("result").GetString());
    }

    /// <summary>
    /// An activation of a second license answered with the service's genuine answer to the activation of the first is
    /// refused; the activation sent again gets the second license's own activation, which the service made for the refused
    /// request (the Issue's case R2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two licenses; a client behind a relay.
    ///
    /// Steps:
    /// - Activate license 1 (its answer kept) and deactivate; have the relay answer the next activation with that answer;
    ///   activate license 2; read the kept activation; activate license 2 again.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH "…answers another request."; no activation kept, state None, the request's jti alone in the
    ///   pending list. Then Activated with license 2, areq the refused request's jti; license 2 has one activation, and no
    ///   clone is recorded.
    /// </remarks>
    [Fact]
    public async Task Activate_ReplayedAnswerOfAnotherLicense_IsRefusedAndTheRetryGetsItsOwn()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string first = await vendor.LicenseAsync(string.Empty);
        (string secondId, string second) = await vendor.LicenseWithIdAsync(string.Empty);
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        Activated(await client.ActivateAsync(first, Cancel));
        string answer = relay.LastAnswer!;
        Assert.Null(await client.DeactivateAsync(Cancel));

        relay.ReplaceNext((_, _) => answer);
        Assert.Equal((ErrorCode.RequestMismatch, AnswersAnother), Code(Failed(await client.ActivateAsync(second, Cancel))));
        string refused = relay.LastProofId!;
        StoredActivation kept = vendor.Kept("a");
        Assert.Equal(((string?)null, (byte[]?)null), (kept.ActivationId, kept.License));
        Assert.Equal([refused], kept.Pending);
        Assert.Equal((ActivationState.None, (string?)null), (client.State, client.ActivationId));

        VerifiedLicense license = Activated(await client.ActivateAsync(second, Cancel));
        Assert.Equal((secondId, refused), (license.LicenseId, license.AnsweredRequestId));
        Assert.Equal(1, await vendor.ActivationCountAsync(secondId));
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// The activation of a bought license answered with the service's genuine answer to the trial is refused and keeps the
    /// trial; a trial started after the license's deactivation, answered with that trial's answer, is refused too (the
    /// Issue's case R3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license, and a product with a trial policy; a client behind a relay.
    ///
    /// Steps:
    /// - Start a trial (its answer kept); have the relay answer the next activation with it; activate the license; read the
    ///   kept activation; activate again; deactivate; have the relay answer the next trial with the trial's answer; start a
    ///   trial; read the kept activation.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH "…answers another request."; the trial still kept with its ID and state Active; then
    ///   Activated with the license's ID. The second trial: ELM_REQUEST_MISMATCH "…answers another request."; no
    ///   activation kept, state None, and the request's jti alone in the pending list.
    /// </remarks>
    [Fact]
    public async Task Trial_ReplayedTrialAnswer_IsRefused()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        Assert.Equal(LicenseKind.Trial, Activated(await client.StartTrialAsync(Cancel)).Kind);
        string trial = relay.LastAnswer!;
        string trialId = client.ActivationId!;

        relay.ReplaceNext((_, _) => trial);
        Assert.Equal((ErrorCode.RequestMismatch, AnswersAnother), Code(Failed(await client.ActivateAsync(key, Cancel))));
        Assert.Equal((trialId, "trial"), (vendor.Kept("a").ActivationId, vendor.Kept("a").Kind));
        Assert.Equal((ActivationState.Active, trialId), (client.State, client.ActivationId));
        Assert.Equal(licenseId, Activated(await client.ActivateAsync(key, Cancel)).LicenseId);

        Assert.Null(await client.DeactivateAsync(Cancel));
        relay.ReplaceNext((_, _) => trial);
        Assert.Equal((ErrorCode.RequestMismatch, AnswersAnother), Code(Failed(await client.StartTrialAsync(Cancel))));
        Assert.Equal(((string?)null, ActivationState.None), (vendor.Kept("a").ActivationId, client.State));
        Assert.Equal([relay.LastProofId!], vendor.Kept("a").Pending);
    }

    /// <summary>
    /// An activation answered with the service's genuine answer to another request still waiting for its answer is refused
    /// when that request was for another target: an activation of another license key, a trial, or an offline activation
    /// request of another key (the review of PR #418, question 5a-7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two licenses, and a product with a trial policy; a client behind a relay.
    ///
    /// Steps:
    /// - key: activate license 1 with the answer dropped (its answer kept by the relay); have the relay answer the next
    ///   activation with it; activate license 2; read the kept activation; activate license 2 again.
    /// - trial: start a trial with the answer dropped; have the relay answer the next activation with the trial's answer;
    ///   activate license 1; activate it again.
    /// - offline: make an offline activation request for license 1 and have the service process it; have the relay answer
    ///   the next online activation with that answer; activate license 2; activate it again.
    ///
    /// Expected:
    /// - Each replayed answer: ELM_REQUEST_MISMATCH "…answers another request.", no activation kept, and the refused
    ///   request's jti in the pending list. Each activation after it: Activated with the license activated, its areq the
    ///   refused request's jti; no clone is recorded.
    /// </remarks>
    [Theory]
    [InlineData("key")]
    [InlineData("trial")]
    [InlineData("offline")]
    public async Task Activate_AnswerToAnotherPendingRequest_IsRefused(string kind)
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string firstId, string first) = await vendor.LicenseWithIdAsync(string.Empty);
        (string secondId, string second) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        string other;
        string wanted = kind == "trial" ? first : second;
        string wantedId = kind == "trial" ? firstId : secondId;
        switch (kind)
        {
            case "key":
                relay.DropNext();
                Assert.Equal(ErrorCode.NetworkError, Failed(await client.ActivateAsync(first, Cancel)).Code);
                other = relay.LastAnswer!;
                break;
            case "trial":
                relay.DropNext();
                Assert.Equal(ErrorCode.NetworkError, Failed(await client.StartTrialAsync(Cancel)).Code);
                other = relay.LastAnswer!;
                break;
            default:
                string request = Created(await client.CreateActivationRequestAsync(first, cancellationToken: Cancel)).File;
                other = await ProcessedAsync(vendor, request);
                break;
        }

        relay.ReplaceNext((_, _) => other);
        Assert.Equal((ErrorCode.RequestMismatch, AnswersAnother), Code(Failed(await client.ActivateAsync(wanted, Cancel))));
        string refused = relay.LastProofId!;
        Assert.Equal(((string?)null, ActivationState.None), (vendor.Kept("a").ActivationId, client.State));
        Assert.Contains(refused, vendor.Kept("a").Pending);

        VerifiedLicense license = Activated(await client.ActivateAsync(wanted, Cancel));
        Assert.Equal((wantedId, refused), (license.LicenseId, license.AnsweredRequestId));
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// The aim of a request that got no answer survives the steps between it and the activation sent again, which then gets
    /// the file the service issued for it (design §12.6.2, V1d5a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license, and a product with a trial policy; a client behind a relay.
    ///
    /// Steps:
    /// - replaced-offline: make an offline activation request and have the service process it; make another with renew,
    ///   which puts the first one's nonce in the pending list; activate online.
    /// - deactivation-proof: start a trial; activate the license with the answer dropped; make a deactivation proof and
    ///   forget it; activate again.
    /// - offline-refresh: start a trial; activate the license with the answer dropped; make an offline refresh request,
    ///   have the service process it, and import the answer; activate again.
    ///
    /// Expected:
    /// - The last activation is Activated with the license, and its areq is the nonce of the first offline request, or the
    ///   jti of the activation whose answer was dropped.
    /// </remarks>
    [Theory]
    [InlineData("replaced-offline")]
    [InlineData("deactivation-proof")]
    [InlineData("offline-refresh")]
    public async Task Activate_AimKeptThroughOtherSteps_TakesTheFileOfTheEarlierRequest(string kind)
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        string earlier;
        if (kind == "replaced-offline")
        {
            string first = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel)).File;
            await ProcessedAsync(vendor, first);
            Assert.True(Created(await client.CreateActivationRequestAsync(key, renew: true, cancellationToken: Cancel)).Replaced);
            earlier = NonceOf(first);
        }
        else
        {
            Activated(await client.StartTrialAsync(Cancel));
            relay.DropNext();
            Assert.Equal(ErrorCode.NetworkError, Failed(await client.ActivateAsync(key, Cancel)).Code);
            earlier = relay.LastProofId!;
            if (kind == "deactivation-proof")
            {
                Created(await client.CreateDeactivationProofAsync(Cancel));
                Assert.Null(await client.ForgetDeactivationProofAsync(Cancel));
            }
            else
            {
                string refresh = Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File;
                Activated(await client.ImportResponseAsync(await ProcessedAsync(vendor, refresh), Cancel));
            }
        }

        VerifiedLicense license = Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal((licenseId, earlier), (license.LicenseId, license.AnsweredRequestId));
    }

    /// <summary>
    /// An answer whose unsigned activation ID a relay changed is refused, for an activation and for a refresh, and the kept
    /// activation keeps its ID (the Issue's case R5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client behind a relay that can rewrite the next answer.
    ///
    /// Steps:
    /// - Have the relay put another UUID in the next answer's activationId; activate; activate again; have it do the same
    ///   to the next refresh; refresh; read the ID and the kept activation; refresh again.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH "…names another activation than its license file." and no activation kept; then Activated.
    ///   The refresh: the same error; the client's ID and the kept one unchanged, sequence 1; the refresh after it Activated
    ///   with sequence 2 and that ID.
    /// </remarks>
    [Fact]
    public async Task Answer_UnsignedActivationIdChanged_IsRefused()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        string another = Guid.CreateVersion7().ToString("D");

        relay.ReplaceNext((body, _) => WithActivationId(body, another));
        Assert.Equal((ErrorCode.RequestMismatch, AnotherActivation), Code(Failed(await client.ActivateAsync(key, Cancel))));
        Assert.Null(client.ActivationId);
        string id = Activated(await client.ActivateAsync(key, Cancel)).ActivationId!;
        Assert.Equal(id, client.ActivationId);

        relay.ReplaceNext((body, _) => WithActivationId(body, another));
        Assert.Equal((ErrorCode.RequestMismatch, AnotherActivation), Code(Failed(await client.RefreshAsync(Cancel))));
        Assert.Equal((id, id, 1L), (client.ActivationId, vendor.Kept("a").ActivationId, vendor.Kept("a").Seq));
        VerifiedLicense next = Activated(await client.RefreshAsync(Cancel));
        Assert.Equal((2L, id), (next.Sequence, next.ActivationId));
    }

    /// <summary>
    /// A refresh answered with a file the service's signing key signed is kept only when it names this request, the kept
    /// activation, and a later sequence number; each claim changed alone is refused with its own message, and the kept
    /// activation stays as it was (design §12.6.2, V1d5a, the owner's answer 5a-5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; an activated client behind a relay that answers the next refresh with a file made by the test: signed
    ///   with the service's signing key, bound to the client's key and fingerprints, with the answer's activationId equal to
    ///   the file's act unless the case says otherwise.
    ///
    /// Steps:
    /// - Refresh, answered with the case's file: valid (areq the request's jti, act the kept ID, sequence 2); without areq;
    ///   areq of another request; without act; act and activationId of another activation; sequence 1. Read the kept
    ///   activation.
    ///
    /// Expected:
    /// - valid: Activated with sequence 2 and the request's jti in areq. The others: ELM_REQUEST_MISMATCH with "…answers
    ///   another request." (twice), "…names another activation than its license file.", "…is for another activation than
    ///   the one kept.", "…no later sequence number…"; the kept activation still at sequence 1 with its file, and the
    ///   request's jti in its retry list.
    /// </remarks>
    [Theory]
    [InlineData("valid")]
    [InlineData("no-areq")]
    [InlineData("other-areq")]
    [InlineData("no-act")]
    [InlineData("other-act")]
    [InlineData("same-seq")]
    public async Task Refresh_SignedFileWithOtherClaims_IsRefusedWithItsMessage(string kind)
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        var relay = new Relay();
        await using ActivationClient client = vendor.Client("a", relay);
        VerifiedLicense first = Activated(await client.ActivateAsync(key, Cancel));
        string id = first.ActivationId!;
        StoredActivation before = vendor.Kept("a");
        string another = Guid.CreateVersion7().ToString("D");

        relay.ReplaceNext(
            (_, jti) =>
                kind switch
                {
                    "no-areq" => Answer(id, HandFile(vendor, first, 2, null, id)),
                    "other-areq" => Answer(id, HandFile(vendor, first, 2, "another-request", id)),
                    "no-act" => Answer(id, HandFile(vendor, first, 2, jti, null)),
                    "other-act" => Answer(another, HandFile(vendor, first, 2, jti, another)),
                    "same-seq" => Answer(id, HandFile(vendor, first, 1, jti, id)),
                    _ => Answer(id, HandFile(vendor, first, 2, jti, id)),
                }
        );
        ActivationResult result = await client.RefreshAsync(Cancel);
        string sent = relay.LastProofId!;
        if (kind == "valid")
        {
            VerifiedLicense license = Activated(result);
            Assert.Equal((2L, sent, id), (license.Sequence, license.AnsweredRequestId, license.ActivationId));
            return;
        }

        string expected = kind switch
        {
            "no-act" => AnotherActivation,
            "other-act" => RefreshOfAnother,
            "same-seq" => NoLaterSequence,
            _ => AnswersAnother,
        };
        Assert.Equal((ErrorCode.RequestMismatch, expected), Code(Failed(result)));
        StoredActivation after = vendor.Kept("a");
        Assert.Equal((id, 1L, Text(before.License)), (after.ActivationId, after.Seq, Text(after.License)));
        Assert.Equal([sent], after.Retry);
    }

    /// <summary>
    /// An imported offline answer whose unsigned activation ID was changed is refused and changes nothing; the answer as the
    /// service gave it is then kept with the signed ID (design §12.8.5, V1d5a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client without a service URL with a waiting activation request that the service processed.
    ///
    /// Steps:
    /// - Import the answer with another UUID in activationId; read the kept activation; import the answer as it came.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH "…names another activation than its license file."; no activation kept and the request still
    ///   waiting. Then Activated with the act of its file, which is the client's ID.
    /// </remarks>
    [Fact]
    public async Task Import_UnsignedActivationIdChanged_IsRefused()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a", offline: true);
        string request = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel)).File;
        string answer = await ProcessedAsync(vendor, request);

        string changed = WithActivationId(answer, Guid.CreateVersion7().ToString("D"));
        Assert.Equal((ErrorCode.RequestMismatch, AnotherActivation), Code(Failed(await client.ImportResponseAsync(changed, Cancel))));
        Assert.Equal(((string?)null, NonceOf(request)), (vendor.Kept("a").ActivationId, vendor.Kept("a").Offline?.Nonce));

        VerifiedLicense license = Activated(await client.ImportResponseAsync(answer, Cancel));
        Assert.Equal((license.ActivationId, NonceOf(request)), (client.ActivationId, license.AnsweredRequestId));
    }

    /// <summary>
    /// A kept license file without areq and act, as kept before they were issued, still passes Check, and the activation
    /// refreshes (the owner's answer 5a-5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An activated client whose kept license file is replaced by one the service's signing key signed without areq and
    ///   act, of the same activation, bound to the same key and fingerprints, with sequence 1.
    ///
    /// Steps:
    /// - Check feature core; refresh.
    ///
    /// Expected:
    /// - Valid, with no areq and no act; Activated with sequence 2.
    /// </remarks>
    [Fact]
    public async Task KeptFile_WithoutTheAnswersClaims_StillChecksAndRefreshes()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a");
        VerifiedLicense first = Activated(await client.ActivateAsync(key, Cancel));
        new ActivationStore(vendor.Folder("a"), vendor.Test.Time).Write(vendor.Kept("a") with { License = HandFile(vendor, first, 1, null, null) });

        VerifiedLicense kept = Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null)).License;
        Assert.Equal(((string?)null, (string?)null), (kept.AnsweredRequestId, kept.ActivationId));
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
    }

    private static string Text(byte[]? file) => file is null ? string.Empty : Encoding.UTF8.GetString(file);

    // {activationId, license} as the service answers (design §12.6.1).
    private static string Answer(string activationId, byte[] file) =>
        $$"""{"activationId":"{{activationId}}","license":{{Encoding.UTF8.GetString(file)}}}""";

    // The same answer with another activationId; the license file, and so its signature, is unchanged.
    private static string WithActivationId(string answer, string activationId)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        return $$"""{"activationId":"{{activationId}}","license":{{document.RootElement.GetProperty("license").GetRawText()}}}""";
    }

    // A license file of the activation's license that the service's signing key signs, bound to the same key and
    // fingerprints, with the given sequence number, areq, and act.
    private static byte[] HandFile(Vendor vendor, VerifiedLicense like, long seq, string? areq, string? act)
    {
        DateTimeOffset now = vendor.Test.Time.GetUtcNow();
        var spec = new LicenseSpec
        {
            LicenseId = like.LicenseId,
            ProductId = like.ProductId,
            Kind = like.Kind,
            Features = [new FeatureSpec("core")],
            ExpiresAt = now.AddDays(1),
            RefreshWindow = like.RefreshWindow,
            InstallationKeyThumbprint = like.InstallationKeyThumbprint,
            Fingerprints = like.Fingerprints,
            Sequence = seq,
            AnsweredRequestId = areq,
            ActivationId = act,
        };
        string certificate = File.ReadAllText(vendor.Test.WriteCertificate("hand.cert", KeyUses.License, now.AddDays(-1), TimeSpan.FromDays(365)));
        return LicenseIssuer.Issue(vendor.Test.SigningKey, [certificate], spec, vendor.Test.Time).File;
    }

    // A relay on the path (design §5.3 T17): passes each request on, records the jti of each POST's proof and the body of each
    // successful answer, and, when told, answers the next POST with 200 and a body made from the service's own answer and the
    // request's jti, keeping the service's ELM-Nonce header.
    private sealed class Relay : DelegatingHandler
    {
        private Func<string, string, string>? _replace;
        private Func<string, byte[]>? _replaceBytes;
        private volatile string? _lastProofId;
        private volatile string? _lastAnswer;
        private volatile bool _dropNext;

        public string? LastProofId => _lastProofId;

        public string? LastAnswer => _lastAnswer;

        public void ReplaceNext(Func<string, string, string> replace) => Volatile.Write(ref _replace, replace);

        // The next answer becomes the bytes made from the service's answer, which need not be UTF-8.
        public void ReplaceNextBytes(Func<string, byte[]> replace) => Volatile.Write(ref _replaceBytes, replace);

        // The next POST is passed on and its answer kept, and the client gets no answer, as when a connection is cut.
        public void DropNext() => _dropNext = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            string jti = ProofId(request.Headers.GetValues("ELM-Proof").Single());
            _lastProofId = jti;
            using HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _lastAnswer = body;
            }

            if (_dropNext)
            {
                _dropNext = false;
                throw new HttpRequestException("The relay dropped the answer.");
            }

            Func<string, string, string>? replace = Interlocked.Exchange(ref _replace, null);
            Func<string, byte[]>? replaceBytes = Interlocked.Exchange(ref _replaceBytes, null);
            if (replaceBytes is not null)
            {
                replace = (b, _) => b;
            }

            var answer = new HttpResponseMessage(replace is null ? response.StatusCode : HttpStatusCode.OK)
            {
                Content = replaceBytes is not null
                    ? new ByteArrayContent(replaceBytes(body))
                    : new StringContent(replace is null ? body : replace(body, jti), Encoding.UTF8),
            };
            answer.Content.Headers.ContentType = replace is null ? response.Content.Headers.ContentType : new("application/json");
            if (response.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? nonces))
            {
                answer.Headers.TryAddWithoutValidation("ELM-Nonce", nonces);
            }

            if (replace is null)
            {
                answer.Headers.RetryAfter = response.Headers.RetryAfter;
            }

            return answer;
        }

        private static string ProofId(string proof)
        {
            using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(proof.Split('.')[1]));
            return payload.RootElement.GetProperty("jti").GetString()!;
        }
    }
}
