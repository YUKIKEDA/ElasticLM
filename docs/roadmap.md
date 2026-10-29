# ロードマップ

進捗の正本。設計（何を作るか）は [design.ja.md](./design.ja.md)、却下した案は [design-decisions.md](./design-decisions.md) にある。

GitHub Issue は着手する作業単位だけ切る。完了条件の詳細は Issue 本文に書き、この表は ID・内容・状態・Issue だけを持つ。各行が受け持つ設計の節は design.ja.md §22 にある。

状態: `not-started` / `in-progress` / `blocked` / `done`

## 運用

1. 新しい作業は、先にこの表へ行を足してから Issue を切る。行の追加・削除は Grill を経る（`.cursor/rules/workflow.mdc`）。
2. 次の Issue は、前の行が `done` または `blocked` になってから切る。
3. 設計の前提が折れたら、その行を `blocked` にし、設計を直してから次へ進む。
4. 結果は GitHub Issue の本文に残す。この表は状態と Issue リンクだけ。

## 今

- **フェーズ:** M5a（管理者の土台と CLI のログイン）
- **済み:** F0（[#1](https://github.com/YUKIKEDA/ElasticLM/issues/1)）、D0（[#3](https://github.com/YUKIKEDA/ElasticLM/issues/3)）、M1（[#7](https://github.com/YUKIKEDA/ElasticLM/issues/7)）、M2（[#11](https://github.com/YUKIKEDA/ElasticLM/issues/11)）、M3a（[#15](https://github.com/YUKIKEDA/ElasticLM/issues/15)）、M3b（[#21](https://github.com/YUKIKEDA/ElasticLM/issues/21)）、M3c（[#25](https://github.com/YUKIKEDA/ElasticLM/issues/25)）、M3d（[#28](https://github.com/YUKIKEDA/ElasticLM/issues/28)）、M4a1（[#46](https://github.com/YUKIKEDA/ElasticLM/issues/46)）、M4a2（[#54](https://github.com/YUKIKEDA/ElasticLM/issues/54)）、M4a3（[#58](https://github.com/YUKIKEDA/ElasticLM/issues/58)）、M4b1（[#60](https://github.com/YUKIKEDA/ElasticLM/issues/60)）、M4b2（[#66](https://github.com/YUKIKEDA/ElasticLM/issues/66)）、M4c1（[#70](https://github.com/YUKIKEDA/ElasticLM/issues/70)）、M4c2（[#74](https://github.com/YUKIKEDA/ElasticLM/issues/74)）、M4c3（[#78](https://github.com/YUKIKEDA/ElasticLM/issues/78)）、M4d1（[#82](https://github.com/YUKIKEDA/ElasticLM/issues/82)）、M4d2（[#86](https://github.com/YUKIKEDA/ElasticLM/issues/86)）、M4d3（[#90](https://github.com/YUKIKEDA/ElasticLM/issues/90)）、W0（[#94](https://github.com/YUKIKEDA/ElasticLM/issues/94)）

## v1 まで

| ID  | 内容                                                                                       | 状態        | Issue |
| --- | ------------------------------------------------------------------------------------------ | ----------- | ----- |
| F0  | 開発基盤（規約・スキル・ビルド設定・CI・テンプレート・コミュニティ文書）                   | done        | [#1](https://github.com/YUKIKEDA/ElasticLM/issues/1) |
| D0  | 要件・脅威モデル・全体構成（オフライン検証・オンプレミスのフローティング・SaaS 型の有効化） | done        | [#3](https://github.com/YUKIKEDA/ElasticLM/issues/3) |
| M1  | `ElasticLM.Licensing`（JWS、トークン、トラストストア、検証、時計の守り、マシン指紋）       | done        | [#7](https://github.com/YUKIKEDA/ElasticLM/issues/7) |
| M2  | `ElasticLM.Issuing` と `elm` の鍵・発行・検査                                              | done        | [#11](https://github.com/YUKIKEDA/ElasticLM/issues/11) |
| M3a | プロトコルの土台（インストール鍵、証明の作成と検証、ノンス、リーストークン）          | done        | [#15](https://github.com/YUKIKEDA/ElasticLM/issues/15) |
| M3b | `elm-server` の本体と `ElasticLM.Client` のリース（席・回収・TLS・自動更新）            | done        | [#21](https://github.com/YUKIKEDA/ElasticLM/issues/21) |
| M3c | 借り出しと記録、署名つきの利用ログと `elm log verify`、ローカル管理と `elm server`        | done        | [#25](https://github.com/YUKIKEDA/ElasticLM/issues/25) |
| M3d | Windows で再起動の後のリースの回収が遅い問題の調査と修正                                   | done        | [#28](https://github.com/YUKIKEDA/ElasticLM/issues/28) |
| M4a1 | `elm-service` の土台（SQLite の静的ビルドと PostgreSQL、スキーマの移行、設定、署名鍵、`elm-service setup` と API キー、監査ログ、3 OS の NativeAOT） | done        | [#46](https://github.com/YUKIKEDA/ElasticLM/issues/46) |
| M4a2 | `elm-service` のモデル（製品・機能・ポリシー）とその管理 API、署名鍵の状態 | done        | [#54](https://github.com/YUKIKEDA/ElasticLM/issues/54) |
| M4a3 | ライセンスとライセンスキー、その管理 API と状態の操作、`Unbound` のライセンスファイル | done        | [#58](https://github.com/YUKIKEDA/ElasticLM/issues/58) |
| M4b1 | `elm-service` のオンラインの有効化・更新・解除・試用（複製の検出とサーバーライセンスを含む）、その管理 API | done        | [#60](https://github.com/YUKIKEDA/ElasticLM/issues/60) |
| M4b2 | `ElasticLM.Client` のオンラインの有効化・更新・解除・試用 | done        | [#66](https://github.com/YUKIKEDA/ElasticLM/issues/66) |
| M4c1 | `elm-service` のオフラインの有効化・更新・解除と `elm offline process` | done        | [#70](https://github.com/YUKIKEDA/ElasticLM/issues/70) |
| M4c2 | `ElasticLM.Client` のオフラインのファイル（要求の作成、応答の取り込み、解除の証明） | done        | [#74](https://github.com/YUKIKEDA/ElasticLM/issues/74) |
| M4c3 | `elm-server` のサーバーライセンスの有効化と更新（オンラインと `elm server refresh-request`） | done        | [#78](https://github.com/YUKIKEDA/ElasticLM/issues/78) |
| M4d1 | `LeaseEngine` の規則の取り出しと、`elm-service` のクラウドのリースと借り出し、リースの管理 API | done        | [#82](https://github.com/YUKIKEDA/ElasticLM/issues/82) |
| M4d2 | `LicenseClient` の SaaS 型のモード（OS の信頼、ライセンスキー、`use: lease` の証明書、借り出しのファイル） | done        | [#86](https://github.com/YUKIKEDA/ElasticLM/issues/86) |
| M4d3 | `elm-service` の利用ログ（`use: log` の署名）と、その管理 API | done        | [#90](https://github.com/YUKIKEDA/ElasticLM/issues/90) |
| W0  | 管理画面の技術の選定                                                                       | done        | [#94](https://github.com/YUKIKEDA/ElasticLM/issues/94) |
| M5a | 管理者の土台と CLI のログイン（JwtBearer、`elm admin login`・`logout`・`request`、管理者の一覧と無効化の API、最後のログイン、90 日ログインのない管理者のキーを止める規則） | not-started | —     |
| M5b | 管理画面のログイン（BFF。認可コードと PKCE、データベースのセッション、Data Protection の鍵の保存、`X-ELM-CSRF`、偽の IdP と Keycloak のテスト） | not-started | —     |
| M5c | 管理画面の土台（`web/admin/`。`build.ps1` のビルドと `tsc`・Vitest のゲート、道具の版の確かめ、`dist` がないときの Service のビルドの誤り、秘密を持たない CI の SPA のジョブ、埋め込みと CSP つきの配信、`elm-service` の大きさの差と Windows のパスの長さの確かめ、AGENTS.md・CONTRIBUTING.md・規則・規約への Node.js と pnpm の追記、最初の画面） | not-started | —     |
| M5d | 管理画面の残りの画面（発行、有効化の解除、オフラインの処理、監査ログ、管理者と API キー） | not-started | —     |
| M6  | `libelasticlm`（C ABI）                                                                    | not-started | —     |
| F1  | リリース基盤（release.yml、パッケージと単一バイナリの検査、NuGet の Trusted Publishing）   | not-started | —     |
| V1  | 端から端までのテスト、§4.2 の参照機（`ubuntu-latest`）での計測、文書、サンプル、安全性の見直し、v1 の公開 | not-started | —     |

## v1 の後

名前のない先送りを作らないために置く行。着手する前に Grill を経る。

| ID  | 内容                                                                     | 状態        | Issue |
| --- | ------------------------------------------------------------------------ | ----------- | ----- |
| A-1 | Webhook（受け取る側の要件が挙がったら）                                  | not-started | —     |
| A-2 | オンプレミスのサーバーの冗長化                                           | not-started | —     |
| A-3 | ML-DSA への移行（.NET の `MLDsa` が試験扱いでなくなり、3 OS で動いたら） | not-started | —     |
| A-4 | 鍵管理サービス（KMS）の署名鍵                                            | not-started | —     |

## Intentionally out of scope

- 複数ベンダーの相乗り（design.ja.md 0.12）
- クライアントのバイナリの改ざん対策・難読化（0.18）
- ドングル（ハードウェアキー）
- 失効リスト（CRL）の配布（§5.4）
