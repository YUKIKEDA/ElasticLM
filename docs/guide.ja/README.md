# ElasticLM ドキュメント

ElasticLM を使うと、.NET や C/C++ アプリのベンダーは、ライセンスを発行し、顧客のマシンでそのライセンスを検証し、同時使用数を数えられる。このドキュメントの読者は、ライセンスを発行する人、アプリに検証を組み込む開発者、ライセンスサーバーを運用する管理者だ。自分が使うプラットフォームの知識は前提にするが、設計書を読んでいることは前提にしない。

> **状況:** 試験公開のあいだ、ElasticLM は [Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)で GitHub のプレリリースとして配布する。そこにプレリリースがまだなければ、チュートリアルでダウンロードするプログラムとパッケージは、`CONTRIBUTING.md` の [Build from source](https://github.com/YUKIKEDA/ElasticLM/blob/main/CONTRIBUTING.md#build-from-source) に従って自分でビルドできる。各ページは `main` ブランチの内容を説明している。チュートリアルはまだ執筆中だが、[ライセンスを発行して .NET のアプリで確かめる](tutorials/verify-a-license-offline.md)、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](tutorials/activate-an-app-online.md)、[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](tutorials/use-a-floating-license.md)、[ライセンスを発行して C のアプリで確かめる](tutorials/verify-a-license-in-c.md)は書き終えている。ハウツーガイドはまだ執筆中だが、[ポリシーを設定する](how-to/set-up-a-policy.md)、[トライアルを提供する](how-to/offer-a-trial.md)、[ネットワークにつながらないデバイスをアクティベーションする](how-to/activate-a-device-without-a-network.md)、[デバイスのアクティベーションを解除する](how-to/deactivate-a-device.md)、[ルート鍵を安全に保管する](how-to/keep-the-root-key-safe.md)、[署名鍵を入れ替える](how-to/rotate-the-signing-key.md)、[ライセンスを失効させる](how-to/revoke-a-license.md)、[署名鍵を失効させる](how-to/revoke-a-signing-key.md)、[自分の TLS 証明書を使う](how-to/use-your-own-tls-certificate.md)、[サーバーライセンスを更新する](how-to/renew-a-server-license.md)、[借用を制限する](how-to/limit-borrowing.md)、[利用ログを検証する](how-to/verify-the-usage-log.md)、[エラーを処理する](how-to/handle-errors.md)、[ネットワークがなくても動かす](how-to/run-without-a-network.md)、[NativeAOT で発行する](how-to/publish-with-nativeaot.md)、[リクエストのレートを制限する](how-to/limit-request-rates.md)、[サービスをバックアップして復元する](how-to/back-up-and-restore-the-service.md)、[サービスをアップグレードする](how-to/upgrade-the-service.md)、[サービスをコンテナで動かす](how-to/run-the-service-in-a-container.md)、[サービスを PostgreSQL で動かす](how-to/run-the-service-on-postgresql.md)、[OIDC でサインインする](how-to/sign-in-with-oidc.md)、[elm-service でサーバーライセンスをアクティベーションする](how-to/activate-a-server-license-with-elm-service.md)は書き終えている。リファレンスは、`elm` のコマンド、`elm-server` と `elm-service` の設定、管理 API、デバイスのプロトコル、トークンとファイルの形式、エラーコード、ログ、.NET API、C API の部分を書き終えている。下に挙げる解説のページは、ElasticLM の仕組みを説明している。

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

1. [ライセンスを発行して .NET のアプリで確かめる](tutorials/verify-a-license-offline.md): 鍵を作り、ライセンスを発行して確かめるチュートリアル。
2. [SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](tutorials/activate-an-app-online.md): `elm-service` を動かし、そこに製品、トライアル、ライセンスを作るチュートリアル。
3. [ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](tutorials/use-a-floating-license.md): シートを含むサーバーライセンスを発行し、そのシートを貸し出すサーバーを動かすチュートリアル。
4. [ポリシーを設定する](how-to/set-up-a-policy.md)と[トライアルを提供する](how-to/offer-a-trial.md): 販売するライセンスの規則と、トライアルについてのハウツーガイド。
5. [ネットワークにつながらないデバイスをアクティベーションする](how-to/activate-a-device-without-a-network.md)と[デバイスのアクティベーションを解除する](how-to/deactivate-a-device.md): サービスに一度もつながらないデバイスと、ライセンスを別のデバイスに移すことについてのハウツーガイド。
6. [ルート鍵を安全に保管する](how-to/keep-the-root-key-safe.md)と[署名鍵を入れ替える](how-to/rotate-the-signing-key.md): 署名鍵を証明する鍵の保管と、サービスが署名に使う鍵の入れ替えについてのハウツーガイド。
7. [ライセンスを失効させる](how-to/revoke-a-license.md)と[署名鍵を失効させる](how-to/revoke-a-signing-key.md): ライセンスを完全に終わらせることと、漏れた署名鍵についてのハウツーガイド。
8. [サーバーライセンスを更新する](how-to/renew-a-server-license.md): サーバーライセンスを期限が切れる前に発行し直すことと、新しいファイルでは取り上げられないものについてのハウツーガイド。
9. [elm-service でサーバーライセンスをアクティベーションする](how-to/activate-a-server-license-with-elm-service.md): `elm-service` を通してサーバーライセンスを販売し、顧客のサーバーがそこでライセンスをアクティベーションして更新するようにすることについてのハウツーガイド。
10. [リクエストのレートを制限する](how-to/limit-request-rates.md)、[サービスをバックアップして復元する](how-to/back-up-and-restore-the-service.md)、[サービスをアップグレードする](how-to/upgrade-the-service.md): `elm-service` のリクエストの上限と、SQLite で動かすときのバックアップとアップグレードについてのハウツーガイド。
11. [サービスをコンテナで動かす](how-to/run-the-service-in-a-container.md)と[サービスを PostgreSQL で動かす](how-to/run-the-service-on-postgresql.md): `elm-service` をイメージから動かすことと、複数のノードが共有するデータベースで動かすことについてのハウツーガイド。
12. [OIDC でサインインする](how-to/sign-in-with-oidc.md): API キーの代わりに、管理者が自社の ID プロバイダーでサインインできるようにするためのハウツーガイド。
13. [トラストチェーン](explanation/trust-chain.md): ルート鍵と署名鍵、その鍵を安全に保つ方法。
14. [ライセンスファイルと検証](explanation/license-files-and-verification.md): 何を発行し、アプリが何を確かめるか。
15. [アクティベーション](explanation/activation.md): SaaS 型のサービスがライセンスをデバイスに紐付ける仕組み。
16. [フローティングライセンス](explanation/floating-licenses.md): シート、リース、サーバーライセンス。
17. [セキュリティモデル](explanation/security-model.md): 何を守り、何を守らないか。ベンダーの責任は何か。

**ElasticLM をアプリに組み込む場合（開発者）。** アプリにルート公開鍵を組み込み、ライセンス、アクティベーション、リースを確かめる。

1. [ライセンスを発行して .NET のアプリで確かめる](tutorials/verify-a-license-offline.md): ライセンスをオフラインで検証する .NET のアプリを作るチュートリアル。
2. [SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](tutorials/activate-an-app-online.md): トライアルを始め、ライセンスキーでアクティベーションする .NET のアプリを作るチュートリアル。
3. [ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](tutorials/use-a-floating-license.md): ライセンスサーバーからシートのリースを受け、シートを借用する .NET のアプリを作るチュートリアル。
4. [ライセンスを発行して C のアプリで確かめる](tutorials/verify-a-license-in-c.md): `libelasticlm` をリンクし、ライセンスをオフラインで検証する C のプログラムを作るチュートリアル。.NET でないアプリ向け。
5. [エラーを処理する](how-to/handle-errors.md)と[ネットワークがなくても動かす](how-to/run-without-a-network.md): アクティベーション、確認、トークンの更新が失敗したときと、ネットワークが使えないあいだに、アプリが何をするかについてのハウツーガイド。
6. [NativeAOT で発行する](how-to/publish-with-nativeaot.md): アプリを 1 つのネイティブ実行ファイルとして出荷することについてのハウツーガイド。
7. [ネットワークにつながらないデバイスをアクティベーションする](how-to/activate-a-device-without-a-network.md)と[デバイスのアクティベーションを解除する](how-to/deactivate-a-device.md): オフラインのアクティベーション、トークンの更新、アクティベーション解除をするアプリのコードを載せたハウツーガイド。
8. [署名鍵を失効させる](how-to/revoke-a-signing-key.md): ベンダーが署名鍵を失効させたときに、アプリのトラストストアに何を載せるかについてのハウツーガイド。
9. [ライセンスファイルと検証](explanation/license-files-and-verification.md)
10. [デバイスとプルーフ](explanation/devices-and-proofs.md): デバイスを識別するインストール鍵。
11. [アクティベーション](explanation/activation.md)か[フローティングライセンス](explanation/floating-licenses.md)のうち、製品で使うほう。
12. [セキュリティモデル](explanation/security-model.md): アプリが確かめなければならないことと、アプリでは防げないこと。
13. [.NET API](reference/dotnet-api.md) か [C API](reference/c-api.md): アプリに組み込むライブラリのすべての型、関数、オプション。

**組織のライセンスサーバーを運用する場合（顧客の管理者）。** 組織のシートを貸し出す `elm-server` を運用する。

1. [ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](tutorials/use-a-floating-license.md): サーバーライセンスを使って `elm-server` を準備し、サーバーが何を貸し出すかを確かめるチュートリアル。
2. [サーバーライセンスを更新する](how-to/renew-a-server-license.md): ベンダーの新しいサーバーライセンスを、動いているサーバーに読み込ませることについてのハウツーガイド。
3. [elm-service でサーバーライセンスをアクティベーションする](how-to/activate-a-server-license-with-elm-service.md): ベンダーのライセンスキーでサーバーをアクティベーションし、そのライセンスをオンラインかファイルで更新することについてのハウツーガイド。
4. [借用を制限する](how-to/limit-borrowing.md): 借用を短くすることと、通常のリースのためにシートを残すことについてのハウツーガイド。
5. [自分の TLS 証明書を使う](how-to/use-your-own-tls-certificate.md): 組織の CA が発行した証明書と、そのときアプリのマシンが何を信頼しなければならないかについてのハウツーガイド。
6. [利用ログを検証する](how-to/verify-the-usage-log.md): サーバーが署名したログを確かめることと、そのコピーを取っておくことについてのハウツーガイド。
7. [署名鍵を失効させる](how-to/revoke-a-signing-key.md#ライセンスサーバーで鍵を失効させる): ベンダーが署名鍵を失効させたときに、サーバーで何を設定するかについてのハウツーガイド。
8. [フローティングライセンス](explanation/floating-licenses.md): リース、借用、サーバーを再起動したときに何が起きるか。
9. [デバイスとプルーフ](explanation/devices-and-proofs.md): クライアントが自分の身元を証明する方法。
10. [セキュリティモデル](explanation/security-model.md): サーバーの鍵とログが何を守るか。

**販売や課金のシステムを連携する場合（ベンダーのシステム）。** システムは API キーを使って、SaaS 型のサービスの管理 API を呼び出す。

1. [SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](tutorials/activate-an-app-online.md): API キーを使い、管理 API で製品、そのポリシー、ライセンスを作るチュートリアル。
2. [ポリシーを設定する](how-to/set-up-a-policy.md)、[トライアルを提供する](how-to/offer-a-trial.md)、[デバイスのアクティベーションを解除する](how-to/deactivate-a-device.md)、[ライセンスを失効させる](how-to/revoke-a-license.md): ポリシーとトライアルを作る呼び出し、デバイスのアクティベーションを解除する呼び出し、ライセンスを失効させる呼び出しについてのハウツーガイド。
3. [アクティベーション](explanation/activation.md): サービスから見た製品、ポリシー、ライセンス、デバイス。
4. [セキュリティモデル](explanation/security-model.md): API キー、ロール、監査ログ。
5. [管理 API](reference/administration-api.md): システムが呼び出すすべてのルートと、そのリクエストとレスポンス。

ElasticLM がなぜこう作られているのかは、[設計上の選択](explanation/design-choices.md)で説明している。

**リファレンス。** 正確な名前を調べるときは、[elm コマンドリファレンス](reference/elm-cli.md)、[elm-server の設定](reference/server-configuration.md)、[elm-service の設定](reference/service-configuration.md)、[管理 API](reference/administration-api.md)、[デバイスのプロトコル](reference/device-protocol.md)、[トークンとファイルの形式](reference/formats.md)、[エラーコード](reference/error-codes.md)、[ログ](reference/logs.md)、[.NET API](reference/dotnet-api.md)、[C API](reference/c-api.md) を使う。

## ほかの資料

- 脆弱性の報告と、ダウンロードしたファイルの確かめ方: [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md)。
- `elm-service` のコンテナイメージのビルドのしかたと確かめ方（イメージは、プレリリースでない最初のリリースから公開する）: [docs/container.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/container.md)。動かし方は[サービスをコンテナで動かす](how-to/run-the-service-in-a-container.md)を読む。
- 設計書（日本語）: [docs/design.ja.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/design.ja.md)。
