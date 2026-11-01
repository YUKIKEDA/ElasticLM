# ElasticLM ドキュメント

ElasticLM を使うと、.NET や C/C++ アプリのベンダーは、ライセンスを発行し、顧客のマシンでそのライセンスを検証し、同時使用数を数えられる。このドキュメントの読者は、ライセンスを発行する人、アプリに検証を組み込む開発者、ライセンスサーバーを運用する管理者だ。自分が使うプラットフォームの知識は前提にするが、設計書を読んでいることは前提にしない。

> **状況:** 1.0 はまだリリースしていない。各ページは `main` ブランチの内容を説明している。チュートリアル、ハウツーガイド、リファレンスのページはまだ執筆中で、下に挙げるのは ElasticLM の仕組みを説明するページだ。

## ドキュメントの構成

どのページも、次の 4 種類のどれかに当たる。いま必要なことに合った種類を選ぶとよい。

| 種類 | こんなときに読む |
| --- | --- |
| チュートリアル | 初めて使うので、手順に沿って動くところまで進めたい。 |
| ハウツーガイド | 署名鍵の入れ替えのように、やりたい作業が決まっている。 |
| リファレンス | コマンド、設定、フィールド、エラーコードの正確な名前を調べたい。 |
| 解説 | ある部分がどう動くのか、なぜそうなっているのか、何を防ぐのかを理解したい。 |

## どこから読むか

立場にかかわらず、最初に[概要](explanation/overview.md)を読む。各部分の名前と、誰がどれを運用するかが書いてある。そのあとは立場ごとに次の順で読む。

**ライセンスを発行する場合（ベンダー）。** すべてのライセンスを有効にする鍵を持ち、SaaS 型のサービスを運用することもある。

1. [トラストチェーン](explanation/trust-chain.md): ルート鍵と署名鍵、その鍵を安全に保つ方法。
2. [ライセンスファイルと検証](explanation/license-files-and-verification.md): 何を発行し、アプリが何を確かめるか。
3. [アクティベーション](explanation/activation.md): SaaS 型のサービスがライセンスをデバイスに紐付ける仕組み。
4. [フローティングライセンス](explanation/floating-licenses.md): シート、リース、サーバーライセンス。
5. [セキュリティモデル](explanation/security-model.md): 何を守り、何を守らないか。ベンダーの責任は何か。

**ElasticLM をアプリに組み込む場合（開発者）。** アプリにルート公開鍵を組み込み、ライセンス、アクティベーション、リースを確かめる。

1. [ライセンスファイルと検証](explanation/license-files-and-verification.md)
2. [デバイスとプルーフ](explanation/devices-and-proofs.md): デバイスを識別するインストール鍵。
3. [アクティベーション](explanation/activation.md)か[フローティングライセンス](explanation/floating-licenses.md)のうち、製品で使うほう。
4. [セキュリティモデル](explanation/security-model.md): アプリが確かめなければならないことと、アプリでは防げないこと。

**組織のライセンスサーバーを運用する場合（顧客の管理者）。** 組織のシートを貸し出す `elm-server` を運用する。

1. [フローティングライセンス](explanation/floating-licenses.md): リース、借用、サーバーを再起動したときに何が起きるか。
2. [デバイスとプルーフ](explanation/devices-and-proofs.md): クライアントが自分の身元を証明する方法。
3. [セキュリティモデル](explanation/security-model.md): サーバーの鍵とログが何を守るか。

**販売や課金のシステムを連携する場合（ベンダーのシステム）。** システムは API キーを使って、SaaS 型のサービスの管理 API を呼び出す。

1. [アクティベーション](explanation/activation.md): サービスから見た製品、ポリシー、ライセンス、デバイス。
2. [セキュリティモデル](explanation/security-model.md): API キー、ロール、監査ログ。

ElasticLM がなぜこう作られているのかは、[設計上の選択](explanation/design-choices.md)で説明している。

## ほかの資料

- 脆弱性の報告と、ダウンロードしたファイルの確かめ方: [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md)。
- `elm-service` のコンテナイメージの動かし方: [docs/container.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/container.md)。
- 設計書（日本語）: [docs/design.ja.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/design.ja.md)。
