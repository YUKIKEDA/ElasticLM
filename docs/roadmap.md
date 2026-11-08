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

- **フェーズ:** V1c12（`elm-server` と `elm-service` が TLS の証明書の中間 CA の証明書を送る。決めた形は design.ja.md §13.5 の「運用者の証明書の中間 CA」）
- **済み:** F0（[#1](https://github.com/YUKIKEDA/ElasticLM/issues/1)）、D0（[#3](https://github.com/YUKIKEDA/ElasticLM/issues/3)）、M1（[#7](https://github.com/YUKIKEDA/ElasticLM/issues/7)）、M2（[#11](https://github.com/YUKIKEDA/ElasticLM/issues/11)）、M3a（[#15](https://github.com/YUKIKEDA/ElasticLM/issues/15)）、M3b（[#21](https://github.com/YUKIKEDA/ElasticLM/issues/21)）、M3c（[#25](https://github.com/YUKIKEDA/ElasticLM/issues/25)）、M3d（[#28](https://github.com/YUKIKEDA/ElasticLM/issues/28)）、M4a1（[#46](https://github.com/YUKIKEDA/ElasticLM/issues/46)）、M4a2（[#54](https://github.com/YUKIKEDA/ElasticLM/issues/54)）、M4a3（[#58](https://github.com/YUKIKEDA/ElasticLM/issues/58)）、M4b1（[#60](https://github.com/YUKIKEDA/ElasticLM/issues/60)）、M4b2（[#66](https://github.com/YUKIKEDA/ElasticLM/issues/66)）、M4c1（[#70](https://github.com/YUKIKEDA/ElasticLM/issues/70)）、M4c2（[#74](https://github.com/YUKIKEDA/ElasticLM/issues/74)）、M4c3（[#78](https://github.com/YUKIKEDA/ElasticLM/issues/78)）、M4d1（[#82](https://github.com/YUKIKEDA/ElasticLM/issues/82)）、M4d2（[#86](https://github.com/YUKIKEDA/ElasticLM/issues/86)）、M4d3（[#90](https://github.com/YUKIKEDA/ElasticLM/issues/90)）、W0（[#94](https://github.com/YUKIKEDA/ElasticLM/issues/94)）、M5a（[#102](https://github.com/YUKIKEDA/ElasticLM/issues/102)）、M5b（[#109](https://github.com/YUKIKEDA/ElasticLM/issues/109)）、M5c（[#116](https://github.com/YUKIKEDA/ElasticLM/issues/116)）、M5d1（[#125](https://github.com/YUKIKEDA/ElasticLM/issues/125)）、M5d2（[#129](https://github.com/YUKIKEDA/ElasticLM/issues/129)）、M6a（[#137](https://github.com/YUKIKEDA/ElasticLM/issues/137)）、M6b（[#141](https://github.com/YUKIKEDA/ElasticLM/issues/141)）、M6c（[#145](https://github.com/YUKIKEDA/ElasticLM/issues/145)）、F1（[#149](https://github.com/YUKIKEDA/ElasticLM/issues/149)）、F2（[#157](https://github.com/YUKIKEDA/ElasticLM/issues/157)）、V1a（[#178](https://github.com/YUKIKEDA/ElasticLM/issues/178)）、V1b（[#182](https://github.com/YUKIKEDA/ElasticLM/issues/182)）、V1c1（[#190](https://github.com/YUKIKEDA/ElasticLM/issues/190)）、V1c2a（[#197](https://github.com/YUKIKEDA/ElasticLM/issues/197)）、V1c2b（[#202](https://github.com/YUKIKEDA/ElasticLM/issues/202)）、V1c7（[#203](https://github.com/YUKIKEDA/ElasticLM/issues/203)）、V1c2c（[#208](https://github.com/YUKIKEDA/ElasticLM/issues/208)）、V1c3a（[#213](https://github.com/YUKIKEDA/ElasticLM/issues/213)）、V1c3b（[#217](https://github.com/YUKIKEDA/ElasticLM/issues/217)）、V1c3e（[#219](https://github.com/YUKIKEDA/ElasticLM/issues/219)）、V1c3d（[#223](https://github.com/YUKIKEDA/ElasticLM/issues/223)）、V1c8（[#209](https://github.com/YUKIKEDA/ElasticLM/issues/209)）、V1c9（[#227](https://github.com/YUKIKEDA/ElasticLM/issues/227)）、V1c3c（[#236](https://github.com/YUKIKEDA/ElasticLM/issues/236)）、V1c10（[#238](https://github.com/YUKIKEDA/ElasticLM/issues/238)）、V1c11（[#242](https://github.com/YUKIKEDA/ElasticLM/issues/242)）、V1c4a（[#248](https://github.com/YUKIKEDA/ElasticLM/issues/248)）、V1c4b（[#252](https://github.com/YUKIKEDA/ElasticLM/issues/252)）、V1c4c（[#256](https://github.com/YUKIKEDA/ElasticLM/issues/256)）

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
| M5a | 管理者の土台と CLI のログイン（JwtBearer、`elm admin login`・`logout`・`request`、管理者の一覧と無効化の API、最後のログイン、90 日ログインのない管理者のキーを止める規則、偽の IdP と Keycloak のテスト、大きさの差。Grill で決める点は design.ja.md §14.6） | done        | [#102](https://github.com/YUKIKEDA/ElasticLM/issues/102) |
| M5b | 管理画面のログイン（BFF。認可コードと PKCE、データベースのセッションとその一覧・取り消し、管理者の無効化でセッションを消す、最後のログインの更新、Data Protection の鍵の保存、`X-ELM-CSRF`、Keycloak のテストに認可コードのフロー、大きさの差。Grill で決める点は design.ja.md §14.6） | done        | [#109](https://github.com/YUKIKEDA/ElasticLM/issues/109) |
| M5c | 管理画面の土台（`web/admin/`。`build.ps1` のビルドと `tsc`・Vitest のゲート、道具の版の確かめ、`dist` がないときの Service のビルドの誤り、秘密を持たない CI の SPA のジョブ、埋め込みと CSP つきの配信、`elm-service` の大きさの差と Windows のパスの長さの確かめ、AGENTS.md・CONTRIBUTING.md・規則・規約への Node.js と pnpm の追記、最初の画面: ライセンスの検索・詳細・失効） | done        | [#116](https://github.com/YUKIKEDA/ElasticLM/issues/116) |
| M5d1 | 管理画面の日々の操作（発行（キーの一度だけの表示とライセンスファイル）、ライセンスの一時停止・再開・延長、有効化の一覧と解除、オフラインの処理、監査ログ。決めた形は design.ja.md 0.102） | done        | [#125](https://github.com/YUKIKEDA/ElasticLM/issues/125) |
| M5d2 | 管理画面のサポートと管理（リースの一覧と強制返却、試用の一覧と削除、管理者、API キー、セッション。決めた形は design.ja.md 0.103） | done        | [#129](https://github.com/YUKIKEDA/ElasticLM/issues/129) |
| M6a | `libelasticlm` の土台と検証（`ElasticLM.Native` の共有ライブラリ、C ヘッダー、3 つの OS の C のテスト、ライセンスの検証・結果・誤りの関数、大きさ。決めた形は design.ja.md 0.105） | done        | [#137](https://github.com/YUKIKEDA/ElasticLM/issues/137) |
| M6b | `libelasticlm` の有効化（`elm_activation`。オンラインの操作、試用、オフラインのファイル、自動の更新とコールバック。決めた形は design.ja.md 0.106） | done        | [#141](https://github.com/YUKIKEDA/ElasticLM/issues/141) |
| M6c | `libelasticlm` のリース（`elm_client`。オンプレミスと SaaS 型のリース、更新のスレッド、コールバック、借り出し、サーバーのピンの消去、C のスレッドが終わったときのスレッドごとの記録の確かめ。決めた形は design.ja.md 0.107） | done        | [#145](https://github.com/YUKIKEDA/ElasticLM/issues/145) |
| F1  | リリース基盤（release.yml、パッケージと単一バイナリの検査、NuGet の Trusted Publishing、CI が作らない 4 つの RID（`win-arm64`、`linux-arm64`、`linux-musl-x64`、`osx-x64`）の 3 つの実行ファイルと `libelasticlm`。決めた形は design.ja.md 0.109） | done        | [#149](https://github.com/YUKIKEDA/ElasticLM/issues/149) |
| F2  | `elm-service` のコンテナイメージ（ベースの選び方、root でない利用者、データと鍵の渡し方、レジストリへの公開の経路とその脅威、イメージの来歴の証明。決めた形は design.ja.md 0.111） | done        | [#157](https://github.com/YUKIKEDA/ElasticLM/issues/157) |
| V1a | 端から端までのテスト（`tests/ElasticLM.E2E`。決めた形は design.ja.md 0.113） | done        | [#178](https://github.com/YUKIKEDA/ElasticLM/issues/178) |
| V1b | §4.2 の参照機（`ubuntu-latest`）での計測と、Windows で測った値の測り直し（決めた形は design.ja.md 0.114、0.115） | done        | [#182](https://github.com/YUKIKEDA/ElasticLM/issues/182) |
| V1c1 | 利用者の文書の Explanation（全体の構成、信頼の鎖、認証の仕組みと流れ、安全性のモデル）、入口の文書、README、Docusaurus のサイトの土台と公開のジョブ（決めた形は design.ja.md 0.116、Grill で決める点は §20.7） | done        | [#190](https://github.com/YUKIKEDA/ElasticLM/issues/190) |
| V1c2a | 利用者の文書の Reference の前半（`elm` のコマンド、`elm-server` と `elm-service` の設定、誤りのコード、ログ。英語と日本語。決めた形は design.ja.md §20.7 の V1c2 の形） | done        | [#197](https://github.com/YUKIKEDA/ElasticLM/issues/197) |
| V1c2b | 利用者の文書の Reference の管理 API とデバイスのプロトコル、経路と HTTP の状態を頁と比べるテスト（英語と日本語。決めた形は design.ja.md §20.7 の V1c2 の形） | done        | [#202](https://github.com/YUKIKEDA/ElasticLM/issues/202) |
| V1c2c | 利用者の文書の Reference のトークンとファイルの形式、.NET と C の API の要点、公開の型と関数を頁と比べるテスト（英語と日本語。V1c2b の Grill で分けた。決めた形は design.ja.md §20.7 の V1c2 の形） | done        | [#208](https://github.com/YUKIKEDA/ElasticLM/issues/208) |
| V1c3a | 利用者の文書の Tutorials の土台（手順を CI で流す仕組み、コードをサンプルから引く仕組み、サイドバーの区分）と Tutorial 1（.NET のアプリでオフラインで確かめる）とそのサンプル `samples/VerifyOffline`（英語と日本語。V1c3 の Grill で分けた。決めた形は design.ja.md §20.7 の V1c3 の形） | done        | [#213](https://github.com/YUKIKEDA/ElasticLM/issues/213) |
| V1c3b | 利用者の文書の Tutorial 3（`elm-server` でフローティングのライセンスと借り出し）とそのサンプル `samples/FloatingLease`。手順を流す仕組みに、裏で動かすサーバー、出力から値を取る印とプレースホルダー、0 でない終了コード、`elm-server` の行を足す（英語と日本語。決めた形は design.ja.md §20.7 の V1c3 の形） | done        | [#217](https://github.com/YUKIKEDA/ElasticLM/issues/217) |
| V1c3e | 利用者の文書を、試しの期間の読み手（GitHub のプレリリースを手に入れた読み手）がすることで書く形に改める（実行ファイルは GitHub のプレリリースの圧縮ファイルから、パッケージはプレリリースの `.nupkg` と頁の `nuget.config` と `--prerelease --source packages` で入れる形にし、「まだ公開していない」という文を外す。最初の rc より前の読み手には `CONTRIBUTING.md` の「Build from source」の節を足す。手順を流す仕組みは印のところでジョブの実行ファイルとプレリリースの版で pack したパッケージに置き換える。英語と日本語。持ち主の指摘で足した。V1c3d の前。決めた形は design.ja.md §20.7 の V1c3 の形） | done        | [#219](https://github.com/YUKIKEDA/ElasticLM/issues/219) |
| V1c3d | 利用者の文書の Tutorial 2（`elm-service` でオンラインの有効化と試用）とそのサンプル、ノンスの秘密を作る `elm` のコマンド。`elm-service` は GitHub のプレリリースの圧縮ファイルから入れる形で書く（ソースからは作らず、rc はコンテナイメージを push しないのでイメージも使わない。V1c3e の方針）。手順を流す仕組みに、`elm-service` の prebuilt、取った値を頁の外で `ELM_API_KEY` に設定すること、取った値で `steps:file` の中身を埋めることを足す（英語と日本語。V1c3b の Grill で分けた。`steps:file` の分は V1c3b の設計のレビューの R1 で移した。V1c3e のあと。決めた形は design.ja.md §20.7 の V1c3 の形の「V1c3d の Grill」） | done | [#223](https://github.com/YUKIKEDA/ElasticLM/issues/223) |
| V1c3c | 利用者の文書の Tutorial 4（C のアプリに `libelasticlm`）と C のサンプル。手順を流す仕組みに C のコンパイラーと、ビルドしたアプリの実行ファイルで始まる行を足す（英語と日本語。V1c8 のあと。実行ファイルの行は V1c3b の設計のレビューの R2 で移した。V1c8 で C の API の頁から移した 4 つの手順（展開、Windows の x64 か ARM64 の Native Tools Command Prompt、macOS の quarantine、`app.c` と動かす手順と期待する出力）も Tutorial 4 に書く。Grill で決める点は design.ja.md §20.7） | done | [#236](https://github.com/YUKIKEDA/ElasticLM/issues/236) |
| V1c4a | 利用者の文書の How-to guides の土台（`docs/guide/how-to/`、サイドバーの区分、手順を流す仕組みが Tutorial 2 の終わりの状態から How-to の頁を流すこと）、ライセンスの 4 頁（ポリシー、試用、オフラインの有効化、端末の解除）とオフラインの有効化のサンプル `samples/ActivateOffline`、V1c3d で見つけた文書の直し 2 つと V1c8 で見つけた直し 1 つ（英語と日本語。V1c4 の Grill で V1c4 から分けた。決めた形は design.ja.md §20.7 の「V1c4 の形」） | done | [#248](https://github.com/YUKIKEDA/ElasticLM/issues/248) |
| V1c4b | 利用者の文書の How-to guides の鍵の 4 頁（ルート鍵の保管、署名鍵の交代、ライセンスの失効、署名鍵の失効。英語と日本語。手順を流す仕組みで、引き継いだ `elm-service` を起動し直せるようにする。V1c4a のあと。決めた形は design.ja.md §20.7 の「V1c4 の形」の「V1c4b の Grill」） | done | [#252](https://github.com/YUKIKEDA/ElasticLM/issues/252) |
| V1c4c | 利用者の文書の How-to guides の `elm-server` の 4 頁（TLS の証明書、サーバーライセンスの更新、借り出しの上限、利用ログの確かめ。英語と日本語。V1c4b のあと。決めた形は design.ja.md §20.7 の「V1c4 の形」の「V1c4c の Grill」） | done | [#256](https://github.com/YUKIKEDA/ElasticLM/issues/256) |
| V1c4e | 利用者の文書の How-to guides のアプリの 3 頁（誤りの扱い、ネットワークのないときの振る舞い、NativeAOT での発行。英語と日本語。V1c4c のあと。Grill で決める点は design.ja.md §20.7 の「V1c4 の形」） | not-started | — |
| V1c4d | 利用者の文書の How-to guides の `elm-service` の運用の 6 頁（コンテナー、PostgreSQL、OIDC での管理者のログイン、バックアップと戻し、版の更新、速さの上限。英語と日本語。V1c4e のあと。PostgreSQL、Keycloak、コンテナー、Entra ID の頁の流し方も、この行の Grill で決める。design.ja.md §20.7 の「V1c4 の形」） | not-started | — |
| V1c4f | 利用者の文書の How-to guides の、`elm-service` で `elm-server` のサーバーライセンスを有効化してオンラインとオフラインで更新する頁（英語と日本語。V1c4c の Grill の Q4 で足した。V1c4d のあと。名指す Tutorial と流し方、裏のプロセスを 2 つ持てるようにする手順を流す仕組みの変更はこの行の Grill で決める。design.ja.md §20.7 の表の V1c4f の行） | not-started | — |
| V1c5 | 文書のサイトで、頁の 2 つ目以降の Mermaid の flowchart が空になる問題の原因を調べて直す（V1e の最初の公開の前。design.ja.md §20.7 の V1c1 の形） | not-started | [#192](https://github.com/YUKIKEDA/ElasticLM/issues/192) |
| V1c6 | 利用者が見る文（CLI の出力、誤りとログの文、公開する API の XML ドキュメント）から設計の節番号を外す。内部のコメントと公開しない型の XML ドキュメントは残す。`elm server reload` の誤りの文でコードが 2 度出る重なりも直す（V1c4c の PR の持ち主の決定 D2）（V1d の前。design.ja.md §20.7 の V1c2 の形） | not-started | [#198](https://github.com/YUKIKEDA/ElasticLM/issues/198) |
| V1c7 | `elm server status` が、有効期限のないサーバーライセンスで例外になる不具合の修正（V1c2b で見つけた。V1c2c の前） | done        | [#203](https://github.com/YUKIKEDA/ElasticLM/issues/203) |
| V1c8 | `libelasticlm` の Windows のアーカイブにインポートライブラリ（`elasticlm.lib`）を入れるかを調べて決め、入れるなら配って CI で確かめる。Linux と macOS でビルド時にリンクする方法も試して、事実として C の頁に書く（OS ごとの手順は V1c3c。V1c2c で見つけた。V1c3d のあと、V1c3c の前。決めた形は design.ja.md 0.118） | done | [#209](https://github.com/YUKIKEDA/ElasticLM/issues/209) |
| V1c9 | `elm-server` が、待ち受けるポートがふさがっていると、扱っていない例外で終わる不具合の修正（V1c3d で見つけた。V1c8 のあと、V1c3c の前。決めた形は design.ja.md §13.1 の「V1c9 の Grill」） | done | [#227](https://github.com/YUKIKEDA/ElasticLM/issues/227) |
| V1c10 | `libelasticlm` の `ELM_INVALID_ARGUMENT` の文の終わりに、.NET の引数の名前（`(Parameter 'rootPublicKeysPem')`）が付く不具合の修正（V1c3c で見つけた。PR #237 の持ち主の決定で行にした。V1c3c のあと、V1c4 の前。決めた形は design.ja.md §17.3 の「V1c10 の Grill」） | done | [#238](https://github.com/YUKIKEDA/ElasticLM/issues/238) |
| V1c11 | `ActivationClient` と `LicenseClient` が、サーバーの空の `ELM-Nonce` ヘッダーを受け入れ、次の要求の証明を作るところで `ArgumentException` を投げる不具合の修正（V1c10 で見つけた。PR #241 の持ち主の決定で行にした。V1c10 のあと、V1c4 の前。決めた形は design.ja.md §12.1 の V1c11 の文） | done | [#242](https://github.com/YUKIKEDA/ElasticLM/issues/242) |
| V1c12 | `elm-server`（PEM と PKCS#12）と `elm-service`（PEM）が、TLS の証明書のファイルにある中間 CA の証明書を、サーバーの証明書と一緒に送るようにする（今は 1 枚目の証明書だけを読む。`TlsCertificateFiles.cs`）（V1c4c の実装で見つけた。PR の持ち主の決定 D1 で行にした。V1c4c のあと、V1c4e の前。決めた形は design.ja.md §13.5 の「運用者の証明書の中間 CA」） | in-progress | [#260](https://github.com/YUKIKEDA/ElasticLM/issues/260) |
| V1d | 安全性の見直し（Grill で決める点は design.ja.md §20.7） | not-started | —     |
| V1e | v1 の公開（`1.0.0-rc.N` から `1.0.0`、rc のタグは GitHub のプレリリースだけを出し nuget.org と GHCR には正式版だけを push する `release.yml` の変更と `docs/releasing.md` の rc の試しの手順、rc が通さない nuget.org と GHCR への push の段を正式版のタグの前にどう確かめるか、持ち主の作業（GitHub Pages の有効化と environment `github-pages` の規則の確かめを含む）、イメージの確かめ方の試しと文書、公開のあとの `PackageValidationBaselineVersion`、rc の版にする PR での `SECURITY.md` と `CONTRIBUTING.md` の「まだ出していない」の文の書き換え、rc のプレリリースで Tutorial を 3 つの OS で手で一度ずつ流して OS ごとに記録する、正式版のタグの前に利用者の文書を nuget.org の形に切り替えて README の Status の 1 行を外す 1 つの PR とその前の ID の接頭辞の扱いの決定、`1.0.0` の公開のあとに nuget.org の形の頁を手で一度流す。Grill で決める点は design.ja.md §20.7） | not-started | —     |

## v1 の後

名前のない先送りを作らないために置く行。着手する前に Grill を経る。

| ID  | 内容                                                                     | 状態        | Issue |
| --- | ------------------------------------------------------------------------ | ----------- | ----- |
| A-1 | Webhook（受け取る側の要件が挙がったら）                                  | not-started | —     |
| A-2 | オンプレミスのサーバーの冗長化                                           | not-started | —     |
| A-3 | ML-DSA への移行（.NET の `MLDsa` が試験扱いでなくなり、3 OS で動いたら） | not-started | —     |
| A-4 | 鍵管理サービス（KMS）の署名鍵                                            | not-started | —     |
| A-5 | 設計の文書（`docs/design.ja.md`）を、開発に加わる人が初めて読める形に組み直し、日本語の用語を利用者の文書の用語集（`.cursor/rules/user-docs.mdc`）にそろえる（design.ja.md 0.116、0.117） | not-started | —     |
| A-6 | 文書のサイトの版ごとの公開と、版を指す外のファイルへのリンク（`1.0.0` の次のタグを押す前。design.ja.md 0.116、§20.7 の V1c1 の形） | not-started | —     |
| A-8 | .NET の API の参照のサイトと、管理 API の OpenAPI（要る声が挙がったら。design.ja.md 0.116） | not-started | —     |
| A-9 | 文書のサイトの Docusaurus 4 への移行（4 が出たら、3 の安全性の修正が終わる 3 か月のうちに。design.ja.md §20.7 の V1c1 の形） | not-started | —     |

## Intentionally out of scope

- 複数ベンダーの相乗り（design.ja.md 0.12）
- クライアントのバイナリの改ざん対策・難読化（0.18）
- ドングル（ハードウェアキー）
- 失効リスト（CRL）の配布（§5.4）
