---
sidebar_position: 9
---

# .NET API

This page lists the public API of the two NuGet packages that a .NET app embeds: `ElasticLM.Licensing`, which verifies license files and lease tokens offline, and `ElasticLM.Client`, which activates a device with `elm-service`, keeps its license file, and leases seats from `elm-server` or `elm-service`. It is for app developers who look up a type, a method, or an option. Each release publishes both packages to nuget.org; 1.0 is not released yet, so this page describes the API of the `main` branch. Every type and member has XML documentation, which ships in the packages and which your IDE shows; this page gives each type in one line and the entry types in more detail. What the checks are and why they exist is in [License files and verification](../explanation/license-files-and-verification.md), [Activation](../explanation/activation.md), and [Floating licenses](../explanation/floating-licenses.md).

## How failures are reported

A license, a token, or a server answer that fails a check never throws, whatever its content. The failure comes back as a value:

| Returned | Success | Failure |
| --- | --- | --- |
| `LicenseVerificationResult` | `LicenseVerificationResult.Valid` with `License` and `InGracePeriod` | `LicenseVerificationResult.Invalid` with `Error` |
| `LeaseVerificationResult` | `LeaseVerificationResult.Valid` with `Lease` | `LeaseVerificationResult.Invalid` with `Error` |
| `ActivationResult` | `ActivationResult.Activated` with `License` and `InGracePeriod` | `ActivationResult.Failed` with `Error` and `RetryAfter` |
| `OfflineRequestResult` | `OfflineRequestResult.Created` with `File` and `Replaced` | `OfflineRequestResult.Failed` with `Error` |
| `LeaseResult` | `LeaseResult.Acquired` with `Lease` | `LeaseResult.Failed` with `Error` and `RetryAfter` |
| `Task<LicenseError?>` | `null` | the `LicenseError` |
| `bool` with an `out LicenseError?` (`TryCreate`, `TryVerify`) | `true` | `false`, and the error |

Each result type has exactly these two cases, so a `switch` over them is complete. `Error` is a `LicenseError`: `Code` is an `ErrorCode` value, `WireName` is its name (such as `ELM_EXPIRED`), and `Message` is one English sentence for people that never contains key material or a full token. [Error codes](error-codes.md) lists every code, and which codes the client retries on its own before the app sees them. `RetryAfter` is the wait the server asked for with `ELM_SERVER_RECOVERING` or `ELM_RATE_LIMITED`, or `null`; the client does not wait by itself.

Exceptions are only for mistakes in the call: `ArgumentException` (an empty or invalid argument, such as a product ID or a URL), `ArgumentOutOfRangeException` (a seat count below 1, a clock skew out of range), `ArgumentNullException`, and `ObjectDisposedException` after a client is disposed. The XML documentation of each member names the exceptions it throws.

## `ElasticLM.Licensing`

The verification library. It has no dependency outside the .NET base class library and never uses the network.

