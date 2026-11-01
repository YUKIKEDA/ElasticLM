---
sidebar_position: 9
---

# .NET API

このページは、.NET のアプリに組み込む 2 つの NuGet パッケージの公開 API をまとめたものだ。`ElasticLM.Licensing` は、ライセンスファイルとリーストークンをオフラインで検証する。`ElasticLM.Client` は、`elm-service` でデバイスをアクティベーションし、そのライセンスファイルを保存し、`elm-server` か `elm-service` からシートのリースを受ける。型、メソッド、オプションを調べるアプリ開発者に向けている。リリースのたびに、両方のパッケージを nuget.org に公開する。1.0 はまだリリースしていないので、このページは `main` ブランチの API を説明している。すべての型とメンバーに XML ドキュメントがあり、パッケージに含まれていて IDE に表示される。このページでは各型を 1 行で説明し、入り口になる型はもっと詳しく説明する。どんな確認をなぜするのかは、[ライセンスファイルと検証](../explanation/license-files-and-verification.md)、[アクティベーション](../explanation/activation.md)、[フローティングライセンス](../explanation/floating-licenses.md)で説明している。

## 失敗の伝え方

ライセンス、トークン、サーバーのレスポンスが確認に通らなくても、中身が何であれ例外は投げない。失敗は値として返る。

| 戻り値 | 成功 | 失敗 |
| --- | --- | --- |
| `LicenseVerificationResult` | `License` と `InGracePeriod` を持つ `LicenseVerificationResult.Valid` | `Error` を持つ `LicenseVerificationResult.Invalid` |
| `LeaseVerificationResult` | `Lease` を持つ `LeaseVerificationResult.Valid` | `Error` を持つ `LeaseVerificationResult.Invalid` |
| `ActivationResult` | `License`（`VerifiedLicense`）と `InGracePeriod`（`bool`）を持つ `ActivationResult.Activated` | `Error` と `RetryAfter`（`TimeSpan?`）を持つ `ActivationResult.Failed` |
| `OfflineRequestResult` | `File` と `Replaced` を持つ `OfflineRequestResult.Created` | `Error` を持つ `OfflineRequestResult.Failed` |
| `LeaseResult` | `Lease` を持つ `LeaseResult.Acquired` | `Error` と `RetryAfter`（`TimeSpan?`）を持つ `LeaseResult.Failed` |
| `Task<LicenseError?>` | `null` | その `LicenseError` |
| `bool` と `out LicenseError?`（`TryCreate`、`TryVerify`） | `true` | `false` とそのエラー |

どの結果の型もこの 2 つの場合しか持たないので、`switch` でこの 2 つを扱えば漏れはない。`Error` は `LicenseError` だ。`Code` は `ErrorCode` の値、`WireName` はその名前（`ELM_EXPIRED` など）、`Message` は人が読むための英語の一文で、鍵の情報やトークン全体を含むことはない。[エラーコード](error-codes.md)に、すべてのコードと、アプリに返す前にクライアントが自分で再試行するコードを挙げている。`RetryAfter` は `TimeSpan?` で、サーバーがレスポンスの `Retry-After` ヘッダーで求めた待ち時間だ。`ELM_SERVER_RECOVERING` や `ELM_RATE_LIMITED` のときにサーバーはこれを求める。求めていなければ `null` になる。クライアントは自分では待たない。

例外は、呼び出し方の誤りにだけ使う。`ArgumentException`（空の引数や正しくない引数。製品 ID や URL など）、`ArgumentOutOfRangeException`（1 より少ないシート数、1 秒より短い借用の期間、範囲外の時刻のずれ）、`ArgumentNullException`（`null` の引数、またはオプションの `Trust` や `TimeProvider` が `null`）、クライアントを破棄したあとの `ObjectDisposedException` だ。各メンバーの XML ドキュメントに、そのメンバーが投げる例外を書いている。

## `ElasticLM.Licensing`

検証のライブラリ。.NET の基本クラスライブラリのほかには何にも依存せず、ネットワークを使わない。

