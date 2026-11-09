# 開発規約

コードレビューで同じ指摘を繰り返さないための規約をまとめる。エージェントが守る強制力のある規約は `.cursor/rules/` にあり、本書はその人間向けの要約と、ディレクトリ構成の正本である。設計の中身は [design.ja.md](./design.ja.md) にある。

## ディレクトリ構成

プロジェクトやフォルダは、それを必要とする Issue の PR で作る。先回りして空のものを作らない。プロジェクトを追加した PR で、下の表に1行足す。

```text
ElasticLM.slnx               ソリューション（.sln は使わない）
global.json                  .NET SDK の固定（10.0 系）とテストランナー（Microsoft.Testing.Platform）
Directory.Build.props        全プロジェクト共通の設定（警告はエラー、解析、AOT 互換、パッケージ情報）
Directory.Packages.props     パッケージのバージョン（Central Package Management）
BannedSymbols.txt            src/ で禁止する API（実時間・非暗号乱数・リフレクション）
stylecop.json                StyleCop の設定
.editorconfig                命名・スタイル・解析ルールの重大度
.csharpierrc.json            フォーマッタ（CSharpier）の設定
.config/dotnet-tools.json    ローカルツール（CSharpier）
build.ps1                    ローカルのゲート（CI も同じスクリプトを実行する）
src/                         出荷するプロジェクト（ElasticLM.*）
include/                     libelasticlm の C ヘッダー（elasticlm.h。手で書く。design §17.4）
tests/                       テスト（src/ の各プロジェクトに対応する *.Tests）、端から端までのテスト（tests/ElasticLM.E2E/）、libelasticlm の C のテスト（tests/native/）
samples/                     利用例と NativeAOT の確認用のアプリ
tools/                       ロードマップの行が要るときに足す道具（tools/ElasticLM.Measure/: §4.2 の参照機での計測）
eng/release/                 リリース（release.yml）と CI の NativeAOT のジョブが共有するスクリプト（design §20.5）
eng/container/               elm-service のコンテナイメージの Dockerfile と、イメージを作るスクリプトと試すスクリプト（design §20.6）。OCI layout を docker load で読める形にする関数（ImageLayout.ps1）は、試すスクリプトと Test-GuideSteps.ps1 が使う（design §20.7 の V1c4d2）
eng/docs/                    利用者の文書の確かめのスクリプト（README、Tutorials、How-to guides の手順を流す Test-GuideSteps.ps1。How-to の頁は、印で名指した Tutorial の手順のあとに同じ作業フォルダーで流す。design §20.7）
eng/Get-MsvcEnvironment.ps1  Windows の MSVC の環境（vcvarsall.bat が設定する変数）を返すスクリプト。tests/native/run.ps1 と Test-GuideSteps.ps1 が使う（design §20.7 の V1c3c）
vendor/sqlite/               SQLite のアマルガム（版と SHA-256 を固定）とビルドのスクリプト（Windows は build.ps1、Linux と macOS は build.sh、オプションは defines.txt）。elm-service が組み込む（design §14.2）
web/admin/                   管理画面の SPA（Preact、TypeScript、Vite。design §15.2）。ビルド結果の dist を elm-service が埋め込む
docs/                        設計・却下した案・ADR・ロードマップ・規約（本書）
docs/guide/                  利用者の文書（英語の正本、Diátaxis の 4 つ。design §20.7）
docs/guide.ja/               利用者の文書の日本語の訳（docs/guide/ と同じフォルダーとファイルの名前、同じ PR で直す。0.117）
website/                     利用者の文書の Docusaurus のサイト（docs/guide/ を読む。design §20.7）
.cursor/rules/               エージェント向けの規約
.agents/skills/              スキル（.claude/skills/ にコピーを置く）
.github/                     Issue / PR テンプレート、CI、Dependabot
```

