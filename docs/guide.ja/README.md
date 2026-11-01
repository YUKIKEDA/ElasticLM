# ElasticLM の文書

ElasticLM は、.NET や C/C++ のアプリのベンダーが、ライセンスを発行し、顧客のマシンでそれを検証し、同時利用数を数えるための仕組みだ。この文書は、ライセンスを発行する人、アプリに検証を組み込む開発者、ライセンスサーバーを運用する管理者に向けて書いている。自分が使うプラットフォームの知識は前提にするが、設計書を読んでいることは前提にしない。

> **状況:** 1.0 はまだリリースされていない。これらのページは `main` ブランチの内容を説明する。チュートリアル、ハウツーガイド、リファレンスのページはまだ書いている途中で、下に挙げるページは ElasticLM の仕組みを説明する。

## 文書の構成

各ページは4種類のどれかに当たる。いま必要なことに合った種類を選ぶ。

| 種類 | 読むとき |
| --- | --- |
| チュートリアル | 初めて使い、手順に沿って動くものを作りたいとき。 |
| ハウツーガイド | 署名鍵の入れ替えのように、やりたい作業が決まっているとき。 |
| リファレンス | コマンド、設定、フィールド、エラーコードの正確な名前を知りたいとき。 |
| 解説 | ある部分がどう動くのか、なぜそうなっているのか、何から守るのかを理解したいとき。 |

## どこから読むか

立場にかかわらず、まず[概要](explanation/overview.md)を読む。各部分の名前と、誰がどれを運用するかが書いてある。そのあとは次のとおり。

**ライセンスを発行する人（ベンダー）。** すべてのライセンスを有効にする鍵を持ち、SaaS 型のサービスを運用することもある。

1. [信頼の鎖](explanation/trust-chain.md): ルート鍵と署名鍵、それらを安全に保つ方法。
2. [ライセンスファイルと検証](explanation/license-files-and-verification.md): 何を発行し、アプリが何を確かめるか。
3. [有効化](explanation/activation.md): SaaS 型のサービスがライセンスを端末に紐づける方法。
4. [フローティングライセンス](explanation/floating-licenses.md): 席、リース、サーバーライセンス。
5. [安全性のモデル](explanation/security-model.md): 何を守り、何を守らないか、あなたの責任は何か。

**ElasticLM をアプリに組み込む人（開発者）。** アプリはルート公開鍵を組み込み、ライセンス、有効化、リースを確かめる。

1. [ライセンスファイルと検証](explanation/license-files-and-verification.md)
2. [端末と証明](explanation/devices-and-proofs.md): 端末を識別するインストール鍵。
3. [有効化](explanation/activation.md)か[フローティングライセンス](explanation/floating-licenses.md)の、製品で使うほう。
4. [安全性のモデル](explanation/security-model.md): アプリが確かめるべきことと、アプリには防げないこと。

**組織のライセンスサーバーを運用する人（顧客の管理者）。** 組織の席を貸し出す `elm-server` を運用する。

1. [フローティングライセンス](explanation/floating-licenses.md): リース、借り出し、サーバーを再起動したときに起きること。
2. [端末と証明](explanation/devices-and-proofs.md): クライアントが自分が誰かを証明する方法。
3. [安全性のモデル](explanation/security-model.md): サーバーの鍵とログが守るもの。

**販売や課金のシステムをつなぐ人（ベンダーのシステム）。** システムは API キーで SaaS 型のサービスの管理 API を呼ぶ。

1. [有効化](explanation/activation.md): サービスから見た製品、ポリシー、ライセンス、端末。
2. [安全性のモデル](explanation/security-model.md): API キー、ロール、監査ログ。

ElasticLM がなぜこう作られているかを知りたいときは、[設計の選択](explanation/design-choices.md)を読む。

## ほかの場所

- 脆弱性の報告と、ダウンロードしたファイルの確認: [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md)。
- `elm-service` のコンテナイメージの動かし方: [docs/container.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/container.md)。
- 設計書（日本語）: [docs/design.ja.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/design.ja.md)。
