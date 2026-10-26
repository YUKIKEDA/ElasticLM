# ロードマップ

進捗の正本。設計（何を作るか）は [design.ja.md](./design.ja.md)、却下した案は [design-decisions.md](./design-decisions.md) にある。

GitHub Issue は着手する作業単位だけ切る。完了条件の詳細は Issue 本文に書き、この表は ID・内容・状態・Issue だけを持つ。

状態: `not-started` / `in-progress` / `blocked` / `done`

## 運用

1. 新しい作業は、先にこの表へ行を足してから Issue を切る。行の追加・削除は Grill を経る（`.cursor/rules/workflow.mdc`）。
2. 次の Issue は、前の行が `done` または `blocked` になってから切る。
3. 設計の前提が折れたら、その行を `blocked` にし、設計を直してから次へ進む。
4. 結果は GitHub Issue の本文に残す。この表は状態と Issue リンクだけ。

## 今

- **フェーズ:** D0（要件・脅威モデル・全体構成の Grill と設計書）
- **済み:** F0（[#1](https://github.com/YUKIKEDA/ElasticLM/issues/1)）

## 全体

| ID  | 内容                                                                                       | 状態        | Issue |
| --- | ------------------------------------------------------------------------------------------ | ----------- | ----- |
| F0  | 開発基盤（規約・スキル・ビルド設定・CI・テンプレート・コミュニティ文書）                   | done        | [#1](https://github.com/YUKIKEDA/ElasticLM/issues/1) |
| D0  | 要件・脅威モデル・全体構成（オフライン検証・オンプレミスのフローティング・SaaS 型の有効化） | not-started | —     |
| F1  | リリース基盤（release.yml、パッケージと単一バイナリの検査、NuGet の Trusted Publishing）   | not-started | —     |

D0 の設計が固まった時点で、実装の行（マイルストーンごとの行）をこの表に足す。F1 は、配布するパッケージと実行ファイルの名前が D0 で決まってから着手する。

## Intentionally out of scope

D0 の Grill で決める。