| プロジェクト | 責務 |
| --- | --- |
| `src/ElasticLM.Licensing` | オフラインの検証（design §6.2）。JWS の用途を絞った形、トークンの型、トラストストア、`LicenseVerifier`、時計の守り、マシン指紋。外部の依存なし。NuGet パッケージ |
| `src/ElasticLM.Client` | クライアント（design §6.2）。インストール鍵の作成と保管（Windows は DPAPI、Linux と macOS は `0600` のファイル）、証明（`ELM-Proof`）の作成、`LicenseClient` によるリースの取得・自動更新・回収・返却とサーバーのピン（design §12.7, §13.5）。NuGet パッケージ。`ElasticLM.Licensing` の内部の型は見ず、公開 API（`KeyIds`、`HttpTargetUri` など）だけを使う |
| `src/ElasticLM.Issuing` | 署名側（design §6.2）。鍵ファイル（暗号化した PKCS#8）、鍵の証明書とライセンスの発行。アプリには入らない。`ElasticLM.Licensing` の内部の型と規則（`LicensePayloadRules`）を `InternalsVisibleTo` で共有し、検証側が拒否するものを発行しない。署名つきの追記だけのログ（`SignedLog`）とその検証（`LogVerifier`）も持ち、`elm-server` と `elm log verify` が共有する |
| `src/ElasticLM.Leasing` | リースの規則（design §12.7, §13.2〜§13.4）。席の数え上げと超過、機能ごとの排他、`seq` による複製の検出、回収と回収の時間。状態はメモリに持つ。アプリには入らない |
| `src/ElasticLM.Server` | オンプレミスのライセンスサーバー `elm-server`（design §13）。ASP.NET Core の Slim ビルダー、NativeAOT の単一バイナリ。`ElmServer.StartAsync` に時計と環境変数を渡せるので、テストは同じプロセスのループバックで動かす。利用ログと借り出しの記録（署名つきのログ）、ループバックの管理 API（`ElmServer.Admin.cs`） |
| `src/ElasticLM.Service` | SaaS 型の有効化サービス `elm-service`（design §14）。ASP.NET Core の Slim ビルダー、NativeAOT の単一バイナリで、`vendor/sqlite/` の SQLite を組み込む。表を AuraORM のレコード（`ServiceTables.cs`）で宣言し、SQL を AuraORM で組み立てる 1 つの実装（`ServiceStore<TDialect>`、窓口は `Database`）で SQLite と PostgreSQL を扱い、埋め込んだ SQL のファイルで起動時に移行する（`Migrator`）。M4a1 は API キー、`elm-service setup`、監査ログ（`AuditLog`）、管理 API（`ElmService.Admin.cs`）。M4a2 は製品・機能・ポリシー（`Policies`、`ElmService.Model.cs`）と、送ったメンバー・`null`・省略を区別する本文の読み手（`RequestBody`）。M4a3 はライセンス（`Licenses`、`LicenseKeys`、`ElmService.Licenses.cs`）と `Unbound` のライセンスファイル |
| `src/ElasticLM.Cli` | CLI `elm`（design §16）。NativeAOT の単一バイナリ。`ElmApp.Run` に入出力・時刻・環境変数を渡せるので、テストは同じプロセスで動かす。`elm log verify` と `elm server`（管理 API を呼ぶ）は M3c で足した |
| `src/ElasticLM.Native` | `libelasticlm`（design §17、0.105）。C ABI の関数を `[UnmanagedCallersOnly]` で出す NativeAOT の共有ライブラリ（`NativeLib=Shared`）。アセンブリ名は Windows で `elasticlm`、ほかで `libelasticlm`（ファイル名になる）。Windows の発行のあとで、インポートライブラリ `elasticlm.lib` を発行のフォルダーへ写す（0.118）。ハンドルは `GCHandle`、文字列はネイティブのメモリ、誤りの文はスレッドごと。宣言は `include/elasticlm.h` |
| `tests/ElasticLM.Native.Tests` | `libelasticlm` の関数を、C と同じく関数ポインター（`delegate* unmanaged`）で呼ぶテスト（JIT）。ライセンスは `ElasticLM.Licensing.Tests` の `TestIssuer` で作り、時計は `Exports.Time` を偽の時計に替える |
| `tests/native` | `libelasticlm` の C のテスト（`test_elasticlm.c`）と `run.ps1`。`run.ps1` は、公開する名前とヘッダーを比べ、警告を誤りにして C をコンパイルし、`elm` で作った鍵とライセンスで、発行したライブラリを実行時に読み込んで動かす。有効化の部分は、発行した `elm-service` をループバックで立てて動かす。オフラインの有効化と失効のあとの更新は、C のテストを段に分け、段の間で `run.ps1` が `elm offline process` と管理 API の失効を呼ぶ。リースの部分は、それに加えて発行した `elm-server` を https のループバックで立てて動かす。CI の NativeAOT のジョブが動かし、手元では `./tests/native/run.ps1 -Publish` |
| `tests/ElasticLM.E2E` | 端から端までのテスト（design §20.7）。NativeAOT で公開した `elm`・`elm-server`・`elm-service` を別のプロセスとして起動し、`ElasticLM.Client` から使う。流れごとのクラスで、`src` の写しではない。`./build.ps1` はビルドだけで流さず、CI の NativeAOT のジョブが流す。手元では `./tests/ElasticLM.E2E/run.ps1 -Publish` |
| `tests/ElasticLM.Licensing.Tests` | `ElasticLM.Licensing` のテスト。鍵とトークンはテストの中で作る（`TestIssuer`） |
| `tests/ElasticLM.Client.Tests` | `ElasticLM.Client` のテスト。作った証明をサーバー側の `ProofVerifier`（`ElasticLM.Issuing`）で確かめる |
| `tests/ElasticLM.Issuing.Tests` | `ElasticLM.Issuing` のテスト。発行したライセンスとリースを `ElasticLM.Licensing` で検証する |
| `tests/ElasticLM.Leasing.Tests` | `ElasticLM.Leasing` のテスト。偽の時計で、席・`seq`・回収の時間・期限を確かめる |
| `tests/ElasticLM.Server.Tests` | `elm-server` と `LicenseClient` の結合テスト。サーバーを同じプロセスのループバックの TLS で立て、本物のクライアントから呼ぶ。`elm server` と `elm log verify` も同じプロセスで `ElmApp.Run` から呼ぶ |
| `tests/ElasticLM.Service.Tests` | `elm-service` のテスト。サービスを同じプロセスのループバックで立て、SQLite で流す。環境変数 `ELM_TEST_POSTGRES` に PostgreSQL の接続文字列があれば、同じテストをテストごとのスキーマで PostgreSQL でも流す（CI の Linux） |
| `tests/ElasticLM.Cli.Tests` | `elm` のコマンドのテスト。一時フォルダのファイルで、鍵の作成から検証までを流す |
| `web/admin` | 管理画面の SPA（design §15.2、0.96、0.100）。Preact 11 と TypeScript、Vite 8、テストは Vitest と happy-dom。Node.js は `.node-version`、pnpm は `package.json` の `packageManager`（Corepack が確かめる）、依存は `pnpm-lock.yaml`、pnpm の守りは `pnpm-workspace.yaml`。`build.ps1` がビルドし、`dist` を `ElasticLM.Service` が埋め込んで `/` と `/assets/` で配る。文言は `src/messages.ts` |
| `samples/VerifyLicense` | ライセンスファイルを検証する最小のアプリ。`--self-test` で NativeAOT の確認と検証時間の計測を行う |
| `samples/VerifyOffline` | 利用者の文書の Tutorial 1 のアプリ（design §20.7 の V1c3 の形）。ルートの公開鍵とライセンスファイルを引数で受け取り、`acme-cad` と機能 `pro` をオフラインで検証する。`Program.cs` の全体が Tutorial の頁の `Program.cs` で、検証の部分（`// <snippet:verify>`）を .NET の API の頁が引く。`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `samples/ActivateOnline` | 利用者の文書の Tutorial 2 のアプリ（design §20.7 の V1c3 の形の「V1c3d の Grill」）。サービスの URL、ルートの公開鍵、データのフォルダー、省いてよいライセンスキーを引数で受け取り、`ActivationClient` で `elm-service` に `acme-cad` の試用を求めるか、キーで有効化し、保存したライセンスファイルで機能 `pro` を確かめる。`Program.cs` の全体が Tutorial の頁の `Program.cs` で、`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `samples/ActivateOffline` | 利用者の文書の How-to の「Activate a device without a network」「Deactivate a device」「Rotate the signing key」「Revoke a license」のアプリ（design §20.7 の「V1c4 の形」の Q7。後の 2 頁は V1c4b の Grill の R1）。どのコマンドもルートの公開鍵とデータのフォルダーを最初の 2 つの引数で受け取り、3 つ目がコマンド: `request <key> <file>`（`CreateActivationRequestAsync`）、`refresh <file>`（`CreateRefreshRequestAsync`）、`import <file>`（`ImportResponseAsync` のあと機能 `pro` を確かめる）、`deactivate <service-url>`（`DeactivateAsync`。サービスの URL を渡すのはこのコマンドだけ）、`deactivate-proof <file>`（`CreateDeactivationProofAsync`）、`forget`（`ForgetDeactivationProofAsync`）。`Program.cs` の全体が 4 つの頁の `Program.cs` で、頁の手順は各コマンドが呼ぶメソッドを名前で書く。`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `samples/FloatingLease` | 利用者の文書の Tutorial 3 のアプリ（design §20.7 の V1c3 の形の「V1c3b の Grill」）。ルートの公開鍵とデータのフォルダーを引数で受け取り、`LicenseClient` で `https://127.0.0.1:7070/` の `elm-server` から `acme-cad` の機能 `pro` をリースして返し、1 時間借り出す。起動直後の `ELM_SERVER_RECOVERING` は `RetryAfter` だけ待って取り直す（合わせて 3 度まで）。`Program.cs` の全体が Tutorial の頁の `Program.cs` で、`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `samples/VerifyOfflineC` | 利用者の文書の Tutorial 4 の C のプログラム `app.c`（design §20.7 の V1c3 の形の「V1c3c の Grill」）。ルートの公開鍵とライセンスファイルを引数で受け取り、`libelasticlm` の `elm_verifier_create` と `elm_verifier_verify` で `acme-cad` と機能 `pro` をオフラインで検証する。.NET のプロジェクトではなく、ソリューションと `build.ps1` には入れない。CI の `aot` ジョブの段「Guide steps」が頁のとおりに 3 つの OS でビルドして動かし、`tests/native/run.ps1` が警告を誤りとしてコンパイルする。ファイルの全体が Tutorial の頁の `app.c` で、`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べ、ASCII だけであることを確かめる |
| `samples/MakeTestCertificate` | 利用者の文書の How-to の「Use your own TLS certificate」のアプリ（design §20.7 の「V1c4 の形」の「V1c4c の Grill」の Q3 と R7）。.NET だけを使い、引数で受け取ったフォルダーに、試用の CA（ECDSA P-256、基本制約と Key Usage は critical、パスの長さ 0）の証明書 `test-ca.crt` と、その CA が署名したサーバー証明書 `server.crt`（SAN は IP アドレスの `127.0.0.1` と DNS 名の `localhost`、EKU は serverAuth）とその秘密鍵 `server.key` を PEM で書く。期限は 30 日。どのファイルも上書きせず（`FileMode.CreateNew`）、秘密鍵は Linux と macOS で 0600 で作る。CA の秘密鍵は書かない。試すためだけのもの。`Program.cs` の全体が頁の `Program.cs` で、`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `samples/ResilientActivation` | 利用者の文書の How-to の「Handle errors」「Run without a network」のアプリ（design §20.7 の「V1c4 の形」の「V1c4e の Grill」の Q2 と R4〜R9）。どのコマンドもルートの公開鍵とデータのフォルダーを最初の 2 つの引数で受け取り、3 つ目がコマンド: `activate <service-url> <key>`（`ActivateAsync`）、`check`（`Check`。サービスの URL を渡さない）、`refresh <service-url>`（`RefreshAsync` と、失敗のあとの `State`）。誤りは、操作とコードの組で決めたアプリの判断を OS によらない決まった 1 行（`retry later: <コード>` など）で出し、そのあとに理由と期限を出す。`Program.cs` の全体が 2 つの頁の `Program.cs` で、`ElasticLM.Licensing.Tests` の `GuideSamplesTests` が頁と比べる |
| `tools/ElasticLM.Measure` | §4.2 の目標の参照機での計測（design §20.7 の V1b の形）。`run.ps1` が公開したバイナリを CPU を分けて起動し、この道具が `ElasticLM.Client` の公開の部品でリースと有効化の負荷をかける。`Report.ps1` が 3 回の幅を出して目標と比べる。`measure.yml` が動かし、`./build.ps1` はビルドだけ。手元では `run.ps1 -Small` で手順を試せる |
| `samples/PackageConsumer` | 2 つの NuGet パッケージだけを版で参照するアプリ（design §20.5）。ルートの設定を読み込まず、`nuget.config` は `ElasticLM.*` を `artifacts/packages` だけから取る。ソリューションと `build.ps1` には入れず、`release.yml` がパッケージを作ったあとに NativeAOT で発行して動かす |
| `eng/release` | リリースと CI のスクリプト（design §20.5）。`Get-ReleaseVersion.ps1`（版、タグ、`CHANGELOG.md`）と `Test-ReleaseVersion.ps1`、`Publish-Rid.ps1`（RID ごとの発行。`almalinux:8` の中では POSIX の `publish-rid.sh`）、`Test-Publish.ps1`（単一バイナリ、依存、glibc、署名、大きさ）、`Test-Smoke.ps1`、`Pack-Rid.ps1`（圧縮ファイル）、`Test-Package.ps1`（パッケージの中身）、`New-Sbom.ps1`（その実行のコミットの SPDX の SBOM）。持ち主の手順は `docs/releasing.md` |

## ビルドとゲート

- 対象は `net10.0`、C# 14。Nullable と `InvariantGlobalization` は有効。
- 警告はすべてエラー。`NoWarn` や `#pragma warning disable` でビルドを通さない。ルールがこのリポジトリに合わないときは、理由をコメントに書いて `.editorconfig` を変える。
- `src/` のプロジェクトは `IsAotCompatible`。公開 API には XML ドキュメントが要る。
- `./build.ps1` がゲート。管理画面（`web/admin/`）の型の確かめ・Vitest・ビルド、CSharpier の検査、Release と Debug のビルド、全テストを実行する。CI は Windows・Linux・macOS で同じスクリプトを `-SkipWeb` で実行し、管理画面は秘密を持たない別のジョブで 1 回だけビルドして `dist` を渡す（design §15.2）。

