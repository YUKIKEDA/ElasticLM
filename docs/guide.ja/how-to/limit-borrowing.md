---
sidebar_position: 11
---

# 借用を制限する

このガイドは、`elm-server` を動かしていて、借用をサーバーライセンスが認めるより短くしたい、または通常のリースのためにシートを空けておきたい顧客の管理者に向けている。借用は、期限が切れるまでアプリがネットワークの外に持ち出すリースだ。詳しくは[借用](../explanation/floating-licenses.md#借用)で説明している。このガイドは、[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)を終えた状態から始める。借用を認めるサーバーライセンスを持つ `elm-server` と、そのチュートリアルのアプリがある状態だ。

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、サーバーの設定ファイル `server.json`、そのデータフォルダー `server-data`、ルート公開鍵 `root.pub.pem`、`app` にあるチュートリアルのアプリとそのデータフォルダー `client-data` を使う。

サーバーが止まっていたら、[サーバーを起動する](../tutorials/use-a-floating-license.md#サーバーを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。アプリは借用の前に通常のリースを受けるので、2 つのシートのうち 1 つが空いていなければならない。この 1 時間のうちにアプリを 2 回動かしたなら、両方のシートが借用されていて、アプリは `ELM_NO_SEATS` を表示する。借用してから 1 時間後に借用の期限が切れるまで待つか、チュートリアルを最初からやり直す。

例では、チュートリアルの値を使う。製品 `acme-cad`、シート 2 つの機能 `pro`、最長 1 日の借用を認めるサーバーライセンスだ。自分の値に置き換える。

## 上限を知る

借用を制限するのは、次の 2 者だ。

| 上限 | 決める人 | 場所 |
| --- | --- | --- |
| 借用の最長期間 | ベンダー | サーバーライセンス。`elm license issue` の `--borrow-days` で、既定の `0` では借用ができない。 |
| 1 つのインストールが借用を早期返却できる回数 | ベンダー | サーバーライセンス。`--borrow-returns` で、既定は 30 日に 2 回。 |
| 借用の最長期間をさらに短くする | 自分 | サーバーの設定の `borrow.maxDuration`。サーバーは、これとサーバーライセンスの上限のうち短いほうを使う。 |
| 通常のリースのために残すシート | 自分 | サーバーの設定の `borrow.reserve` で、既定は 0。借用したあとに空いているシートがこれより少なくなる借用は拒否される。 |

借用の制限は、ベンダーが認めるより厳しくはできるが、緩くはできない。サーバーライセンスの上限より長い `borrow.maxDuration` は効かず、サーバーライセンスが認めるより長い借用はアプリが拒否する。ベンダーの上限を変えるには、[サーバーライセンスを更新する](renew-a-server-license.md)のとおりに、ベンダーが新しいサーバーライセンスを発行する。

## 借用の最長期間を設定する

`server.json` に `borrow` を足し、`maxDuration` を `d.hh:mm:ss` の形で指定する。`00:30:00` は 30 分で、`7.00:00:00` なら 7 日になる。ほかの設定はそのままにする。チュートリアルの設定なら、ファイルは次のようになる。

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "borrow": {
    "maxDuration": "00:30:00"
  }
}
```

サーバーは設定を起動時にしか読まないので、再起動する。2 つ目のターミナルで Ctrl+C を押してサーバーを止め、もう一度起動する。

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

サーバーが前に貸し出した借用は、そのまま残る。上限が効くのは新しい借用だ。期間でない値や、範囲（1 秒から 100 年）を外れた値を指定すると、サーバーは `error:` で始まる行を表示し、終了コード 2 で止まる。

## 上限より長い借用が拒否されることを確かめる

チュートリアルのアプリをもう一度動かす。アプリは 1 時間の借用を求める。これは新しい上限より長い。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

サーバーは起動したばかりなので、チュートリアルと同じように、アプリはまず 1 分ほど待つ。そのあと通常のリースを受けて返却し、借用は拒否される。アプリは終了コード 1 で終わり、次のように表示する。

<!-- steps:expect -->
```text
leased: pro
ELM_BORROW_NOT_ALLOWED: The borrow period is longer than allowed.
```

30 分以下の借用を求めるアプリなら、借用できる。サーバーが何を保持しているかを尋ねる。

<!-- steps:run -->
```shell
elm server status --data server-data
```

<!-- steps:expect -->
```text
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

1 つの借用はチュートリアルのもので、サーバーが再起動をまたいで保持していた。拒否された借用はシートを使っていない。

## 通常のリースのためにシートを残す

シートが借用されないように残しておくには、`borrow` に `reserve` を設定する。たとえば、各機能でシートを 1 つ残すなら次のようにする。

```json
  "borrow": {
    "maxDuration": "00:30:00",
    "reserve": 1
  }
```

再起動すると、借用したあとに空いているシートが `reserve` より少なくなる借用は、`ELM_NO_SEATS: No seats are free to borrow for the feature.` で拒否される。ここでのようにシートが 2 つあって 1 つが借用されているなら、新しい借用はすべて拒否され、空いているシートは通常のリースのために残る。`reserve` は、機能ごとに別々にシートを数える。サーバーは先に借用の期間を確かめるので、1 時間を求めるチュートリアルのアプリは、上の `maxDuration` があると、やはり `ELM_BORROW_NOT_ALLOWED` を受け取る。`ELM_NO_SEATS` を見るには、`maxDuration` を指定せずに `reserve` を設定する。

上限をなくすには、`server.json` から `borrow` を外し、サーバーを再起動する。

## 関連するページ

- [借用](../explanation/floating-licenses.md#借用): 上限、早期返却、再起動をまたぐ借用。
- `elm-server` の設定の [`borrow`](../reference/server-configuration.md#borrow)。
- [`ELM_BORROW_NOT_ALLOWED` と `ELM_NO_SEATS`](../reference/error-codes.md) とほかのエラーコード。
