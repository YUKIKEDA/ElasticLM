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

- **フェーズ:** M3a（インストール鍵・証明・リーストークン）
- **済み:** F0（[#1](https://github.com/YUKIKEDA/ElasticLM/issues/1)）、D0（[#3](https://github.com/YUKIKEDA/ElasticLM/issues/3)）、M1（[#7](https://github.com/YUKIKEDA/ElasticLM/issues/7)）、M2（[#11](https://github.com/YUKIKEDA/ElasticLM/issues/11)）

## v1 まで

| ID  | 内容                                                                                       | 状態        | Issue |
| --- | ------------------------------------------------------------------------------------------ | ----------- | ----- |
| F0  | 開発基盤（規約・スキル・ビルド設定・CI・テンプレート・コミュニティ文書）                   | done        | [#1](https://github.com/YUKIKEDA/ElasticLM/issues/1) |
| D0  | 要件・脅威モデル・全体構成（オフライン検証・オンプレミスのフローティング・SaaS 型の有効化） | done        | [#3](https://github.com/YUKIKEDA/ElasticLM/issues/3) |
| M1  | `ElasticLM.Licensing`（JWS、トークン、トラストストア、検証、時計の守り、マシン指紋）       | done        | [#7](https://github.com/YUKIKEDA/ElasticLM/issues/7) |
| M2  | `ElasticLM.Issuing` と `elm` の鍵・発行・検査                                              | done        | [#11](https://github.com/YUKIKEDA/ElasticLM/issues/11) |
| M3a | プロトコルの土台（インストール鍵、証明の作成と検証、ノンス、リーストークン）          | in-progress | [#15](https://github.com/YUKIKEDA/ElasticLM/issues/15) |
| M3b | `elm-server` の本体と `ElasticLM.Client` のリース（席・回収・TLS・自動更新）            | not-started | —     |
| M3c | 借り出しと記録、署名つきの利用ログと `elm log verify`、ローカル管理と `elm server`        | not-started | —     |
| M4  | `elm-service`（保存先・SQLite の静的ビルド・管理 API・有効化・試用・オフライン・クラウドのリース・監査ログ）と `ElasticLM.Client` の有効化・更新・解除・オフラインのファイル・試用、`elm-server` のサーバーライセンスの更新 | not-started | —     |
| W0  | 管理画面の技術の選定                                                                       | not-started | —     |
| M5  | 管理の OIDC（JwtBearer と BFF）と管理画面                                                  | not-started | —     |
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