## コード

- 書式は CSharpier に任せる。命名は private フィールドが `_camelCase`、定数が PascalCase、インターフェースが `I` 始まり。
- 名前空間はアセンブリ名に合わせ、フォルダには合わせない。名前空間と同じ名前の型は作らない（CA1724 はエラー）。
- 検証前のデータと検証済みのデータは別の型にする。ID は種類ごとに別の型にする。
- 呼び出し側が分岐する結果（期限切れ、空き席なし、署名不一致）は戻り値で返し、例外にしない。
- 時刻は `TimeProvider` から読む。乱数は `RandomNumberGenerator` を使う（`BannedSymbols.txt`）。

## テスト

- xUnit v3 と組み込みの `Assert`。アサーションライブラリとモックライブラリは使わない。
- テスト名は `{対象}_{状況}_{期待}`。各テストに `summary` と `remarks`（Preconditions / Steps / Expected）を書く。
- 時刻は偽の `TimeProvider`、鍵はテスト実行時に生成する。リポジトリに鍵やトークンを置かない。
- セキュリティ上の検査のテストは、PR の前にミューテーション確認を行い、PR に列挙する。

## 言語

- コード、コメント、ルール、コミットの件名、PR のタイトルと見出し、README.md、利用者の文書（`docs/guide/`、design §20.7）、`docs/releasing.md`、`docs/container.md` は英語。
- 設計書（上の英語の文書を除く `docs/` 配下）、利用者の文書の日本語の訳（`docs/guide.ja/`）、Issue と PR の本文、README.ja.md は日本語でよい。日本語は常体で書く。
