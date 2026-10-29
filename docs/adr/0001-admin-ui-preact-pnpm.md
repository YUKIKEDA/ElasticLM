# 0001. 管理画面を Preact と pnpm で作る

## Status

Accepted

## Date

2026-10-29

## Issue

[#94](https://github.com/YUKIKEDA/ElasticLM/issues/94)（W0）

## Context

管理画面は静的な SPA で、ビルド結果を `elm-service` に埋め込む（§15.2）。条件は 3 つある。

- 圧縮後 1 MB 以下。
- CSP `default-src 'self'` で動く（インラインのスクリプトとスタイルを使わない）。
- `build.ps1` と CI で再現してビルドできる。

持ち主の希望は、依存が少なく、小さく、書きやすいことだった。

W0 では、5 つの候補で同じ試作を作った。候補は Preact、Svelte 5、Vue 3、SolidJS、フレームワークなしの TypeScript で、どれも Vite 8 でビルドした。試作の画面は、一覧・検索・ページ・詳細・確認つきの失効・エラー表示・`401` でのログインへの移動である。計測は Node.js 22.23.4、Windows で、時間は 3 回の最小〜最大である。パッケージの数は Windows で入ったもので、lock には OS ごとのネイティブの部品が入る。

| | Preact（プリセットなし） | SolidJS | Svelte 5 | Vue 3 | なし |
| --- | --- | --- | --- | --- | --- |
| `dist` の brotli（バイト） | 6,771 | 7,264 | 14,840 | 24,720 | 1,744 |
| 実行時の依存（推移を含む） | 1 | 4 | 0 | 25 | 0 |
| 入るパッケージの数 | 56 | 112 | 84 | 82 | 55 |
| 試作の行数（共通のファイルを除く） | 108 | 105 | 119 | 109 | 88 |
| 次の大きな版 | なし | 2.0 が RC | なし | 3.6 が RC | — |

- 4 つの問いの答えは、5 つとも Yes だった。
  - brotli で 1 MB 以下。
  - ヘッドレスの Chrome で、CSP の違反が 0 件。インラインを足した対照では 2 件出た。
  - 3 回の入れ直しとビルドで、同じバイト列になった。
  - Vitest と happy-dom で、ブラウザなしにテストできた。
- Preact 用の Vite のプリセットを使うと、入るパッケージは 124 個になる（Babel が入る）。`dist` は同じハッシュだった。
- パッケージマネージャーは、Preact（プリセットなし）で比べた。

  | | npm 11.6.2 | pnpm 12.13.0 |
  | --- | --- | --- |
  | 入れる時間（秒、キャッシュあり） | 8.1–9.5 | 1.0–1.2 |
  | 入るパッケージの数 | 56 | 56 |
  | `dist` のハッシュ | `879a73cc8ae6c72d` | `879a73cc8ae6c72d` |

- 同梱の道具は止まった。
  - npm 10.9.9（Node.js 22.23.4 に同梱）と npm 11.0.0 は、依存の解決の途中で `Cannot read properties of null (reading 'edgesOut')` で止まった。
  - pnpm 9.12.1 は Rolldown のネイティブの部品を入れず、ビルドが `Cannot find native binding` で止まった。

- 設計の独立レビューを受けて、次も確かめた。
  - Node.js 24.22.2 と Corepack で入れた pnpm 12.13.0 で、3 回の入れ直しとビルドが 22.23.4 と同じハッシュの `dist` になり、テストも通った。
  - Corepack は、`packageManager` のハッシュが違えば `Mismatch hashes` で止まった。
  - `minimumReleaseAge: 1440` を `pnpm-workspace.yaml` に書くと、17 時間前に出た `@types/node@26.6.6` の追加が `ERR_PNPM_NO_MATURE_MATCHING_VERSION` で断られた。書かない（既定の 1440 だけの）ときは入った。

試作と計測のスクリプト（`spikes/w0/` の `measure.mjs`、`browser-check.mjs`、`pm-compare.mjs`）は、コミット `0888137` にある。このコミットは PR の head の履歴にあり、`git fetch origin pull/<PR の番号>/head` で取れる。手順と全部の数は Issue #94 の Result にある。

## Decision

- 管理画面は Preact 11 と TypeScript で書き、Vite 8 でビルドする。Preact 用の Vite のプリセットは使わない。テストは Vitest と happy-dom で行う。
- ビルドの道具は Node.js 24 と pnpm 12 に版を固定する。pnpm は Corepack で入れ、`packageManager` のハッシュで確かめる。
- 依存は `pnpm-lock.yaml` から `pnpm install --frozen-lockfile` で入れる。`pnpm-workspace.yaml` に `strictDepBuilds: true` と `minimumReleaseAge: 1440` を明示し、依存のビルドスクリプトを走らせず、公開から 1 日たたない版を断る。
- CI は SPA を秘密を持たない別のジョブで 1 回ビルドし、`dist` を 3 つの OS のジョブに渡す。

設計は §5.2（A7）、§5.3（T21）、§5.4、§15.2、§21、0.96 にある。

## Consequences

- 実行時に配る依存は `preact` 1 つで、その先の依存はない。開発の依存は、フレームワークなしより 1 つ多いだけである。
- 状態を保ったままのホットリロードはない。編集するとページを読み直す。
- フックの関数は、描いたときの値を持つ。W0 の計測のスクリプトが、入力と送信を同じタスクで続けたとき、Preact だけが古い入力の値で検索した。人の操作では起きないが、送信のときはフォームの要素から値を読む（§15.2）。
- pnpm は依存を `node_modules/.pnpm/<名前>@<版>_<ハッシュ>/node_modules/<名前>/` に置くので、npm よりパスが長い。長いパスを有効にしていない Windows で、約 125 文字のフォルダーに置くと、`vitest/package.json` のパスが 260 文字に届き、Vitest が `ERR_PACKAGE_IMPORT_NOT_DEFINED` で起動しなかった。短いフォルダーでは通った。M5 で、CI と手元のパスを確かめる。
- 開発する人は Node.js 24 を入れ、Corepack で pnpm を使う。`build.ps1` は版が違えば誤りで止める（M5）。
- Corepack は Node.js 25.0.0 の前までしか同梱されない。Node.js を 25 以降に上げるときは、pnpm の入れ方を決め直す。
- 悪い依存の版が守りを通り抜けたときは、管理者のブラウザで動くことを受け入れた（§5.4）。
- 選び直すなら、この ADR を置き換える新しい ADR を書く。
