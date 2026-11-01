[English](README.md) | 日本語

# ElasticLM

.NET と C / C++ のアプリ向けのライセンス認証基盤。ベンダーは署名つきのライセンスを発行する。アプリはそれをオフラインで確かめるか、SaaS 型のサービスで有効化するか、オンプレミスのライセンスサーバーからフローティング（同時利用数）の席を借りる。すべての部品が NativeAOT に対応して単一バイナリで配られ、暗号はすべて .NET 自身のライブラリを使う。

> **状態:** 1.0 はまだ公開していない。下のコマンドと API は `main` ブランチのものである。

## できること

- **オフラインの検証。** ライセンスファイルは、ECDSA P-256 で署名したライセンストークンと、署名した鍵の証明書を持つ。アプリは、組み込んだルート鍵で両方を確かめる。ネットワークは使わない。
- **有効化。** SaaS 型のサービス `elm-service` が、ライセンスを端末のインストール鍵に結びつけ、ライセンスの台数の上限まで数え、期限の短いトークンを更新する。ファイルのやり取りによるオフラインの有効化と、試用も扱う。
- **フローティングのライセンス。** オンプレミスのサーバー `elm-server`（または `elm-service`）が、席を短い期間だけ貸し、自動で更新させる。ネットワークから離れて使うために、席を借り出すこともできる。
- **運用。** `elm-service` は管理 API と管理画面を持ち、管理者を OIDC でログインさせ、監査ログを残し、SQLite と PostgreSQL のどちらでも動く。利用ログには署名が付く。

## 配る物

| 配布物 | 形 | 使う人 |
| --- | --- | --- |
| `ElasticLM.Licensing` | NuGet パッケージ。オフラインの検証 | アプリ |
| `ElasticLM.Client` | NuGet パッケージ。有効化、リース、借り出し、試用 | アプリ |
| `libelasticlm` | NativeAOT の共有ライブラリと C ヘッダー | C と C++ のアプリ |
| `elm-server` | オンプレミスのフローティングのライセンスサーバー。単一バイナリ | 利用者の管理者 |
| `elm-service` | 管理画面を持つ SaaS 型の有効化サービス。単一バイナリとコンテナイメージ | ベンダー |
| `elm` | 鍵、ライセンス、管理 API、オンプレミスのサーバーを扱う CLI | ベンダーと利用者の管理者 |

`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`、`osx-x64` 向けに作る。

## 試す

`elm` にパスを通し、ルート鍵と、それが証明する署名鍵を作り、ライセンスを発行して確かめる。`elm` は、それぞれの秘密鍵を `ELM_KEY_PASSPHRASE` のパスフレーズで暗号化するので、先にこの変数を設定しておく。

```shell
elm key root create --out root.key.pem --public-out root.pub.pem
elm key signing create --out signing.key.pem --public-out signing.pub.pem
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
elm license verify first.elmlic --trust root.pub.pem --product acme-cad --feature pro
```

ルート鍵は署名鍵を証明するためだけに使う。`root.key.pem` はオフラインに置く。アプリは `root.pub.pem` を組み込み、最後のコマンドと同じように `first.elmlic` を確かめる。

## 文書

利用者の文書は [`docs/guide/`](docs/guide/README.md) にある（英語）。ElasticLM の仕組み、部品が互いをどう信頼するか、何から守るかを説明する。1.0 からはサイトとしても公開する。

設計は [`docs/design.ja.md`](docs/design.ja.md)、計画は [`docs/roadmap.md`](docs/roadmap.md) にある。脆弱性の報告は [`SECURITY.md`](SECURITY.md) の手順で受け付ける。

## ライセンス

[MIT](LICENSE)