| 型 | 種類 | 説明 |
| --- | --- | --- |
| `CloneStrategy` | 列挙型 | クローンしたインストールを検出したときにサーバーがどうするか。`Deny`（止めて `ELM_CLONE_DETECTED` を返す）か `Flag`（記録して、通常どおり返す）。 |
| `ErrorCode` | 列挙型 | ElasticLM のすべてのエラーコードと、その番号（`Ok` は 0、`Malformed` は 2）。番号と名前は、一度公開したら変えない。 |
| `ErrorCodes` | 静的クラス | `ErrorCode` と通信で使う名前を相互に変換する。`ToWireName` と `TryParseWireName`。 |
| `FileClockGuard` | クラス | 最後に確認した時刻をファイルに保存する時刻巻き戻し対策。[後述](#fileclockguard)。 |
| `HttpTargetUri` | 静的クラス | `Normalize` は、プルーフが署名するリクエストの URI の形を返す。スキームとホストは小文字にし、既定のポートを省き、パーセントエンコードは大文字にし、クエリとフラグメントを除く。 |
| `IClockGuard` | インターフェース | 時刻巻き戻し対策。`Observe` は、現在時刻を最後に確認した時刻と比べ、確認に通ればその時刻を記録する。`AcceptServerTime` は、オンラインでのトークンの更新のあとにサービスの時刻を受け取る（既定では何もしない）。 |
| `InstallationIdentity` | クラス | デバイスに紐付いたライセンスのための、このインストール。インストール鍵のサムプリントと、現在のマシンフィンガープリントを持つ。[後述](#installationidentity)。 |
| `KeyIds` | 静的クラス | `Of` は P-256 の鍵の鍵 ID を返す。鍵の JWK サムプリント（RFC 7638）を base64url にしたものだ。 |
| `LeaseExpectation` | レコード | リースが対象としていなければならないもの。`ProductId` と `InstallationKeyThumbprint`。 |
| `LeaseVerificationResult` | 抽象レコード | リースの確認の結果。`Valid` の場合は `VerifiedLease`（`Lease`）を、`Invalid` の場合は `LicenseError`（`Error`）を持つ。 |
| `LeaseVerifier` | クラス | オンプレミスのサーバーか `elm-service` から受け取ったリーストークンを確かめる。[後述](#leaseverifier)。 |
| `LicensedFeature` | レコード | 検証したライセンスの機能。`Id`、`VersionRange`（すべてのバージョンなら `null`）、`Seats`（サーバーライセンスだけ）、`ExpiresAt`（機能自体の有効期限、または `null`）。 |
| `LicenseError` | レコード | 確認に失敗した理由。`Code`、`Message`、`WireName`。 |
| `LicenseKind` | 列挙型 | ライセンスの種類。`Standard`、`Trial`、`Server` のいずれか。 |
| `LicenseRequirement` | レコード構造体 | アプリがライセンスに求めること。`ProductId`、`Kind`、`FeatureId`（`null` なら機能を確かめない）、`FeatureVersion`（`major.minor[.patch]`。`null` ならバージョンを確かめない）、`Installation`（デバイスに紐付かないライセンスだけを使うアプリでは `null`）。オプションで無効にしない限り、`Standard` はトライアルも受け入れる。 |
| `LicenseVerificationResult` | 抽象レコード | `LicenseVerifier.Verify` の結果。`Valid` の場合は、`VerifiedLicense`（`License`）と、有効期限を過ぎて猶予期間内にあるかどうか（`InGracePeriod`）を持つ。`Invalid` の場合は、最初に失敗した確認の `LicenseError`（`Error`）を持つ。 |
| `LicenseVerifier` | クラス | ライセンスファイルをオフラインで検証する。[後述](#licenseverifier)。 |
| `LicenseVerifierOptions` | クラス | `LicenseVerifier` と `LeaseVerifier` のオプション。[後述](#licenseverifier)。 |
| `MachineFingerprint` | 静的クラス | `Compute` は、製品ごとのこのマシンのフィンガープリントを返す。それぞれ小文字の 16 進数 32 文字だ。OS がマシン ID を返さないとき（たとえば `/etc/machine-id` のないコンテナ）は空のリストを返す。OS の ID はプロセスごとに 1 回だけ読む。 |
| `ServerIdentity` | クラス | クライアントから見たオンプレミスのサーバー。検証したサーバーライセンスと、サーバーの鍵の鍵 ID を持つ。[後述](#serveridentity)。 |
| `StatelessClockGuard` | クラス | 状態を持たない時刻巻き戻し対策で、`StatelessClockGuard.Instance` として使う。どの時刻も受け入れるので、時計を戻しても検出しない。それでも、未来に発行されたトークンは検証側が拒否する。 |
| `TokenTypes` | 静的クラス | `typ` ヘッダーの値を定数で持つ。`KeyCertificate`（`elm-cert+jws`）、`License`（`elm-license+jws`）、`Lease`（`elm-lease+jws`）、`Proof`（`elm-proof+jws`）、`ActivationRequest`（`elm-actreq+jws`）、`DeactivationProof`（`elm-deact+jws`）、`Log`（`elm-log+jws`）。 |
| `TrustStore` | クラス | アプリが信頼するベンダーのルート鍵と、失効させた鍵 ID。[後述](#truststore)。 |
| `VerifiedLease` | クラス | すべての確認に通ったリーストークン。各 ID、機能、シート数、紐付いたインストール鍵、借用かどうか、シーケンス番号、発行時刻、有効期限、トークンそのものを持つ。作れるのは `LeaseVerifier` だけだ。 |
| `VerifiedLicense` | クラス | すべての確認に通ったライセンス。クレームを型付きで持つ。ライセンス ID、製品、種類、機能、トークン ID、署名鍵の ID、発行時刻、有効期限、猶予期間、トークンの更新を始める期間、紐付け、シーケンス番号、応えるオフラインのリクエストのノンス、サーバーライセンスではリースの期間、借用の上限、超過の割合、クローンへの対応、そしてベンダーの `Metadata` だ。作れるのは `LicenseVerifier` だけなので、これを持つコードは確認に通ったと分かる。 |

### `TrustStore`

アプリが信頼するルート鍵と、失効させた鍵 ID。アプリに組み込んだルート公開鍵から作る。その理由は[トラストストア](../explanation/trust-chain.md#トラストストア)で説明している。公開のコンストラクターはない。`Create` は、ルート鍵の数にも失効させた鍵 ID の数にも上限を設けない。[C API](c-api.md) は、ルート鍵を 1 から 16 個、失効させた鍵 ID を 0 から 1024 個受け取る。

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `Create` | `ReadOnlySpan<string> rootPublicKeysPem`、`ReadOnlySpan<string> revokedKeyIds` | `TrustStore` | 1 つ以上の P-256 公開鍵からトラストストアを作る。鍵はそれぞれ PEM の `PUBLIC KEY` ブロック 1 つで渡す。失効させた鍵 ID で署名したライセンストークンや、`elm-service` のリーストークンは、`ELM_KEY_REVOKED` で失敗する。失効させた鍵 ID が署名した鍵証明書や、失効させた鍵 ID のために発行した鍵証明書は、信頼できないものとして飛ばす。そのため、ほかに鍵証明書のないトークンは `ELM_UNTRUSTED_KEY` で失敗する。ルート鍵が 1 つもない、鍵が 1 つの PEM ブロックに入った P-256 公開鍵でない、同じ鍵を 2 回渡した、失効させた鍵 ID が空、のどれかなら `ArgumentException` を、失効させた鍵 ID が `null` なら `ArgumentNullException` を投げる。 |

| プロパティ | 型 | 説明 |
| --- | --- | --- |
| `RootKeyIds` | `IReadOnlyCollection<string>` | 信頼するルート鍵の鍵 ID。 |

### `LicenseVerifier`

ライセンスファイルをオフラインで検証する。[確かめる順序](../explanation/license-files-and-verification.md#確かめる順序)に書いた順に 1 つずつ確かめ、最初に失敗した確認を返す。

| コンストラクター | 引数 |
| --- | --- |
| `LicenseVerifier` | `TrustStore trust`、`TimeProvider time`、`IClockGuard clockGuard`、`LicenseVerifierOptions? options = null`。オプションはコピーする。`null` なら既定値を使う。時刻のずれが負か 1 時間を超えるときは `ArgumentOutOfRangeException` を投げる。 |

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `Verify` | `ReadOnlySpan<byte> licenseFile`、`LicenseRequirement requirement` | `LicenseVerificationResult` | ライセンスファイルのバイト列を、アプリが求めることに照らして検証する。`ArgumentException` を投げるのは、求める製品 ID が空のときと、求める機能のバージョンが `major.minor[.patch]` の形でないときだけだ。 |

オプションは `LicenseVerifierOptions` で指定する（どのプロパティにも公開の `set` がある。コンストラクターが値をコピーするので、あとから変えても反映されない）。

| プロパティ | 型 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `ClockSkew` | `TimeSpan` | 5 分 | いいえ | このマシンと発行元のあいだで許す時刻のずれ。0 から `LicenseVerifierOptions.MaxClockSkew`（1 時間）まで。コンストラクターが確かめる。 |
| `AcceptTrialAsStandard` | `bool` | `true` | いいえ | `LicenseKind.Standard` を求めたときに、トライアルのライセンスも受け入れるかどうか。 |

下のコードは、[ライセンスを発行して .NET のアプリで確かめる](../tutorials/verify-a-license-offline.md)で作るアプリのうち検証する部分で、アプリが呼ぶ順に並んでいる。このアプリは、ルート鍵の公開鍵のファイルとライセンスファイルをコマンドラインで受け取り、製品 `acme-cad` とその機能 `pro` を求める。製品と機能は、定数 `ProductId` と `FeatureId` に持っている。

<!-- sample: samples/VerifyOffline/Program.cs#verify -->
```csharp
TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
var requirement = new LicenseRequirement(ProductId, LicenseKind.Standard, FeatureId, FeatureVersion: null, Installation: null);
LicenseVerificationResult result = verifier.Verify(File.ReadAllBytes(args[1]), requirement);
```

このサンプルは `StatelessClockGuard` を渡している。これは状態を持たないので、時計を戻しても検出しない。状態を持つ時刻巻き戻し対策（`FileClockGuard` か `InstallationClockGuard`）が何を検出し、何を検出しないかは、[時刻巻き戻しの検出と、その限界](../explanation/license-files-and-verification.md#時刻巻き戻しの検出とその限界)で説明している。

### `LeaseVerifier`

リーストークンを、受け取ったときと、借用したリースを使う前に確かめる。確認の内容は[クライアントがリースごとに確かめること](../explanation/floating-licenses.md#クライアントがリースごとに確かめること)に挙げている。`ElasticLM.Client` がすべてのリースでこれを実行するので、クライアントを使うアプリは呼ばなくてよい。

| コンストラクター | 引数 |
| --- | --- |
| `LeaseVerifier` | `TrustStore trust`、`TimeProvider time`、`IClockGuard clockGuard`、`LicenseVerifierOptions? options = null`。時刻のずれが負か 1 時間を超えるときは `ArgumentOutOfRangeException` を投げる。 |

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `VerifyOnPremises` | `string leaseToken`、`ServerIdentity server`、`LeaseExpectation expect` | `LeaseVerificationResult` | オンプレミスのサーバーから受け取ったリースを確かめる。サーバーの鍵で署名されていること、サーバーの現在のサーバーライセンスのもとで発行されたこと、この製品とインストール鍵のためのものであること、サーバーライセンスの機能、シート数、リースの期間か借用の上限、有効期限の範囲内であること、未来に発行されていないこと、時刻巻き戻し対策に通ること、期限が切れていないことを確かめる。 |
| `VerifyHosted` | `string leaseToken`、`IReadOnlyList<string> certificates`、`LeaseExpectation expect` | `LeaseVerificationResult` | `elm-service` から受け取ったリースを確かめる。信頼するルート鍵のもとで、リースと一緒に送られた 1 から 4 個の鍵証明書のどれかがリース用に証明した鍵で署名されていること、その鍵が失効していないこと、その鍵証明書の有効期間内に発行されたこと、この製品とインストール鍵のためのものであること、未来に発行されていないこと、時刻巻き戻し対策に通ること、期限が切れていないことを確かめる。 |

オプションには同じ型の `LicenseVerifierOptions` を受け取るが、コピーするのは `ClockSkew` だけだ。`AcceptTrialAsStandard` はリースには影響しない。

### `InstallationIdentity`

ノードロックのライセンスが一致しなければならないインストール。`LicenseRequirement.Installation` に渡す。紐付けの確認は[ライセンスファイルと検証](../explanation/license-files-and-verification.md#製品機能マシンへの紐付け)に書いている。

| コンストラクター | 引数 |
| --- | --- |
| `InstallationIdentity` | `string keyThumbprint`、`IReadOnlyList<string> fingerprints`。フィンガープリントのリストは空でもよい。サムプリントが空なら `ArgumentException` を、どちらかの引数が `null` なら `ArgumentNullException` を投げる。 |

| プロパティ | 型 | 説明 |
| --- | --- | --- |
| `KeyThumbprint` | `string` | インストール鍵のサムプリント。トークンの `cnf.jkt` と比べる。 |
| `Fingerprints` | `IReadOnlyList<string>` | マシンフィンガープリント。トークンの `fp` と比べる。 |

それぞれの部分は次のメンバーから得る。

| 部分 | パッケージ | メンバー |
| --- | --- | --- |
| インストール鍵とそのサムプリント | `ElasticLM.Client` | `InstallationKeyStore.TryLoadOrCreate` か `TryLoad` が `InstallationKey` を返す。その `Thumbprint` プロパティがサムプリントだ。 |
| 識別情報の全体 | `ElasticLM.Client` | メソッド `InstallationKey.Identity(IReadOnlyList<string> fingerprints)` が、鍵のサムプリントとそのフィンガープリントを持つ `InstallationIdentity` を返す。 |
| マシンフィンガープリント | `ElasticLM.Licensing` | `MachineFingerprint.Compute(string productId)`。 |
| 任意の P-256 の鍵のサムプリント | `ElasticLM.Licensing` | `KeyIds.Of(ECDsa key)`。 |

`ElasticLM.Licensing` は、インストール鍵を作りも保存もしない。このパッケージだけを使うアプリは、フィンガープリントだけに紐付いたライセンス（`cnf` のない `fp`）なら検証できる。鍵に紐付いたライセンス（`cnf`）は、アプリが自分で保存する鍵のサムプリントがあるときだけ検証できる。

### `FileClockGuard`

アプリが渡したパスのファイルに、最後に確認した時刻を保存する時刻巻き戻し対策。既定のパスはない。`IDisposable` ではない。

| コンストラクター | 引数 |
| --- | --- |
| `FileClockGuard` | `string path`、`ECDsa? installationKey = null`。`path` のフォルダーは存在していなければならない。秘密鍵の部分を含む P-256 の鍵を渡すと、その鍵で状態に署名し、署名の検証に失敗した状態は存在しないものとして扱う。鍵はコピーも破棄もしないので、この時刻巻き戻し対策を使うあいだ、鍵を使える状態にしておく必要がある。鍵を渡さなければ状態に署名しないので、ファイルに書き込める人なら誰でも時刻巻き戻し対策をリセットできる。`path` が空なら `ArgumentException` を投げる。 |

状態がない、書き換えられている、または読めないときは、トークンの発行時刻を代わりに使う。ファイルに書き込めないときは、時刻をメモリにだけ持ち、60 秒後にもう一度書き込もうとする。

`InstallationKey` の `ECDsa` は公開されていない。`ElasticLM.Client` のインストール鍵で状態に署名するには、`InstallationKey.CreateClockGuard(string path)` を呼ぶ。これが返す `InstallationClockGuard` は、鍵の自分用のコピーを持って `FileClockGuard` と同じように状態を保存し、破棄されるとそのコピーを消す。

### `ServerIdentity`

クライアントから見たオンプレミスのサーバー。`LicenseClient` は、使うサーバーごとにこれを作る。`LeaseVerifier.VerifyOnPremises` で自分でリースを確かめるアプリは、次のメソッドで作る。使い終わったら破棄する。

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `TryVerify` | `LicenseVerifier verifier`、`ReadOnlySpan<byte> licenseFile`、`ReadOnlySpan<byte> publicKeyJwk`、`string productId`、`out ServerIdentity? identity`、`out LicenseError? error` | `bool` | サーバーライセンスのファイルを、指定した製品の、種類が `Server` のライセンスとして検証する。フィンガープリントは比べない（サーバーが自分のマシンで確かめる）。また、JWK で渡したサーバーの公開鍵のサムプリントが、ライセンスの `cnf.jkt` と一致することを求める。検証のエラー、鍵が P-256 の JWK でなければ `ELM_MALFORMED`、別の鍵なら `ELM_SERVER_PIN_MISMATCH` で失敗する。`verifier` が `null` なら `ArgumentNullException` を、`productId` が空なら `ArgumentException` を投げる。 |
| `TryCreate` | `VerifiedLicense license`、`ReadOnlySpan<byte> publicKeyJwk`、`out ServerIdentity? identity`、`out LicenseError? error` | `bool` | 検証済みのサーバーライセンスから作る。サーバーライセンスでないライセンスなら `ELM_WRONG_KIND`、鍵が P-256 の JWK でなければ `ELM_MALFORMED`、別の鍵なら `ELM_SERVER_PIN_MISMATCH` で失敗する。`license` が `null` なら `ArgumentNullException` を投げる。 |
| `Dispose` | なし | `void` | サーバーの鍵を解放する。 |

| プロパティ | 型 | 説明 |
| --- | --- | --- |
| `License` | `VerifiedLicense` | サーバーライセンス。 |
| `KeyId` | `string` | サーバーの鍵の鍵 ID。ライセンスの `cnf.jkt` と同じ値だ。 |

## `ElasticLM.Client`

アクティベーションとリースのクライアント。`ElasticLM.Licensing` に依存し、受け取ったり保存したりするライセンスファイルとリースを、すべてそれで確かめる。インストール鍵を作って保存する。インストール鍵は、各リクエストに署名するデバイスの鍵ペアだ（[デバイスとプルーフ](../explanation/devices-and-proofs.md)で説明している）。

| 型 | 種類 | 説明 |
| --- | --- | --- |
| `ActivationClient` | クラス | SaaS 型のアクティベーションサービス `elm-service` のクライアント。[後述](#activationclient)。 |
| `ActivationClientOptions` | クラス | `ActivationClient` の設定。[後述](#activationclient)。 |
| `ActivationResult` | 抽象レコード | アクティベーション、トライアル、トークンの更新、またはインポートしたオフラインのレスポンスの結果。`Activated` の場合は、確かめて保存したライセンス（`License`）と、猶予期間内かどうか（`InGracePeriod`）を持つ。`Failed` の場合は `Error` と `RetryAfter` を持つ。 |
| `ActivationState` | 列挙型 | このインストールのアクティベーションの状態。`None`（何も保存していない）、`Active`、`RefreshFailing`（トークンの更新に失敗している。保存したファイルを期限まで使う）、`Refused`（サービスがライセンスを拒否した。失効、一時停止、または期限切れ）、`CloneDetected`（サービスがこのインストールの別のコピーを見つけた。自動のトークンの更新は止まる）のいずれか。`Refused` と `CloneDetected` でも、保存したファイルは期限まで使う。`Check` はファイルの有効期限と猶予期間が終わるまで `Valid` を返し続けるので、どうするかはアプリが決める。 |
| `ActivationStateChangedEventArgs` | クラス | `ActivationClient.StateChanged` のデータ。`Previous`、`Current`、`Error`（拒否や失敗で状態が変わったときはその理由、それ以外は `null`）。 |
| `InstallationClockGuard` | クラス | `FileClockGuard` と同じように状態をファイルに保存し、インストール鍵の自分用のコピーで署名する時刻巻き戻し対策。`InstallationKey.CreateClockGuard` が作る。破棄するとコピーを消す。 |
| `InstallationKey` | クラス | インストール鍵。[後述](#installationkey)。 |
| `InstallationKeyStore` | クラス | 製品のインストール鍵を作り、読む。[後述](#installationkeystore)。 |
| `KeyScope` | 列挙型 | インストール鍵を保存する場所。`User`（管理者権限は要らない）か `Machine`（サービスとして動くアプリ向け。書き込みには管理者権限が要る）。 |
| `Lease` | クラス | クライアントが持つリース。`Id`、`FeatureId`、`Seats`、`Token`（現在の `VerifiedLease`）、`IsBorrow`、`ExpiresAt`、`State`、`LastError`（最後に失敗したリースの更新かリースの再登録のエラー、またはサーバーが確認しなかった返却のエラー。サーバーが引き取らなかった借用は、前のエラーを保つ）。 |
| `LeaseResult` | 抽象レコード | リースの取得か借用の結果。`Acquired` の場合は `Lease` を、`Failed` の場合は `Error` と `RetryAfter` を持つ。 |
| `LeaseState` | 列挙型 | リースの状態。`Held`（有効。借用でないリースは、期間の 3 分の 1 ごとに更新する）、`Retrying`（最後のリースの更新に失敗した）、`Reclaiming`（サーバーが再起動したので、クライアントがリースを再登録している）、`Lost`（借用でないリースがリースの更新に成功する前に期限切れになった、リースの再登録を拒否された、`elm-service` がそのリースをもう知らなかった、またはクローンを検出した。どうするかはアプリが決める）、`Released`（アプリが返却した、または `DisposeAsync` が返却した。借用が返却済みになるのは、サーバーが引き取ったときか、サーバーがその借用を知らないときだけ）のいずれか。借用は期限切れで `Lost` になることはなく `Held` のままなので、`ExpiresAt` を現在時刻と比べる。 |
| `LeaseStateChangedEventArgs` | クラス | `LicenseClient.LeaseStateChanged` のデータ。`Lease`、`Previous`、`Current`、`Error`（`Retrying`、`Lost`、サーバーが確認しなかった返却のあとの `Released` のとき。それ以外は `null`）。サーバーが引き取らなかった借用は状態を保ち、イベントは発生しない。 |
| `LicenseClient` | クラス | オンプレミスのライセンスサーバーから、またはライセンスキーを使って `elm-service` から、シートのリースを受けるクライアント。[後述](#licenseclient)。 |
| `LicenseClientOptions` | クラス | `LicenseClient` の設定。[後述](#licenseclient)。 |
| `OfflineRequestResult` | 抽象レコード | オフラインのリクエストかアクティベーション解除のプルーフを作った結果。`Created` の場合は、ファイルのテキスト（`File`）と、待っていたリクエストを置き換えたかどうか（`Replaced`）を持つ。置き換えたときは、前のファイルを捨てるようユーザーに伝える。このファイルのあとで前のファイルを受け取ると、サービスはクローンと見なすからだ。アクティベーション解除のプルーフでは常に `false` になる。`Failed` の場合は `Error` を持つ。アクティベーションのリクエストにはライセンスキーが入るので、他人が読めない場所に置く。 |
| `ProofBuilder` | 静的クラス | `Create` は、1 つのリクエストにインストール鍵で署名するプルーフを作る。`HeaderName` は、それを運ぶヘッダーの名前（`ELM-Proof`）だ。自分でリクエストを送るアプリ向けで、クライアントは自分でプルーフを作る。 |

### `InstallationKeyStore`

製品のインストール鍵を作り、読む。`ActivationClient` と `LicenseClient` は自分でこれを使う。アプリがこれを使うのは、鍵のサムプリントを表示するときや、`LicenseVerifier` に渡す `InstallationIdentity` を作るときだ。鍵があるのに読めないときは、鍵を置き換えない。新しい鍵にすると、アクティベーションを 1 つ余分に使うからだ。

| コンストラクター | 引数 |
| --- | --- |
| `InstallationKeyStore` | `string productId`、`KeyScope scope = KeyScope.User`、`string? directory = null`（`null` ならスコープの[既定のフォルダー](../explanation/devices-and-proofs.md#鍵の保存場所)）。製品 ID が 1 から 64 文字の小文字、数字、`-` でない、またはスコープが定義されていない値なら `ArgumentException` を、製品 ID が `null` なら `ArgumentNullException` を投げる。 |

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `TryLoadOrCreate` | `out InstallationKey? key`、`out LicenseError? error` | `bool` | 鍵を読む。鍵のファイルがなければ、鍵とそのフォルダーを作る。鍵があるのに読めないとき、または作れないときは `ELM_INSTALLATION_KEY_UNREADABLE` で失敗する。鍵は呼び出し側が破棄する。 |
| `TryLoad` | `out InstallationKey? key`、`out LicenseError? error` | `bool` | 鍵を読む。鍵がないか読めないときは `ELM_INSTALLATION_KEY_UNREADABLE` で失敗する。鍵は呼び出し側が破棄する。 |
| `Delete` | なし | `void` | 鍵のファイルを消す。ファイルがなければ何もしない。 |

| プロパティ | 型 | 説明 |
| --- | --- | --- |
| `Directory` | `string` | 鍵を置くフォルダーのフルパス。 |
| `Scope` | `KeyScope` | スコープ。 |

### `InstallationKey`

`InstallationKeyStore` が読むか作るインストール鍵。公開のコンストラクターはない。使い終わったら破棄する。

| メンバー | 種類 | 引数 | 戻り値 | 説明 |
| --- | --- | --- | --- | --- |
| `Thumbprint` | プロパティ | なし | `string` | 鍵の JWK サムプリント。ライセンスとリースは、これに `cnf.jkt` として紐付く。 |
| `Identity` | メソッド | `IReadOnlyList<string> fingerprints` | `InstallationIdentity` | この鍵のサムプリントと、渡したフィンガープリントを持つ識別情報。フィンガープリントは通常 `MachineFingerprint.Compute` から得る。 |
| `CreateClockGuard` | メソッド | `string path` | `InstallationClockGuard` | ファイル `path` に状態を保存し、この鍵の自分用のコピーで署名する時刻巻き戻し対策。そのファイルのフォルダーは存在していなければならない。時刻巻き戻し対策を破棄すると、コピーを消す。`path` が空なら `ArgumentException` を、鍵が破棄済みなら `ObjectDisposedException` を投げる。 |
| `Dispose` | メソッド | なし | `void` | メモリ上の鍵を消す。鍵のファイルは残る。 |

### `ActivationClient`

ライセンスキーでこのインストールをアクティベーションするか、トライアルを始める。ライセンスファイルをインストール鍵の隣に保存し、アプリが動いているあいだトークンを更新し、アクティベーションを解除し、オフラインで確かめる。サービスに接続できないデバイスは、代わりにリクエストのファイルを作り、そのレスポンスをインポートする。保存したアクティベーションファイルが壊れているときは、オフラインのメソッド（`CreateActivationRequestAsync` から `ForgetDeactivationProofAsync` まで）と `Check` は `ELM_MALFORMED` を返す。流れは[アクティベーション](../explanation/activation.md)で説明している。メソッドは複数のスレッドから呼んでよい。1 つのクライアントの呼び出しは 1 つずつ実行し、1 台のデバイスのリクエストは、プロセスをまたいで 1 つずつ送る。サービスの TLS 証明書は OS が確かめる。公開のコンストラクターはない。クライアントは `TryCreate` で作り、`DisposeAsync` で破棄する。

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `TryCreate` | `ActivationClientOptions options`、`out ActivationClient? client`、`out LicenseError? error` | `bool` | インストール鍵があれば読み（なければ最初の `ActivateAsync`、`StartTrialAsync`、`CreateActivationRequestAsync` が作る）、マシンフィンガープリントを計算し、保存したアクティベーションを読んで、そのトークンの更新を予定する。サービスには接続しない。`ELM_INSTALLATION_KEY_UNREADABLE` で失敗する。製品 ID が正しくない、サービスの URL が https でもループバックアドレスへの http でもない、アプリのバージョンが 64 バイトを超える、のどれかなら `ArgumentException` を、`Trust` か `TimeProvider` が `null` なら `ArgumentNullException` を投げる。 |
| `ActivateAsync` | `string licenseKey`、`CancellationToken cancellationToken = default` | `Task<ActivationResult>` | ライセンスキーでこのインストールをアクティベーションする。保存したトライアルは、成功すれば置き換える。それ以外のアクティベーションか、アクティベーション解除のプルーフを保存しているときは、リクエストを送らずに `ELM_INVALID_STATE` を返す（先にアクティベーションを解除する）。キャンセルしたリクエストは `ELM_NETWORK_ERROR` で終わる。 |
| `StartTrialAsync` | `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | 製品のトライアルを始める。トライアルを含めて何かのアクティベーションを保存しているとき、またはアクティベーション解除のプルーフを保存しているときは、リクエストを送らずに `ELM_INVALID_STATE` を返す。 |
| `RefreshAsync` | `CancellationToken cancellationToken = default` | `Task<ActivationResult>` | 保存したアクティベーションのトークンを、まだ時期が来ていなくても今すぐ更新する。クライアントは、トークンの更新を始める期間に入ったときにも自分で更新する。`ServiceUrl` がない、アクティベーションを保存していない、このデバイスの別のプロセスが 10 秒経ってもアクティベーションのロックを持っている、または 16 個のリクエストにレスポンスがなかった（待っているオフラインのリクエストは 1 つと数える）ときは、リクエストを送らずに `ELM_INVALID_STATE` を返す。 |
| `DeactivateAsync` | `CancellationToken cancellationToken = default` | `Task<LicenseError?>` | このインストールのアクティベーションを解除する。サービスがその枠を解放し、保存したライセンスファイルを消す。インストール鍵は残る。サービスがもう持っていないアクティベーションは、解除したものとして扱う。失敗したときは何も消さない。 |
| `CreateActivationRequestAsync` | `string licenseKey`、`bool renew = false`、`CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | オフラインのアクティベーションのリクエスト（`.elmreq`）を作る。7 日後に期限が切れるか、そのレスポンスをインポートするまでは、同じリクエストを署名し直して返す。`renew` を指定すると、それより前でも新しいリクエストを作る。デバイスの時計が遅れていて、サービスが `ELM_REQUEST_EXPIRED` を返したときに使う。トライアル以外のアクティベーションか、アクティベーション解除のプルーフを保存しているときは `ELM_INVALID_STATE` を返す。 |
| `CreateRefreshRequestAsync` | `bool renew = false`、`CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | 保存したアクティベーションの、オフラインでのトークンの更新のリクエストを作る。期限が切れるか、そのレスポンスをインポートするまでは、同じリクエストを返す。アクティベーションがなければ `ELM_INVALID_STATE` を返す。 |
| `ImportResponseAsync` | `string response`、`CancellationToken cancellationToken = default` | `Task<ActivationResult>` | 待っているリクエストへのサービスのレスポンス（`.elmresp`、最大 256 KiB）をインポートし、オンラインのレスポンスと同じように確かめて保存する。別のリクエストへのレスポンスなら `ELM_REQUEST_MISMATCH`、レスポンスでないテキストなら `ELM_MALFORMED` で失敗する。保存したレスポンスをもう一度インポートしても、何も変わらない。 |
| `CreateDeactivationProofAsync` | `CancellationToken cancellationToken = default` | `Task<OfflineRequestResult>` | オフラインのアクティベーション解除のプルーフ（`.elmdeact`）を作り、保存したアクティベーションを消す。インストール鍵は残る。プルーフは保存し、`ForgetDeactivationProofAsync` を呼ぶまで同じものを返す。それまでは、アクティベーションもトライアルも拒否する。アクティベーションがなければ `ELM_INVALID_STATE` を返す。 |
| `ForgetDeactivationProofAsync` | `CancellationToken cancellationToken = default` | `Task<LicenseError?>` | ユーザーがサービスにプルーフを渡したあとで、保存したアクティベーション解除のプルーフを忘れ、インストールがもう一度アクティベーションできるようにする。プルーフがなければ何もしない。 |
| `Check` | `string? featureId`、`string? version`、`LicenseKind kind = LicenseKind.Standard` | `LicenseVerificationResult` | 保存したライセンスファイルを、このインストールの鍵と現在のマシンフィンガープリントに照らしてオフラインで確かめる。呼び出すたびにファイルを読み、サービスには接続しない。何も保存していなければ `ELM_LICENSE_NOT_FOUND`、保存したファイルが壊れていれば `ELM_MALFORMED` で失敗する。`State` は見ない。`Refused` と `CloneDetected` でも、保存したファイルが検証に通るあいだは `Valid` を返す。 |
| `DisposeAsync` | なし | `ValueTask` | トークンの更新のタイマーを止め、始まっているトークンの更新をキャンセルして、それを最大 5 秒待つ。アクティベーションは残る。`StateChanged` のハンドラーから呼んでもよい。 |

| プロパティまたはイベント | 型 | 説明 |
| --- | --- | --- |
| `State` | `ActivationState` | アクティベーションの状態。 |
| `ActivationId` | `string?` | 保存したアクティベーションの ID、または `null`。 |
| `StateChanged` | `EventHandler<ActivationStateChangedEventArgs>` | 状態が変わったときに発生する。アクティベーション、アクティベーション解除、トークンの更新の失敗と回復、拒否、クローンの検出、管理者が消したアクティベーションのときだ。クライアントのロックの外で発生し、ハンドラーの例外は無視する。`DisposeAsync` のあとには発生しない。 |

設定は `ActivationClientOptions` で指定する（どのプロパティにも `init` アクセサーがあるので、オブジェクトを作るときにだけ設定できる）。

| プロパティ | 型 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `ProductId` | `string` | なし | はい | 製品の `code`。1 から 64 文字の小文字、数字、`-`。インストール鍵のフォルダーの名前にもなる。 |
| `ServiceUrl` | `Uri?` | `null` | いいえ | `elm-service` の URL。https か、テスト用にループバックアドレスへの http。`null` は、サービスに一度もつながらず、オフラインのファイルだけを使うデバイス向けだ。このときオンラインの操作は `ELM_INVALID_STATE` を返し、自動ではトークンを更新しない。 |
| `Trust` | `TrustStore` | なし | はい | ベンダーのルート鍵。ライセンスファイルはこれに照らして確かめる。 |
| `KeyScope` | `KeyScope` | `KeyScope.User` | いいえ | インストール鍵とアクティベーションを保存する場所。 |
| `DataDirectory` | `string?` | `null` | いいえ | インストール鍵とアクティベーションを置くフォルダー。`null` ならスコープの既定のフォルダー（[鍵の保存場所](../explanation/devices-and-proofs.md#鍵の保存場所)）。 |
| `SendClientInfo` | `bool` | `false` | いいえ | ホスト名と OS のユーザー名を送るかどうか。サービスがこれを保存するのは、ポリシーの `clientInfoStrategy` が `hostAndUser` のときだけだ。 |
| `AppVersion` | `string?` | `null` | いいえ | 監査ログのために送るアプリのバージョン。UTF-8 で最大 64 バイト。 |
| `ClockGuard` | `IClockGuard?` | `null` | いいえ | 時刻巻き戻し対策。`null` なら、インストール鍵のフォルダーにあるファイル `clock` を使い、インストール鍵で署名する `FileClockGuard` になる。 |
| `VerifierOptions` | `LicenseVerifierOptions?` | `null` | いいえ | 検証側のオプション。`null` なら既定値。 |
| `TimeProvider` | `TimeProvider` | `TimeProvider.System` | いいえ | 時計。 |

### `LicenseClient`

オンプレミスのライセンスサーバーからシートのリースを受ける。サーバーのライセンスをベンダーのトラストストアとピンに照らして確かめ、リースを取得し、それぞれをその期間の 3 分の 1 ごとに更新し、サーバーが再起動したあとはリースを再登録し、最後に返却する。`LicenseKey` を設定すると、代わりに `elm-service` からリースを受ける。このとき TLS は OS の信頼で確かめ、各リースはそれと一緒に届く鍵証明書で確かめる。サービスが知らないリースは、再登録せずに失われる。リース、リースの更新、リースの再登録、借用は[フローティングライセンス](../explanation/floating-licenses.md)で説明している。メソッドは複数のスレッドから呼んでよい。サーバーへのリクエストは 1 つずつ送る。公開のコンストラクターはない。クライアントは `TryCreate` で作り、`DisposeAsync` で破棄する。

| メソッド | 引数 | 戻り値 | 動作 |
| --- | --- | --- | --- |
| `TryCreate` | `LicenseClientOptions options`、`out LicenseClient? client`、`out LicenseError? error` | `bool` | インストール鍵を読み、なければ作って、マシンフィンガープリントを計算する。サーバーには接続しない。`ELM_INSTALLATION_KEY_UNREADABLE` で失敗する。製品 ID が正しくない、サーバーの URL が https の絶対 URL でない（ライセンスキーがあれば、ループバックアドレスへの http も許す）、ライセンスキーが空か `ExpectedServerLicenseId` と一緒に指定した、アプリのバージョンが 64 バイトを超える、のどれかなら `ArgumentException` を投げる。 |
| `AcquireLeaseAsync` | `string featureId`、`string? version = null`、`int count = 1`、`CancellationToken cancellationToken = default` | `Task<LeaseResult>` | `count` 個のシートのリースを取得し、その更新を始める。オンプレミスのサーバーからの場合、最初の呼び出しでサーバーライセンスとサーバーの鍵を取得して確かめる。キャンセルしたリクエストは `ELM_NETWORK_ERROR` で終わる。`count` が 1 より小さければ `ArgumentOutOfRangeException` を投げる。 |
| `BorrowLeaseAsync` | `string featureId`、`TimeSpan period`、`string? version = null`、`int count = 1`、`CancellationToken cancellationToken = default` | `Task<LeaseResult>` | 1 秒以上の期間でリースを借用する。サーバーが許すより長い期間は、サーバーが拒否する。借用は更新せず、期限が切れるまで有効で、検証に必要なものと一緒にインストール鍵の隣に保存する。 |
| `GetBorrowedLeases` | なし | `IReadOnlyList<Lease>` | このクライアントのサーバーの URL とモードについて保存した借用（最大 1,000 ファイル）を読み、借用したときと同じようにそれぞれを確かめる。リクエストは送らない。期限が切れた借用は消す。検証に通らないファイルはそのまま残し、返さない。 |
| `ReleaseLeaseAsync` | `Lease lease`、`CancellationToken cancellationToken = default` | `Task<LicenseError?>` | リースを返却し、その更新を止める。借用でないリースは、サーバーに届かなかったときでも、そのあと `Released` になる。サーバーは期限が来たときにそのリースを捨てる。サーバーが拒否したか、サーバーに届かなかった借用は、`ELM_UNKNOWN_LEASE` の場合を除き、状態、ファイル、前の `LastError` を保ち、イベントを発生させない。すでに失われたリースや返却したリースは送り直さず、`null` を返す。別のクライアントのリースなら `ArgumentException` を投げる。 |
| `ResetServerPin` | `Uri serverUrl` | `bool` | 意図して別のマシンに移したサーバーのピンを消す。次のリクエストでは、最初に接続したときと同じようにサーバーを確かめる。ピンがなかったとき、ファイルを消せないとき、そしてライセンスキーのあるクライアントでは常に、`false` を返す。 |
| `DisposeAsync` | なし | `ValueTask` | リースの更新のタイマーを止め、借用でないリースを返却し（5 秒で諦め、失敗は無視する。返却されなかったリースは、サーバーが期限に捨てる）、鍵と接続を解放する。借用は返却しない。借用は有効なまま、ディスクに残る。 |

| プロパティまたはイベント | 型 | 説明 |
| --- | --- | --- |
| `InstallationKeyThumbprint` | `string` | このインストールの鍵のサムプリント。リースはこれに紐付く。 |
| `LeaseStateChanged` | `EventHandler<LeaseStateChangedEventArgs>` | リースの状態が変わったときに発生する。リースの更新の失敗と回復、リースの再登録、リースの喪失、返却のときだ。クライアントのロックの外で発生するので、ハンドラーから `ReleaseLeaseAsync` を呼んでよい。リースの更新による変化はタイマーのスレッドで発生し、そこでのハンドラーの例外は無視する。`ReleaseLeaseAsync` の呼び出しによる変化は、呼び出したスレッドで、呼び出しが戻る前に発生し、ハンドラーの例外はその呼び出しから出てくる。`DisposeAsync` は、返却するリースについてイベントを発生させない。 |

設定は `LicenseClientOptions` で指定する（どのプロパティにも `init` アクセサーがあるので、オブジェクトを作るときにだけ設定できる。`TryCreate` はそのコピーを取る）。

| プロパティ | 型 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `ProductId` | `string` | なし | はい | アプリの製品 ID。1 から 64 文字の小文字、数字、`-`。 |
| `ServerUrl` | `Uri` | なし | はい | オンプレミスのライセンスサーバーの URL。https でなければならない（たとえば `https://licenses.example:7070/`）。`LicenseKey` があるときは、`elm-service` の URL（https か、テスト用にループバックアドレスへの http）。 |
| `LicenseKey` | `string?` | `null` | いいえ | `elm-service` からリースを受けるためのライセンスキー。オンプレミスのサーバーなら `null`。キーがあるとき、クライアントはサーバーのピンを保存しない。キーはメモリにだけ持ち、リースを取得するときにだけ送る。 |
| `Trust` | `TrustStore` | なし | はい | ベンダーのトラストストア。サーバーライセンスを、`LicenseKey` があるときはサービスのリースの鍵証明書を、これで検証する。 |
| `ExpectedServerLicenseId` | `string?` | `null` | いいえ | サーバーが持っていなければならないサーバーライセンスの ID。`null` なら最初に見たものを受け入れ、以降はそれを求める。ID を指定すると、別のマシンに移したサーバーも、同じ ID なら受け入れる。`LicenseKey` と一緒には指定できない。 |
| `KeyScope` | `KeyScope` | `KeyScope.User` | いいえ | インストール鍵とサーバーのピンを保存する場所。 |
| `DataDirectory` | `string?` | `null` | いいえ | インストール鍵とピンを置くフォルダー。`null` ならスコープの既定のフォルダー（[鍵の保存場所](../explanation/devices-and-proofs.md#鍵の保存場所)）。 |
| `SendClientInfo` | `bool` | `false` | いいえ | 新しいリースごとに、ホスト名と OS のユーザー名を送るかどうか。オンプレミスでは、サーバーの[アクセスルール](server-configuration.md#アクセスルール)の `host:` と `user:` のエントリーがこれを使う。クライアントが自分で名乗る値なので、サーバーは確かめられない。 |
| `AppVersion` | `string?` | `null` | いいえ | 新しいリースごとに送るアプリのバージョン。UTF-8 で最大 64 バイト。 |
| `ClockGuard` | `IClockGuard?` | `null` | いいえ | サーバーライセンスとリースのための時刻巻き戻し対策。`null` なら、インストール鍵のフォルダーにあるファイル `clock` を使い、インストール鍵で署名する `FileClockGuard` になる。 |
| `VerifierOptions` | `LicenseVerifierOptions?` | `null` | いいえ | 検証側のオプション（時刻のずれ）。`null` なら既定値。 |
| `TimeProvider` | `TimeProvider` | `TimeProvider.System` | いいえ | 時計。 |