| Type | Kind | What it is |
| --- | --- | --- |
| `CloneStrategy` | Enum | What a server does when it detects a cloned installation: `Deny` (stop and answer `ELM_CLONE_DETECTED`) or `Flag` (record it and answer as usual). |
| `ErrorCode` | Enum | Every ElasticLM error code, with its number (`Ok` is 0, `Malformed` is 2). Numbers and names never change once published. |
| `ErrorCodes` | Static class | Converts between `ErrorCode` and the wire name: `ToWireName` and `TryParseWireName`. |
| `FileClockGuard` | Class | A clock guard that keeps the last time seen in a file. Given an installation key, it signs the file; a file that is missing, edited, or unreadable falls back to the token's issue time. The folder must exist. |
| `HttpTargetUri` | Static class | `Normalize` returns the form of a request URI that a proof signs: scheme and host in lowercase, no default port, uppercase percent-encodings, no query or fragment. |
| `IClockGuard` | Interface | A clock guard. `Observe` checks the current time against the last time seen and records it when the check passes; `AcceptServerTime` takes the service's time after an online refresh (by default it does nothing). |
| `InstallationIdentity` | Class | This installation, for a license bound to a device: the installation key's thumbprint (`KeyThumbprint`) and the current machine fingerprints (`Fingerprints`, which may be empty). |
| `KeyIds` | Static class | `Of` returns the key ID of a P-256 key: its JWK thumbprint (RFC 7638), base64url. |
| `LeaseExpectation` | Record | What a lease must be for: `ProductId` and `InstallationKeyThumbprint`. |
| `LeaseVerificationResult` | Abstract record | The result of a lease check: the case `Valid` holds a `VerifiedLease` (`Lease`), the case `Invalid` a `LicenseError` (`Error`). |
| `LeaseVerifier` | Class | Checks lease tokens from an on-premises server or from `elm-service`. See [below](#leaseverifier). |
| `LicensedFeature` | Record | A feature of a verified license: `Id`, `VersionRange` (`null` for every version), `Seats` (server licenses only), and `ExpiresAt` (the feature's own expiry, or `null`). |
| `LicenseError` | Record | Why a check failed: `Code`, `Message`, and `WireName`. |
| `LicenseKind` | Enum | The kind of a license: `Standard`, `Trial`, or `Server`. |
| `LicenseRequirement` | Record struct | What the app requires of a license: `ProductId`, `Kind`, `FeatureId` (`null` checks no feature), `FeatureVersion` (`major.minor[.patch]`, or `null` to skip the version check), and `Installation` (`null` when the app uses only licenses not bound to a device). `Standard` also accepts a trial unless the options turn it off. |
| `LicenseVerificationResult` | Abstract record | The result of `LicenseVerifier.Verify`: the case `Valid` holds a `VerifiedLicense` (`License`) and whether it is past its expiry but inside its grace period (`InGracePeriod`); the case `Invalid` holds the `LicenseError` of the first check that failed (`Error`). |
| `LicenseVerifier` | Class | Verifies license files offline. See [below](#licenseverifier). |
| `LicenseVerifierOptions` | Class | The options of `LicenseVerifier` and `LeaseVerifier`. See [below](#licenseverifier). |
| `MachineFingerprint` | Static class | `Compute` returns this machine's fingerprints for a product, 32 lowercase hex characters each, or an empty list when the OS gives no machine ID (for example, a container without `/etc/machine-id`). The OS IDs are read once per process. |
| `ServerIdentity` | Class | An on-premises server as a client knows it: its verified server license (`License`) and the key ID of its key (`KeyId`). `TryVerify` verifies a server license together with the server's public key; `TryCreate` builds one from a server license already verified. Dispose it when it is no longer used. |
| `StatelessClockGuard` | Class | A clock guard without state, used as `StatelessClockGuard.Instance`. It accepts every time; the verifier still rejects a token issued in the future. |
| `TokenTypes` | Static class | The `typ` header values as constants: `KeyCertificate` (`elm-cert+jws`), `License` (`elm-license+jws`), `Lease` (`elm-lease+jws`), `Proof` (`elm-proof+jws`), `ActivationRequest` (`elm-actreq+jws`), `DeactivationProof` (`elm-deact+jws`), and `Log` (`elm-log+jws`). |
| `TrustStore` | Class | The vendor's root keys that the app trusts, and the key IDs it has revoked. See [below](#truststore). |
| `VerifiedLease` | Class | A lease token that passed every check: its IDs, feature, seats, the installation key it is bound to, whether it is a borrow, its sequence number, issue time, expiry, and the token itself. Only `LeaseVerifier` makes one. |
| `VerifiedLicense` | Class | A license that passed every check, with its claims in typed form: license ID, product, kind, features, token ID, signing key ID, issue time, expiry, grace period, refresh window, binding, sequence number, and for server licenses the lease duration, borrow limits, overage, and clone strategy, plus the vendor's `Metadata`. Only `LicenseVerifier` makes one, so code that holds one knows the checks passed. |

### `TrustStore`

The root keys the app trusts and the key IDs it has revoked. Build it from root public keys compiled into the app; [The trust store](../explanation/trust-chain.md#the-trust-store) explains why. It has no public constructor.

| Method | Parameters | Returns | What it does |
| --- | --- | --- | --- |
| `Create` | `ReadOnlySpan<string> rootPublicKeysPem`, `ReadOnlySpan<string> revokedKeyIds` | `TrustStore` | Creates a trust store from one or more P-256 public keys, each one PEM `PUBLIC KEY` block. A token signed by a revoked key ID fails with `ELM_KEY_REVOKED`, and a certificate signed by or issued for a revoked key ID is not trusted. Throws `ArgumentException` when no root key is given, a key is not a P-256 public key in one PEM block, a key is given twice, or a revoked key ID is empty. |

| Property | Type | What it is |
| --- | --- | --- |
| `RootKeyIds` | `IReadOnlyCollection<string>` | The key IDs of the trusted root keys. |

### `LicenseVerifier`

Verifies a license file offline, step by step in the order that [The order of checks](../explanation/license-files-and-verification.md#the-order-of-checks) gives, and reports the first check that failed.

| Constructor | Parameters |
| --- | --- |
| `LicenseVerifier` | `TrustStore trust`, `TimeProvider time`, `IClockGuard clockGuard`, `LicenseVerifierOptions? options = null`. The options are copied; `null` uses the defaults. Throws `ArgumentOutOfRangeException` when the clock skew is negative or more than one hour. |

| Method | Parameters | Returns | What it does |
| --- | --- | --- | --- |
| `Verify` | `ReadOnlySpan<byte> licenseFile`, `LicenseRequirement requirement` | `LicenseVerificationResult` | Verifies the bytes of a license file against what the app requires. Throws `ArgumentException` only when the requirement has an empty product ID or a feature version that is not `major.minor[.patch]`. |

The options, `LicenseVerifierOptions`:

| Property | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `ClockSkew` | `TimeSpan` | 5 minutes | No | The clock difference allowed between this machine and the issuer, from zero to `LicenseVerifierOptions.MaxClockSkew` (one hour). The constructor checks it. |
| `AcceptTrialAsStandard` | `bool` | `true` | No | Whether a requirement for `LicenseKind.Standard` also accepts a trial license. |

The sample below is the verifying part of the `VerifyLicense` sample program, which takes the root key file, the license file, the product ID, and optionally the feature and its version on the command line:

```csharp
TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
var requirement = new LicenseRequirement(args[2], LicenseKind.Standard, args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null, null);
return Report(verifier.Verify(File.ReadAllBytes(args[1]), requirement));
```

### `LeaseVerifier`

Checks a lease token when it arrives and before a borrowed lease is used; [What the client checks in every lease](../explanation/floating-licenses.md#what-the-client-checks-in-every-lease) lists the checks. `ElasticLM.Client` runs it on every lease, so an app that uses the client does not call it.

| Constructor | Parameters |
| --- | --- |
| `LeaseVerifier` | `TrustStore trust`, `TimeProvider time`, `IClockGuard clockGuard`, `LicenseVerifierOptions? options = null`. Throws `ArgumentOutOfRangeException` when the clock skew is negative or more than one hour. |

| Method | Parameters | Returns | What it does |
| --- | --- | --- | --- |
| `VerifyOnPremises` | `string leaseToken`, `ServerIdentity server`, `LeaseExpectation expect` | `LeaseVerificationResult` | Checks a lease from an on-premises server: signed by the server's key, issued under its current server license, for this product and installation key, inside the server license's features, seats, lease duration or borrow limit, and expiry, not issued in the future, past the clock guard, and not expired. |
| `VerifyHosted` | `string leaseToken`, `IReadOnlyList<string> certificates`, `LeaseExpectation expect` | `LeaseVerificationResult` | Checks a lease from `elm-service`: signed by a key that one of the 1 to 4 certificates sent with the lease certifies for leases under a trusted root, not revoked, for this product and installation key, not issued in the future, past the clock guard, and not expired. |

It takes the same options type, `LicenseVerifierOptions`, and uses only one of its properties:

| Property | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `ClockSkew` | `TimeSpan` | 5 minutes | No | The clock difference allowed, from zero to one hour. |
| `AcceptTrialAsStandard` | `bool` | `true` | No | Not used for leases. |

## `ElasticLM.Client`

The client for activation and leases. It depends on `ElasticLM.Licensing` and checks every license file and lease it receives or keeps with it. It creates and keeps the installation key, the device's key pair that signs each request ([Devices and proofs](../explanation/devices-and-proofs.md) explains it).

| Type | Kind | What it is |
| --- | --- | --- |
| `ActivationClient` | Class | A client of the hosted activation service `elm-service`. See [below](#activationclient). |
| `ActivationClientOptions` | Class | The settings of `ActivationClient`. See [below](#activationclient). |
| `ActivationResult` | Abstract record | The result of an activation, a trial, a refresh, or an imported offline answer: the case `Activated` holds the checked and kept license (`License`) and whether it is inside its grace period (`InGracePeriod`); the case `Failed` holds `Error` and `RetryAfter`. |
| `ActivationState` | Enum | The state of this installation's activation: `None` (none kept), `Active`, `RefreshFailing` (refreshes fail; the kept file is used until it ends), `Refused` (the service refused the license: revoked, suspended, or expired), or `CloneDetected` (the service found another copy of this installation). |
| `ActivationStateChangedEventArgs` | Class | The data of `ActivationClient.StateChanged`: `Previous`, `Current`, and `Error` (why, when a refusal or a failure caused the change, otherwise `null`). |
| `InstallationClockGuard` | Class | A clock guard that keeps its state in a file and signs it with its own copy of an installation key. `InstallationKey.CreateClockGuard` makes it; disposing it destroys the copy. |
| `InstallationKey` | Class | The installation key: `Thumbprint` (its JWK thumbprint, which licenses and leases bind to), `Identity` (the `InstallationIdentity` for a given list of fingerprints), and `CreateClockGuard`. Dispose it when it is no longer used. |
| `InstallationKeyStore` | Class | Creates and reads the installation key of a product: the constructor takes the product ID, the `KeyScope` (default `User`), and a folder (or `null` for the default); then `TryLoadOrCreate`, `TryLoad`, `Delete`, `Directory`, and `Scope`. A key that exists but cannot be read is never replaced, because a new key would use one more activation. |
| `KeyScope` | Enum | Where the installation key is kept: `User` (needs no administrator rights) or `Machine` (for apps that run as a service; writing needs administrator rights). |
| `Lease` | Class | A lease the client holds: `Id`, `FeatureId`, `Seats`, `Token` (the current `VerifiedLease`), `IsBorrow`, `ExpiresAt`, `State`, and `LastError` (of the last failed renewal, reclaim, or release). |
| `LeaseResult` | Abstract record | The result of acquiring or borrowing a lease: the case `Acquired` holds the `Lease`; the case `Failed` holds `Error` and `RetryAfter`. |
| `LeaseState` | Enum | The state of a lease: `Held` (renewed every third of its duration), `Retrying` (the last renewal failed), `Reclaiming` (the server restarted and the client is reclaiming it), `Lost` (expired, reclaim refused, or a clone detected; the app decides what to do), or `Released`. |
| `LeaseStateChangedEventArgs` | Class | The data of `LicenseClient.LeaseStateChanged`: `Lease`, `Previous`, `Current`, and `Error` (for `Retrying`, `Lost`, and a failed release; otherwise `null`). |
| `LicenseClient` | Class | A client that leases seats from an on-premises license server, or from `elm-service` with a license key. See [below](#licenseclient). |
| `LicenseClientOptions` | Class | The settings of `LicenseClient`. See [below](#licenseclient). |
| `OfflineRequestResult` | Abstract record | The result of making an offline request or deactivation proof: the case `Created` holds the file's text (`File`) and whether an earlier request was replaced (`Replaced`; tell the user to discard the earlier file); the case `Failed` holds `Error`. An activation request holds the license key, so keep it where others cannot read it. |
| `ProofBuilder` | Static class | `Create` makes the proof that signs one request with the installation key, and `HeaderName` is the header that carries it (`ELM-Proof`). For an app that sends its own requests; the clients make proofs themselves. |

### `ActivationClient`

Activates this installation with a license key or starts a trial, keeps the license file beside the installation key, refreshes it while the app runs, deactivates it, and checks it offline. A device that cannot reach the service makes request files and imports their answers instead. [Activation](../explanation/activation.md) explains the flows. The methods may be called from several threads; the calls of one client run one at a time, and the requests of one device are sent one at a time across its processes. The service's TLS certificate is checked by the operating system. There is no public constructor; create a client with `TryCreate` and dispose it with `DisposeAsync`.

| Method | Parameters | Returns | What it does |
| --- | --- | --- | --- |
| `TryCreate` | `ActivationClientOptions options`, `out ActivationClient? client`, `out LicenseError? error` | `bool` | Reads the installation key if there is one (the first activation or trial makes it), computes the machine fingerprints, reads the kept activation, and schedules its refresh. It does not contact the service. Fails with `ELM_INSTALLATION_KEY_UNREADABLE`. Throws `ArgumentException` for an invalid product ID, a service URL that is neither https nor http on a loopback address, or an app version over 64 bytes. |
| `ActivateAsync` | `string licenseKey`, `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | Activates this installation with a license key. A kept trial is replaced on success; any other kept activation answers `ELM_INVALID_STATE` without a request (deactivate first). A canceled request ends as `ELM_NETWORK_ERROR`. |
| `StartTrialAsync` | `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | Starts a trial of the product. With any activation kept, a trial included, it answers `ELM_INVALID_STATE` without a request. |
| `RefreshAsync` | `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | Refreshes the kept activation now. The client also refreshes on its own when the token's refresh window starts. |
| `DeactivateAsync` | `CancellationToken cancellationToken = default` | `Task<LicenseError?>` | Deactivates this installation: the service frees its place and the kept license file is removed. The installation key stays. An activation the service no longer has counts as deactivated. On failure nothing is removed. |
| `CreateActivationRequestAsync` | `string licenseKey`, `bool renew = false`, `CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | Makes an offline activation request (`.elmreq`). The same request comes back, signed again, until it expires after 7 days or its answer is imported. `renew` makes a new one before then, for when the service answered `ELM_REQUEST_EXPIRED` because the device's clock is behind. Answers `ELM_INVALID_STATE` with an activation kept that is not a trial, or with a deactivation proof kept. |
| `CreateRefreshRequestAsync` | `bool renew = false`, `CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | Makes an offline refresh request for the kept activation. The same request comes back until it expires or its answer is imported. Answers `ELM_INVALID_STATE` without an activation. |
| `ImportResponseAsync` | `string response`, `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | Imports the service's answer (`.elmresp`, at most 256 KiB) to the waiting request, checks it like an online answer, and keeps it. Fails with `ELM_REQUEST_MISMATCH` for an answer to another request and `ELM_MALFORMED` for text that is not an answer. Importing the kept answer again changes nothing. |
| `CreateDeactivationProofAsync` | `CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | Makes an offline deactivation proof (`.elmdeact`) and removes the kept activation; the installation key stays. The proof is kept and returned again until `ForgetDeactivationProofAsync`; until then, activations and trials are refused. Answers `ELM_INVALID_STATE` without an activation. |
| `ForgetDeactivationProofAsync` | `CancellationToken cancellationToken = default` | `Task<LicenseError?>` | Forgets the kept deactivation proof once the user has passed it to the service, so the installation may activate again. Without a proof it does nothing. |
| `Check` | `string? featureId`, `string? version`, `LicenseKind kind = LicenseKind.Standard` | `LicenseVerificationResult` | Checks the kept license file offline against this installation's key and its current machine fingerprints. It reads the file at each call and does not contact the service. Fails with `ELM_LICENSE_NOT_FOUND` when none is kept and `ELM_MALFORMED` when the kept file is damaged. |
| `DisposeAsync` | none | `ValueTask` | Stops the refresh timer, cancels a refresh that has started, and waits for it at most 5 seconds. The activation stays. A `StateChanged` handler may call it. |

| Property or event | Type | What it is |
| --- | --- | --- |
| `State` | `ActivationState` | The activation's state. |
| `ActivationId` | `string?` | The kept activation's ID, or `null`. |
| `StateChanged` | `EventHandler<ActivationStateChangedEventArgs>` | Raised when the state changes: an activation, a deactivation, a refresh that fails or recovers, a refusal, a detected clone, or an activation an administrator removed. It is raised outside the client's lock, exceptions from a handler are ignored, and it is not raised after `DisposeAsync`. |

The settings, `ActivationClientOptions` (each property is set with `init`):

| Property | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `ProductId` | `string` | none | Yes | The product's `code`: 1 to 64 lowercase letters, digits, and `-`. It also names the installation key's folder. |
| `ServiceUrl` | `Uri?` | `null` | No | The URL of `elm-service`: https, or http on a loopback address for tests. `null` is for a device that never reaches the service and uses only offline files; the online operations then answer `ELM_INVALID_STATE` and nothing is refreshed on its own. |
| `Trust` | `TrustStore` | none | Yes | The vendor's root keys, which the license files are checked against. |
| `KeyScope` | `KeyScope` | `KeyScope.User` | No | Where the installation key and the activation are kept. |
| `DataDirectory` | `string?` | `null` | No | The folder of the installation key and the activation, or `null` for the default folder of the scope ([Where the key is stored](../explanation/devices-and-proofs.md#where-the-key-is-stored)). |
| `SendClientInfo` | `bool` | `false` | No | Whether the host name and the OS user name are sent. The service keeps them only when the policy's `clientInfoStrategy` is `hostAndUser`. |
| `AppVersion` | `string?` | `null` | No | The app's version, sent for the audit log; at most 64 bytes in UTF-8. |
| `ClockGuard` | `IClockGuard?` | `null` | No | The clock guard, or `null` for a `FileClockGuard` in the file `clock` in the installation key's folder, signed with the installation key. |
| `VerifierOptions` | `LicenseVerifierOptions?` | `null` | No | The verifier's options, or `null` for their defaults. |
| `TimeProvider` | `TimeProvider` | `TimeProvider.System` | No | The clock. |

### `LicenseClient`

Leases seats from an on-premises license server: it checks the server's license against the vendor's trust store and its pin, acquires leases, renews each every third of its duration, reclaims them after a server restart, and returns them. With `LicenseKey` set it leases from `elm-service` instead: TLS is checked with the OS's trust, each lease with the certificates that come with it, and a lease the service does not know is lost, not reclaimed. [Floating licenses](../explanation/floating-licenses.md) explains leases, renewal, reclaim, and borrowing. The methods may be called from several threads; requests to the server are made one at a time. There is no public constructor; create a client with `TryCreate` and dispose it with `DisposeAsync`.

| Method | Parameters | Returns | What it does |
| --- | --- | --- | --- |
| `TryCreate` | `LicenseClientOptions options`, `out LicenseClient? client`, `out LicenseError? error` | `bool` | Loads the installation key, or creates it when there is none, and computes the machine fingerprints. It does not contact the server. Fails with `ELM_INSTALLATION_KEY_UNREADABLE`. Throws `ArgumentException` for an invalid product ID, a server URL that is not absolute https (with a license key, http on a loopback address is also allowed), an empty license key or one given with `ExpectedServerLicenseId`, or an app version over 64 bytes. |
| `AcquireLeaseAsync` | `string featureId`, `string? version = null`, `int count = 1`, `CancellationToken cancellationToken = default` | `Task<LeaseResult>` | Acquires a lease of `count` seats and starts renewing it. From an on-premises server, the first call fetches and checks the server license and the server's key. A canceled request ends as `ELM_NETWORK_ERROR`. Throws `ArgumentOutOfRangeException` when `count` is less than 1. |
| `BorrowLeaseAsync` | `string featureId`, `TimeSpan period`, `string? version = null`, `int count = 1`, `CancellationToken cancellationToken = default` | `Task<LeaseResult>` | Borrows a lease for a period of at least one second; the server refuses more than it allows. A borrow is not renewed, is valid until it expires, and is saved beside the installation key with what verifies it. |
| `GetBorrowedLeases` | none | `IReadOnlyList<Lease>` | Reads the borrows saved for this client's server URL and mode (at most 1,000 files) and checks each one as when it was borrowed, without a request. Expired borrows are deleted; a file that does not verify is left alone and not returned. |
| `ReleaseLeaseAsync` | `Lease lease`, `CancellationToken cancellationToken = default` | `Task<LicenseError?>` | Returns a lease and stops renewing it. A lease that is not a borrow is `Released` afterwards even when the server could not be reached; the server drops it at its expiry. A borrow the server refuses or cannot be reached for, other than with `ELM_UNKNOWN_LEASE`, keeps its state and its file. A lease already lost or released is not sent again and returns `null`. Throws `ArgumentException` for a lease of another client. |
| `ResetServerPin` | `Uri serverUrl` | `bool` | Deletes the pin of a server that was moved to another machine on purpose; the next request checks the server as on the first connection. Returns `false` when there was no pin or the file cannot be deleted, and always for a client with a license key. |
| `DisposeAsync` | none | `ValueTask` | Stops the renewal timers, returns every lease that is not a borrow (leases not returned within 5 seconds end at their expiry; failures are ignored), and releases the key and the connections. Borrows stay. |

| Property or event | Type | What it is |
| --- | --- | --- |
| `InstallationKeyThumbprint` | `string` | The thumbprint of this installation's key, which leases are bound to. |
| `LeaseStateChanged` | `EventHandler<LeaseStateChangedEventArgs>` | Raised when a lease changes state: renewals that fail or recover, reclaims, losses, and releases. It is raised on a timer thread outside the client's lock, so a handler may call `ReleaseLeaseAsync`; exceptions from a handler on a timer thread are ignored. |

The settings, `LicenseClientOptions` (each property is set with `init`; `TryCreate` takes a copy):

| Property | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `ProductId` | `string` | none | Yes | The app's product ID: 1 to 64 lowercase letters, digits, and `-`. |
| `ServerUrl` | `Uri` | none | Yes | The URL of the on-premises license server, which must be https (for example `https://licenses.example:7070/`); with `LicenseKey`, the URL of `elm-service` (https, or http on a loopback address for tests). |
| `LicenseKey` | `string?` | `null` | No | The license key to lease from `elm-service`, or `null` for an on-premises server. With a key, the client keeps no server pin. The key is held in memory only and sent only when a lease is acquired. |
| `Trust` | `TrustStore` | none | Yes | The vendor's trust store, which verifies the server license, or with `LicenseKey` the certificates of the service's leases. |
| `ExpectedServerLicenseId` | `string?` | `null` | No | The server license ID the server must hold, or `null` to accept the first one seen and require it afterwards. With an ID, a server moved to another machine is accepted under the same ID. Not allowed with `LicenseKey`. |
| `KeyScope` | `KeyScope` | `KeyScope.User` | No | Where the installation key and the server pins are kept. |
| `DataDirectory` | `string?` | `null` | No | The folder of the installation key and the pins, or `null` for the default folder of the scope ([Where the key is stored](../explanation/devices-and-proofs.md#where-the-key-is-stored)). |
| `SendClientInfo` | `bool` | `false` | No | Whether the host name and the OS user name are sent with each new lease. On premises, `host:` and `user:` entries of the server's [access rules](server-configuration.md#access-rules) use them; the client states them itself and the server cannot check them. |
| `AppVersion` | `string?` | `null` | No | The app version sent with each new lease; at most 64 bytes in UTF-8. |
| `ClockGuard` | `IClockGuard?` | `null` | No | The clock guard for the server license and the leases, or `null` for a `FileClockGuard` in the file `clock` in the installation key's folder, signed with the installation key. |
| `VerifierOptions` | `LicenseVerifierOptions?` | `null` | No | The verifier's options (the clock skew), or `null` for their defaults. |
| `TimeProvider` | `TimeProvider` | `TimeProvider.System` | No | The clock. |
