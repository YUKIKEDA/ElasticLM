---
sidebar_position: 10
---

# C API

このページには、ElasticLM の C ライブラリ `libelasticlm` のすべての関数、型、定数を載せる。読者は C と C++ のアプリの開発者だ。このライブラリは、.NET のパッケージと同じライセンスの検証、アクティベーション、リースを C の ABI で提供する。それぞれが何をするかは、先に[概要](../explanation/overview.md)を読むこと。各関数のプロトタイプはこのページにある。すべての引数の完全なリファレンスは `elasticlm.h` のコメントで、使っているライブラリに合うのはアーカイブに入っているヘッダーだ。`main` ブランチのヘッダーは <https://github.com/YUKIKEDA/ElasticLM/blob/main/include/elasticlm.h> にある。ライブラリを使う最初のプログラムを手順に沿って作るには、[ライセンスを発行して C のアプリで確かめる](../tutorials/verify-a-license-in-c.md)に従う。

## ライブラリの入手とリンク

リリースでは、[GitHub の Releases ページ](https://github.com/YUKIKEDA/ElasticLM/releases)にプラットフォームごとのアーカイブを 1 つずつ公開する。Windows は `libelasticlm-<version>-<RID>.zip`、Linux と macOS は `libelasticlm-<version>-<RID>.tar.gz` だ。`<RID>` は `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`、`osx-x64` のどれかだ。ファイルはフォルダーに入っておらず、アーカイブの最上位にある。その一覧は下の表にある。名前が `-symbols` で終わるアーカイブには、デバッグシンボルだけが入っている。ダウンロードしたアーカイブの確かめ方は [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md) に書いてある。

- NativeAOT のライブラリなので、アンロードできない。`FreeLibrary` や `dlclose` を呼ばないこと。
- .NET のランタイムを内部に持っている。アプリが実行時に必要とするのは、ライブラリ自身のほかには下の表にあるものだけだ。
- アプリは、下の表のようにビルド時にライブラリをリンクしてもよいし、実行時に読み込んでもよい。`ELM_FUNCTION` は、各関数を宣言し、その関数へのポインター型も宣言する。ポインター型の名前は、関数名に `_fn` を付けたもの（`elm_verifier_create_fn`）だ。このポインター型は、`LoadLibrary` と `GetProcAddress`、または `dlopen` と `dlsym` でライブラリを読み込み、各関数をその `_fn` 型で呼ぶプログラムのためにある。`LoadLibrary` にフルパスを渡すとそのファイルを読み込み、名前だけを渡すと Windows が DLL の検索順序で探す。

表で使う用語は次のとおり。

- インポートライブラリ（Windows）: アプリから DLL の関数への呼び出しを解決するために、リンカーが読む小さなファイル。ここでは `elasticlm.lib`。コードは入っておらず、コードは `elasticlm.dll` のほうにある。
- 実行時の検索パス（rpath）: リンカーがアプリに書き込むフォルダーの一覧。実行時に、システムのローダーがそこで共有ライブラリを探す。`$ORIGIN`（Linux）と `@executable_path`（macOS）は、アプリの実行ファイルがあるフォルダーを表す。
- SONAME（Linux）: 共有ライブラリが持てる名前。ライブラリがこれを持っていると、リンカーは、渡されたファイル名の代わりにこの名前をアプリに記録する。
- install name（macOS）: ライブラリが持つ名前。リンカーがこれをアプリに記録し、ローダーはこの名前でライブラリを探す。頭に `@rpath/` があると、ローダーはアプリの実行時の検索パスのフォルダーを 1 つずつ探す。
- Authenticode の署名、Developer ID の署名: Windows と Apple のプラットフォームで、発行元がファイルに付けるコード署名。誰が発行したかを示す。
- ad-hoc の署名: 証明書のない macOS のコード署名。発行元を示さない。

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| アーカイブのファイル | `elasticlm.dll`、インポートライブラリ `elasticlm.lib`、`elasticlm.h`、`LICENSE` | `libelasticlm.so`、`elasticlm.h`、`LICENSE` | `libelasticlm.dylib`、`elasticlm.h`、`LICENSE` |
| ビルド時のリンク | `elasticlm.lib` にリンクする。Windows では `ELM_API` が `__declspec(dllimport)` なので、ヘッダーの関数を呼ぶには、リンク時にインポートライブラリが要る。 | `-L <folder> -lelasticlm` | `-L <folder> -lelasticlm` |
| アプリのアーキテクチャー | アプリは、アーカイブのアーキテクチャー向けにビルドする。`win-x64` は x64、`win-arm64` は ARM64。MSVC では、`win-x64` には Visual Studio の x64 Native Tools Command Prompt、`win-arm64` には ARM64 のマシンの ARM64 Native Tools Command Prompt がこれに当たる。既定の Developer Command Prompt は x86 向けにビルドする。そのときリンカーは LNK4272（library machine type 'x64' conflicts with target machine type 'x86'）を警告し、リンクは失敗する。 | アプリは、アーカイブのアーキテクチャー向けにビルドする。`linux-musl-x64` は、Alpine などの musl のシステムでリンクする。`linux-arm64` には、arm64 のツールチェーンかクロスのツールチェーンが要る。 | `osx-x64` は `-arch x86_64`、`osx-arm64` は `-arch arm64`。 |
| アプリに記録されるライブラリの名前 | インポートライブラリから取った `elasticlm.dll`。 | ライブラリには `SONAME` がないので、アプリにはリンカーに渡した名前が記録される。`-lelasticlm` なら `libelasticlm.so`、ファイルへのパスならそのパスで、アプリは実行時にそのパスを探す。パスではなく `-lelasticlm` でリンクすること。 | install name の `@rpath/libelasticlm.dylib`。 |
| 実行時にライブラリを探す場所 | Windows の DLL の検索順序。この順序はアプリ自身のフォルダーから始まる。Windows には実行時の検索パスがない。 | `LD_LIBRARY_PATH`、実行時の検索パス、システムのライブラリのフォルダーの順。`-Wl,-rpath,'$ORIGIN'` は、アプリ自身のフォルダーを実行時の検索パスに加える。 | 実行時の検索パス。アプリ自身のフォルダーなら `-Wl,-rpath,@executable_path`。PowerShell ではオプション全体を単引用符で囲み、`'-Wl,-rpath,@executable_path'` と書く。囲まないと、PowerShell はカンマを一覧の区切りとして読み、その行は構文エラーになる。 |
| `$ORIGIN` の書き方 | — | シェルでは `'$ORIGIN'`、Makefile では `$$ORIGIN`。PowerShell ではオプション全体を単引用符で囲み、`'-Wl,-rpath,$ORIGIN'` と書く。 | — |
| 実行時に必要なもの | Windows に含まれる DLL だけ。 | `linux-x64` と `linux-arm64` は glibc 2.28 以降（RHEL 8、Debian 10、Ubuntu 20.04 以降）。`linux-musl-x64` は Alpine などの musl。ライセンスの検証を含む暗号処理と HTTPS は、システムの OpenSSL（`libssl`）を使う。ライブラリは最初に必要になったときに OpenSSL を読み込むので、OpenSSL がインストールされていなければならない。HTTPS には、システムの CA 証明書も必要だ。ICU は要らない。 | macOS 12.0 以降と、macOS に含まれるライブラリとフレームワークだけ。 |
| コード署名 | Authenticode の署名はない。 | — | Developer ID の署名はなく、公証も受けていない。`osx-arm64` のライブラリにあるのは、リンカーが付ける ad-hoc の署名だけ。Web ブラウザーはダウンロードしたファイルに quarantine 属性を付け、そのようなアーカイブから `tar` が展開したファイルにも、すべて同じ属性が付く。`gh release download` と `curl` は付けない。[SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md) にあるとおり、属性の付いたものは、属性を外すまで Gatekeeper が拒否する。 |

## すべての関数に共通の規則

次の規則は `elasticlm.h` の先頭に書いてあり、関数自身のコメントが例外を示していない限り、すべての関数に当てはまる。

### 文字列

- 文字列は、渡すものも返るものも、UTF-8 で NUL で終わる。
- 渡す文字列は、有効な UTF-8 でなければならない。NUL を除いて、PEM は 16 KiB まで、それ以外の文字列は 1 KiB までだ。これより長い文字列や、有効な UTF-8 でない文字列は `ELM_INVALID_ARGUMENT` になる。
- ライセンスのメタデータのキーと値は NUL を含むことがあるので、`elm_license_meta_at` と `elm_license_meta_get` はその長さもバイト数で返す。

### バイトバッファー

文字列でない入力が 2 つある。`elm_verifier_verify` のライセンスファイルと、`elm_activation_import_response` のサービスのレスポンスだ。どちらもポインターとバイト数の長さ（`const uint8_t*` と `size_t`）で渡し、NUL は要らない。ライブラリが読むのは呼び出しのあいだだけだ。文字列の 16 KiB と 1 KiB の上限は当てはまらず、次の上限がある。

| 関数 | バッファー | 上限 | 上限を超えたとき |
| --- | --- | --- | --- |
| `elm_verifier_verify` | `file`、`len` | 65,536 バイト | `ELM_MALFORMED` |
| `elm_activation_import_response` | `response`、`len` | 262,144 バイト。バイト列は有効な UTF-8 でなければならず、そうでなければ `ELM_INVALID_ARGUMENT` | `ELM_MALFORMED` |

`NULL` のポインターは `ELM_INVALID_ARGUMENT` になる。バッファーより大きい `len` の動作は未定義だ。

### エラー

- ヘッダーの「Functions that return a number」の下にある関数は、エラー番号を返す。`0`（`ELM_OK`）が成功で、それ以外の番号は[エラーコード](error-codes.md)のどれかだ。
- これらの関数は、始まるときにこのスレッドのエラーメッセージを空にし、失敗したときに書き込む。`elm_last_error_message` はそのメッセージを返す。成功のあとは空で、同じスレッドで次にこうした関数を呼ぶまで有効だ。
- 失敗したときは、必ず出力を `NULL` と `0` にする。`struct_size` が正しい `elm_feature` は、ほかのメンバーが `NULL` と `0` になる。例外の出力が 2 つある。`retry_after_seconds` には失敗したときに待つ時間が入り、`elm_activation_id` の `*needed` にはバッファーが小さすぎるときに必要なサイズが入る。
- `retry_after_seconds` を出力に持つのは、`elm_activation_activate`、`elm_activation_start_trial`、`elm_activation_refresh`、`elm_client_acquire_lease`、`elm_client_borrow_lease` だけだ。値は `0` か、サーバーが待つ時間を付けて拒否したとき（`ELM_SERVER_RECOVERING` や `ELM_RATE_LIMITED` など）はその時間を秒に切り上げたものになる。ライブラリが自分で待つことはない。
- 出力の引数が `NULL` なら `ELM_INVALID_ARGUMENT` になる。例外は、`NULL` でもよい `retry_after_seconds` と、サイズが `0` なら `NULL` でもよい `elm_activation_id` のバッファーだ。
- 「Functions that return a value」の下にある関数は失敗しない。ハンドルが `NULL` なら、下の表にあるとおり `NULL`、`0`、`-1` のどれかを返し、エラーメッセージは変えない。
- .NET の例外がライブラリの外に出ることはない。内部の失敗は `ELM_INTERNAL_ERROR`（`99`）になる。

### 構造体

- 渡す構造体は、すべて `struct_size` で始まる。構造体の `sizeof` を設定すること。それ以外の値は `ELM_INVALID_ARGUMENT` になる。
- 真偽値のメンバーは `int32_t` で、`0` か `1` でなければならない。
- 0 で埋めた構造体は、既定値を表す。

### 所有権と解放

- ライブラリが返したものは、`free` ではなく、対応する `_free` または `_destroy` の関数で解放する。ハンドルごとの関数は[ハンドルの型](#ハンドルの型)の表にあり、オフラインのファイルのテキストは `elm_string_free` で解放する。これらの関数は `NULL` を無視する。`elm_activation_destroy` と `elm_client_destroy` は、ハンドルの処理を止めてからハンドルを解放する。
- ハンドルを 2 回解放したときや、解放したあとに使ったときの動作は未定義だ。別の型のハンドルは、ライブラリが見分けられる場合は拒否する。
- ライセンスが返す文字列は、`elm_license_free` まで有効だ。リースの ID と機能は、`elm_lease_free` まで有効だ。
- オフラインのファイルのテキストは `elm_string_free` で解放する。この関数は、解放の前にテキストを消去する。アクティベーションのリクエストにはライセンスキーが入っているからだ。

### スレッドとコールバック

- ハンドルは、複数のスレッドから同時に使ってよい。ライセンスは複数のスレッドから読んでよいが、ほかのスレッドが読んでいるあいだに解放してはいけない。
- アクティベーションのオンラインの呼び出しと、クライアントでのリースの取得、借用、返却、借用中のリースの一覧は、終わるまで待ち、キャンセルできない。それぞれの最長の待ち時間はヘッダーに書いてある。
- ある操作が起こすコールバックは、その関数が戻る前に戻り終わっている。ハンドルの `_destroy` が戻ったあとに始まるコールバックはない。コールバックの中では、同じハンドルのブロックする関数は `ELM_INVALID_STATE` を返し、その `_destroy` は何もしない。各コールバックの中で呼べる関数は[コールバック](#コールバック)に載せている。
- ほかのスレッドが同じハンドルの関数の中にいるあいだに `elm_activation_destroy` や `elm_client_destroy` を呼んだときの動作は未定義だ。

### ABI バージョン

`ELM_ABI_VERSION` は `1` だ。新しいバージョンでは、関数を追加したり、構造体の末尾にメンバーを追加したりすることがある。

## 関数

各表はヘッダーの順に関数を並べ、その下のブロックにヘッダーが宣言するとおりのプロトタイプを示す。「エラー番号」は、その関数が[エラー](#エラー)の規則に従って `int32_t` を返すことを表す。

### インストールと検証

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_installation_open` | エラー番号 | 製品についてのこのインストールの鍵を開くか、`create` が `1` で鍵がなければ作り、マシンフィンガープリントを読む。0 で埋めた `elm_installation_options` のように `create` が `0` なら、鍵がないと `ELM_INSTALLATION_KEY_UNREADABLE` になる。OS の ID を読めないときは、フィンガープリントの一覧が空になり、フィンガープリントを持つライセンスは `ELM_FINGERPRINT_UNAVAILABLE` で失敗する。 |
| `elm_installation_thumbprint` | `const char*`。`NULL` には `NULL` | インストール鍵のサムプリント。 |
| `elm_verifier_create` | エラー番号 | ベンダーのルート鍵、失効した鍵 ID、時刻巻き戻し対策から検証器を作る。インストールは、呼び出しの直後に解放してよい。 |
| `elm_verifier_verify` | エラー番号 | `elm_requirement` に照らしてライセンスファイルを検証し、新しい `elm_license` を返す。65,536 バイトより大きいファイルは `ELM_MALFORMED` になる。ライブラリはファイルの中身を何も保持しない。 |

```c
int32_t elm_installation_open(const elm_installation_options* options, elm_installation** out);
const char* elm_installation_thumbprint(const elm_installation* installation);
int32_t elm_verifier_create(const elm_verifier_options* options, elm_verifier** out);
int32_t elm_verifier_verify(elm_verifier* verifier, const uint8_t* file, size_t len, const elm_requirement* requirement, elm_license** out);
```

### ライセンスを読む関数

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_license_expires_at` | エラー番号 | 有効期限を UNIX 秒で返す。ライセンスに有効期限がなければ、`*has_value` が `0` になる。 |
| `elm_license_feature` | エラー番号 | インデックス（`0` から個数 - 1）の機能を `elm_feature` に書き込む。 |
| `elm_license_meta_at` | エラー番号 | インデックスにあるメタデータの組を、ライセンスでの順に、キーと値の長さと一緒に返す。 |
| `elm_license_meta_get` | エラー番号 | メタデータのキーの値とその長さを返す。キーがなければ `*value` が `NULL` になる（それでも `ELM_OK`）。 |
| `elm_license_id` | `const char*`。`NULL` には `NULL` | ライセンス ID。 |
| `elm_license_product` | `const char*`。`NULL` には `NULL` | 製品 ID。 |
| `elm_license_kind` | `int32_t`: `ELM_KIND_*` の値。`NULL` には `-1` | ライセンスの種類。 |
| `elm_license_issued_at` | `int64_t`: UNIX 秒。`NULL` には `0` | ライセンスを発行した時刻。 |
| `elm_license_grace_period_seconds` | `int64_t`。`NULL` には `0` | 猶予期間の秒数。 |
| `elm_license_in_grace_period` | `int32_t`: `1` か `0`。`NULL` には `-1` | ライセンスが有効期限を過ぎ、猶予期間の中にあるかどうか。 |
| `elm_license_feature_count` | `size_t`。`NULL` には `0` | 機能の数。 |
| `elm_license_meta_count` | `size_t`。`NULL` には `0` | メタデータの組の数。 |

```c
int32_t elm_license_expires_at(const elm_license* license, int32_t* has_value, int64_t* value);
int32_t elm_license_feature(const elm_license* license, size_t index, elm_feature* out);
int32_t elm_license_meta_at(const elm_license* license, size_t index, const char** key, size_t* key_len, const char** value, size_t* value_len);
int32_t elm_license_meta_get(const elm_license* license, const char* key, const char** value, size_t* value_len);
const char* elm_license_id(const elm_license* license);
const char* elm_license_product(const elm_license* license);
int32_t elm_license_kind(const elm_license* license);
int64_t elm_license_issued_at(const elm_license* license);
int64_t elm_license_grace_period_seconds(const elm_license* license);
int32_t elm_license_in_grace_period(const elm_license* license);
size_t elm_license_feature_count(const elm_license* license);
size_t elm_license_meta_count(const elm_license* license);
```

### アクティベーション

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_activation_create` | エラー番号 | SaaS 型のアクティベーションサービスでの、このインストールのアクティベーションのハンドルを作る。インストール鍵があれば読み、保存してあるアクティベーションを読んで、そのトークンの更新を予定する。サービスには接続せず、鍵も作らない。鍵を作るのは、最初の `elm_activation_activate`、`elm_activation_start_trial`、`elm_activation_create_activation_request` だ。鍵があるのに読めないときは `ELM_INSTALLATION_KEY_UNREADABLE` になる。 |
| `elm_activation_activate` | エラー番号 | ライセンスキーでオンラインのアクティベーションをし、新しい `elm_license` を返す。 |
| `elm_activation_start_trial` | エラー番号 | オンラインでトライアルを始め、新しい `elm_license` を返す。 |
| `elm_activation_refresh` | エラー番号 | 保存してあるアクティベーションのトークンを今すぐオンラインで更新し、新しい `elm_license` を返す。`service_url` がない、保存してあるアクティベーションがない、10 秒たってもこのデバイスの別のプロセスがアクティベーションのロックを持っている、16 件のリクエストにレスポンスがない（待機中のオフラインのリクエストは 1 件と数える）、同じアクティベーションの状態のコールバックの中で呼んだ、のどれかなら、リクエストを送らずに `ELM_INVALID_STATE` を返す。トークンの更新の時期がまだ来ていなくても、リクエストを送る。 |
| `elm_activation_deactivate` | エラー番号 | このインストールのアクティベーションをオンラインで解除する。 |
| `elm_activation_check` | エラー番号 | 保存してあるアクティベーションを、種類と、省略できる機能とバージョンについてオフラインで確かめ、新しい `elm_license` を返す。 |
| `elm_activation_create_activation_request` | エラー番号 | ライセンスキーについて、オフラインのアクティベーションのリクエストのテキストを作る。待機中のリクエストを置き換えたときは `*replaced` が `1`、そうでなければ `0` になる。 |
| `elm_activation_create_refresh_request` | エラー番号 | オフラインのトークンの更新のリクエストのテキストを作る。待機中のリクエストを置き換えたときは `*replaced` が `1`、そうでなければ `0` になる。 |
| `elm_activation_create_deactivation_proof` | エラー番号 | オフラインのアクティベーション解除のプルーフのテキストを作る。`*replaced` は常に `0` だ。 |
| `elm_activation_forget_deactivation_proof` | エラー番号 | 保存してあるアクティベーション解除のプルーフを破棄する。 |
| `elm_activation_import_response` | エラー番号 | 待機中のオフラインのリクエストへのサービスのレスポンスを保存し、新しい `elm_license` を返す。262,144 バイトを超えるレスポンスは `ELM_MALFORMED` になる。 |
| `elm_activation_id` | エラー番号 | 現在のアクティベーション ID（なければ空）を、呼び出し側のバッファーにコピーする。`*needed` には、NUL を含めたサイズが入る。バッファーが小さすぎるときも入り、そのとき関数は `ELM_INVALID_ARGUMENT` を返し、バッファーには触れない。 |
| `elm_activation_state` | `int32_t`: `ELM_STATE_*` の値。`NULL` には `-1` | アクティベーションの状態。 |

```c
int32_t elm_activation_create(const elm_activation_options* options, elm_activation** out);
int32_t elm_activation_activate(elm_activation* activation, const char* license_key, elm_license** out, int64_t* retry_after_seconds);
int32_t elm_activation_start_trial(elm_activation* activation, elm_license** out, int64_t* retry_after_seconds);
int32_t elm_activation_refresh(elm_activation* activation, elm_license** out, int64_t* retry_after_seconds);
int32_t elm_activation_deactivate(elm_activation* activation);
int32_t elm_activation_check(elm_activation* activation, const char* feature_id, const char* feature_version, int32_t kind, elm_license** out);
int32_t elm_activation_create_activation_request(elm_activation* activation, const char* license_key, int32_t renew, char** out, int32_t* replaced);
int32_t elm_activation_create_refresh_request(elm_activation* activation, int32_t renew, char** out, int32_t* replaced);
int32_t elm_activation_create_deactivation_proof(elm_activation* activation, char** out, int32_t* replaced);
int32_t elm_activation_forget_deactivation_proof(elm_activation* activation);
int32_t elm_activation_import_response(elm_activation* activation, const uint8_t* response, size_t len, elm_license** out);
int32_t elm_activation_id(const elm_activation* activation, char* buffer, size_t size, size_t* needed);
int32_t elm_activation_state(const elm_activation* activation);
```

オンラインとオフラインのアクティベーション、トークンの更新、トライアルは[アクティベーション](../explanation/activation.md)で説明している。

### クライアントとリース

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_client_create` | エラー番号 | オンプレミスのライセンスサーバーのリースのクライアントを作る。`license_key` を設定したときは、SaaS 型のサービスのリースのクライアントになる。インストール鍵を開くか、なければ作り（鍵があるのに読めないときは `ELM_INSTALLATION_KEY_UNREADABLE`）、マシンフィンガープリントを読む。サーバーには接続しない。 |
| `elm_client_acquire_lease` | エラー番号 | 機能のリースを 1 つ以上のシートについて取得し、新しい `elm_lease` を返して、バックグラウンドでリースを更新する。 |
| `elm_client_borrow_lease` | エラー番号 | 1 秒から 3162240000 秒のあいだリースを借用し、新しい `elm_lease` を返す。借用は更新せず、有効期限を過ぎても `ELM_LEASE_HELD` のままだ。`elm_lease_expires_at` を現在の時刻と比べること。 |
| `elm_client_release_lease` | エラー番号 | このクライアントが作ったリースを返す。別のクライアントのリースは `ELM_INVALID_ARGUMENT` になる。借用でないリースは、返却に失敗しても、そのあと `ELM_LEASE_RELEASED` になる。サーバーが引き取らなかった借用は、状態、ファイル、それまでの `elm_lease_last_error` がそのまま残り、コールバックも呼ばれない。すでに `ELM_LEASE_LOST` か `ELM_LEASE_RELEASED` のリースは、もう一度は送らない（`ELM_OK`）。 |
| `elm_client_get_borrowed_leases` | エラー番号 | 保存してある借用のうち、まだ有効なものを、新しいハンドルの配列で返す。配列は `elm_lease_list_free` で解放する。 |
| `elm_client_reset_server_pin` | エラー番号 | オンプレミスのサーバーのピンを削除する。`server_url` は `http` か `https` の絶対 URL でなければならず、そうでなければ `ELM_INVALID_ARGUMENT` になる。ピンを削除したときは `*deleted` が `1` になる。ライセンスキーを持つクライアントにはピンがないので、その場合は常に `0` だ。 |
| `elm_lease_id` | エラー番号 | リース ID を返す。 |
| `elm_lease_feature_id` | エラー番号 | リースの機能 ID を返す。 |
| `elm_lease_expires_at` | エラー番号 | 現在のリーストークンの有効期限を UNIX 秒で返す。リースの更新で変わる。 |
| `elm_lease_seats` | `int32_t`。`NULL` には `-1` | シートの数。 |
| `elm_lease_is_borrow` | `int32_t`: `1` か `0`。`NULL` には `-1` | リースが借用かどうか。 |
| `elm_lease_state` | `int32_t`: `ELM_LEASE_*` の値。`NULL` には `-1` | リースの状態。 |
| `elm_lease_last_error` | `int32_t`: エラー番号。なければ `0`。`NULL` には `-1` | 最後に失敗したリースの更新かリースの再登録のエラー、またはサーバーが確認しなかった返却のエラー。回復したあとも残る。サーバーが引き取らなかった借用は、それまでのエラーを保つ。 |

```c
int32_t elm_client_create(const elm_client_options* options, elm_client** out);
int32_t elm_client_acquire_lease(elm_client* client, const char* feature_id, const char* feature_version, int32_t count, elm_lease** out, int64_t* retry_after_seconds);
int32_t elm_client_borrow_lease(elm_client* client, const char* feature_id, int64_t period_seconds, const char* feature_version, int32_t count, elm_lease** out, int64_t* retry_after_seconds);
int32_t elm_client_release_lease(elm_client* client, elm_lease* lease);
int32_t elm_client_get_borrowed_leases(elm_client* client, elm_lease*** out, size_t* count);
int32_t elm_client_reset_server_pin(elm_client* client, const char* server_url, int32_t* deleted);
int32_t elm_lease_id(const elm_lease* lease, const char** out);
int32_t elm_lease_feature_id(const elm_lease* lease, const char** out);
int32_t elm_lease_expires_at(const elm_lease* lease, int64_t* out);
int32_t elm_lease_seats(const elm_lease* lease);
int32_t elm_lease_is_borrow(const elm_lease* lease);
int32_t elm_lease_state(const elm_lease* lease);
int32_t elm_lease_last_error(const elm_lease* lease);
```

リースのハンドルは、`elm_client_destroy` のあとも読める。リース、リースの更新、借用、サーバーのピンは[フローティングライセンス](../explanation/floating-licenses.md)で説明している。

### エラーとバージョン

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_last_error_message` | `const char*` | このスレッドで最後に呼んだ、エラー番号を返す関数のメッセージ。成功のあとは空。`ELM_INVALID_ARGUMENT` のメッセージは、C の引数かメンバーの名前か、値の何が正しくないかを示し、.NET の引数の名前は付かない。 |
| `elm_version` | `const char*` | ライブラリのバージョン。プロセスが終わるまで有効。 |

```c
const char* elm_last_error_message(void);
const char* elm_version(void);
```

### 解放

| 関数 | 戻り値 | すること |
| --- | --- | --- |
| `elm_installation_free` | `void` | インストールを解放する。 |
| `elm_verifier_destroy` | `void` | 検証器を解放する。 |
| `elm_license_free` | `void` | ライセンスとその文字列を解放する。 |
| `elm_activation_destroy` | `void` | 実行中のコールバックを待ち（時間の上限なし）、次に自動のトークンの更新を止めて最大 5 秒待ち、ハンドルを解放する。アクティベーションはデバイスに保存されたまま残る。 |
| `elm_lease_free` | `void` | リースを返さずに、リースのハンドルを解放する。 |
| `elm_lease_list_free` | `void` | `elm_client_get_borrowed_leases` の配列とそのハンドルを、同じ個数を渡して解放する。先に要素を解放したときの動作は未定義だ。 |
| `elm_client_destroy` | `void` | 実行中のコールバックと進行中の操作を待ち（時間の上限なし）、次に借用でないリースを 5 秒以内に返して、ハンドルを解放する。借用はそのまま残る。 |
| `elm_string_free` | `void` | ほかの関数が、この関数で解放するよう示している文字列を、消去してから解放する。 |

```c
void elm_installation_free(elm_installation* installation);
void elm_verifier_destroy(elm_verifier* verifier);
void elm_license_free(elm_license* license);
void elm_activation_destroy(elm_activation* activation);
void elm_lease_free(elm_lease* lease);
void elm_lease_list_free(elm_lease** leases, size_t count);
void elm_client_destroy(elm_client* client);
void elm_string_free(char* text);
```

## ハンドルの型

ハンドルは、どれもライブラリが確保する不透明な構造体だ。

| 型 | 何か | 作る関数 | 解放する関数 |
| --- | --- | --- | --- |
| `elm_installation` | 1 つの製品についての、このインストールの鍵とマシンフィンガープリント。 | `elm_installation_open` | `elm_installation_free` |
| `elm_verifier` | ライセンスの検証器。ベンダーのルート鍵と時刻巻き戻し対策を持つ。 | `elm_verifier_create` | `elm_verifier_destroy` |
| `elm_license` | 検証済みのライセンス。返す文字列は、どれも解放するまで有効だ。 | `elm_verifier_verify` と、ライセンスを返すアクティベーションの関数 | `elm_license_free` |
| `elm_activation` | SaaS 型のサービスでの、このインストールのアクティベーション。 | `elm_activation_create` | `elm_activation_destroy` |
| `elm_client` | オンプレミスのライセンスサーバー、または SaaS 型のサービスのリースのクライアント。 | `elm_client_create` | `elm_client_destroy` |
| `elm_lease` | リース。状態、有効期限、最後のエラーはリースの更新で変わるので、読むたびに取得する。解放してもリースは返らない。 | `elm_client_acquire_lease`、`elm_client_borrow_lease`、`elm_client_get_borrowed_leases` | `elm_lease_free`。一覧なら `elm_lease_list_free` |

## コールバック

| 型 | シグネチャー | 呼ばれるとき |
| --- | --- | --- |
| `elm_state_callback` | `void (*)(void* user, int32_t previous, int32_t current, int32_t error, const char* message)` | アクティベーションの状態が変わったとき。`previous` と `current` は `ELM_STATE_*` の値で、エラーがなければ `error` は `0`、`message` は空だ。 |
| `elm_lease_callback` | `void (*)(void* user, const char* lease_id, int32_t previous, int32_t current, int32_t error, const char* message)` | リースの状態が変わったとき。`previous` と `current` は `ELM_LEASE_*` の値だ。`lease_id` は `elm_lease_id` と比べる。リースの更新が失敗し続けると、`ELM_LEASE_RETRYING` から再び `ELM_LEASE_RETRYING` への変化を、そのエラーと一緒に知らせる。取得と、エラーのないリースの更新では、何も知らせない。 |

- 文字列は、呼び出しのあいだだけ有効だ。
- `elm_state_callback` は、状態を変えた操作を終えるスレッドで呼ばれる。その呼び出しの中にいる自分のスレッドのこともあり、自動のトークンの更新ならライブラリのスレッドになる。トークンの更新の時期がすでに来ていると、`elm_activation_create` が戻る前、つまりまだハンドルを持っていないうちに呼ばれることがある。この中では、`elm_activation_state`、`elm_activation_id`、`elm_activation_check` を呼んでよい。
- `elm_lease_callback` は、ライブラリのスレッドで呼ばれる。返却のリクエストを終えたスレッドか、自動のリースの更新のスレッドだ。この中では、`elm_lease_*` の関数、`elm_lease_free`、`elm_lease_list_free`、`elm_client_reset_server_pin` を呼んでよい。
- C++ の例外や `longjmp` でコールバックを抜けないこと。ライブラリを呼んでいるスレッドが持っているロックも取らないこと。コールバックはライブラリのスレッドを占有することがあるので、すぐに戻ること。コールバックが始めたスレッドは、ほかのスレッドと同じように扱う。

## 構造体

メンバーはヘッダーの順に並べる。どの構造体も `uint32_t struct_size` で始まり、その構造体の `sizeof` を設定する。

### `elm_installation_options`

`elm_installation_open` に渡す。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_installation_options)`。 |
| `product_id` | `const char*` | 必須。製品 ID。 |
| `scope` | `int32_t` | `ELM_SCOPE_USER` か `ELM_SCOPE_MACHINE`。 |
| `directory` | `const char*` | `NULL` ならスコープの[既定のフォルダー](../explanation/devices-and-proofs.md#鍵の保存場所)。または自分で決めたフォルダー。 |
| `create` | `int32_t` | `1`: 鍵がなければ作る。`0`（既定）: 既存の鍵を開くだけ。鍵がなければ `ELM_INSTALLATION_KEY_UNREADABLE` になる。 |

### `elm_verifier_options`

`elm_verifier_create` に渡す。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_verifier_options)`。 |
| `root_keys_pem` | `const char* const*` | ベンダーの PEM 形式のルート公開鍵。1 個から 16 個。 |
| `root_key_count` | `size_t` | ルート鍵の数。 |
| `revoked_key_ids` | `const char* const*` | ベンダーが失効させた鍵 ID。0 個から 1024 個。 |
| `revoked_key_count` | `size_t` | 失効した鍵 ID の数。 |
| `installation` | `const elm_installation*` | `NULL`、またはその鍵で時刻巻き戻し対策の状態に署名するインストール。検証器は自分用のコピーを持つ。 |
| `clock_guard_path` | `const char*` | 時刻巻き戻し対策の状態ファイル。`NULL` なら、インストールのフォルダーの `clock`。インストールがなければ状態を持たない。インストールがないと状態ファイルに署名しないので、そのファイルに書き込める人は誰でも対策をリセットできる。 |
| `clock_skew_seconds` | `int32_t` | `0` なら既定の 300。それ以外は 1 から 3600。 |
| `reject_trial_as_standard` | `int32_t` | `0`（既定）: 標準の要件でトライアルのライセンスも受け付ける。`1`: 受け付けない。 |

時刻巻き戻し対策は[時刻巻き戻しの検出と、その限界](../explanation/license-files-and-verification.md#時刻巻き戻しの検出とその限界)で説明している。

### `elm_requirement`

`elm_verifier_verify` に渡す。アプリがライセンスに求める条件だ。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_requirement)`。 |
| `product_id` | `const char*` | 必須。製品 ID。 |
| `kind` | `int32_t` | `ELM_KIND_*` の値。 |
| `feature_id` | `const char*` | 機能を求めないときは `NULL`。 |
| `feature_version` | `const char*` | `NULL`、または `major.minor[.patch]`。 |
| `installation` | `const elm_installation*` | ノードロックでないライセンスなら `NULL`。同じ製品のものでなければならず（そうでなければ `ELM_INVALID_ARGUMENT`）、呼び出しのあいだだけ使う。 |

### `elm_feature`

`elm_license_feature` が書き込む。`struct_size` は呼び出し側が設定し、ほかのメンバーはライブラリが書き込む。文字列は `elm_license_free` まで有効だ。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_feature)`。呼び出し側が設定する。 |
| `id` | `const char*` | 機能 ID。 |
| `version_range` | `const char*` | ライセンスで認められたバージョンの範囲。機能に範囲がなければ `NULL`。 |
| `has_seats` | `int32_t` | 機能にシート数があれば `1`。 |
| `seats` | `int32_t` | シート数。または `0`。 |
| `has_expires_at` | `int32_t` | 機能が自分の有効期限を持っていれば `1`。 |
| `expires_at` | `int64_t` | 機能の有効期限（UNIX 秒）。または `0`。 |

### `elm_activation_options`

`elm_activation_create` に渡す。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_activation_options)`。 |
| `product_id` | `const char*` | 必須。製品 ID。 |
| `service_url` | `const char*` | `elm-service` の URL。`https`、またはループバックアドレスなら `http`。`NULL` ならオフラインのファイルだけを使う。 |
| `root_keys_pem` | `const char* const*` | ベンダーの PEM 形式のルート公開鍵。1 個から 16 個。 |
| `root_key_count` | `size_t` | ルート鍵の数。 |
| `revoked_key_ids` | `const char* const*` | ベンダーが失効させた鍵 ID。0 個から 1024 個。 |
| `revoked_key_count` | `size_t` | 失効した鍵 ID の数。 |
| `scope` | `int32_t` | `ELM_SCOPE_USER` か `ELM_SCOPE_MACHINE`。 |
| `directory` | `const char*` | `NULL` ならスコープの既定のフォルダー。または自分で決めたフォルダー。 |
| `send_client_info` | `int32_t` | `1`: ホスト名とユーザー名を送る。 |
| `app_version` | `const char*` | `NULL`、またはアプリのバージョン（64 バイトまで）。 |
| `clock_skew_seconds` | `int32_t` | `0` なら既定の 300。それ以外は 1 から 3600。 |
| `reject_trial_as_standard` | `int32_t` | `0`（既定）: `ELM_KIND_STANDARD` を指定した `elm_activation_check` でトライアルのライセンスも受け付ける。`1`: 受け付けない。 |
| `on_state` | `elm_state_callback` | `NULL`、または状態のコールバック。 |
| `user` | `void*` | `on_state` に渡す。`elm_activation_create` の前に準備し、`elm_activation_destroy` が戻るまで有効にしておくこと。 |

### `elm_client_options`

`elm_client_create` に渡す。

| メンバー | 型 | 意味 |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_client_options)`。 |
| `product_id` | `const char*` | 必須。製品 ID。 |
| `server_url` | `const char*` | 必須。オンプレミスのサーバーなら `https`。ライセンスキーを使うときは `https`、またはループバックアドレスなら `http`。 |
| `license_key` | `const char*` | `NULL` ならオンプレミスのサーバー。それ以外は、SaaS 型のサービスのリースに使うライセンスキー。 |
| `root_keys_pem` | `const char* const*` | ベンダーの PEM 形式のルート公開鍵。1 個から 16 個。 |
| `root_key_count` | `size_t` | ルート鍵の数。 |
| `revoked_key_ids` | `const char* const*` | ベンダーが失効させた鍵 ID。0 個から 1024 個。 |
| `revoked_key_count` | `size_t` | 失効した鍵 ID の数。 |
| `expected_server_license_id` | `const char*` | `NULL`、またはピンとして記録するサーバーライセンス。`license_key` とは一緒に使えない。 |
| `scope` | `int32_t` | `ELM_SCOPE_USER` か `ELM_SCOPE_MACHINE`。 |
| `directory` | `const char*` | `NULL` ならスコープの既定のフォルダー。または自分で決めたフォルダー。 |
| `send_client_info` | `int32_t` | `1`: ホスト名とユーザー名を送る。 |
| `app_version` | `const char*` | `NULL`、またはアプリのバージョン（64 バイトまで）。 |
| `clock_skew_seconds` | `int32_t` | `0` なら既定の 300。それ以外は 1 から 3600。 |
| `reject_trial_as_standard` | `int32_t` | `0` か `1`。効果はない。クライアントが検証するのはサーバーライセンスとリーストークンだけで、標準の要件で検証することはない。 |
| `on_lease` | `elm_lease_callback` | `NULL`、またはリースのコールバック。 |
| `user` | `void*` | `on_lease` に渡す。`elm_client_destroy` が戻るまで有効にしておくこと。 |

## 定数

### エラー番号

`elasticlm.h` は、エラーコードごとに、コードの名前と番号のマクロを定義している（`ELM_NO_SEATS` は `40`）。すべてのコードとその意味は[エラーコード](error-codes.md)のページに載せている。

| 番号 | グループ |
| --- | --- |
| `0` から `24` | [成功、検証、署名付きのリクエスト](error-codes.md#成功検証署名付きのリクエスト0-から-24) |
| `30` から `44` | [ライセンス、アクティベーション、リース](error-codes.md#ライセンスアクティベーションリース30-から-44) |
| `50` から `56` | [認証、制限、管理](error-codes.md#認証制限管理50-から-56) |
| `90` と `99` | [ネットワークのエラーと内部エラー](error-codes.md#ネットワークのエラーと内部エラー90-と-99) |

### ライセンスの種類

| マクロ | 値 | 種類 |
| --- | --- | --- |
| `ELM_KIND_STANDARD` | `0` | 標準のライセンス。 |
| `ELM_KIND_TRIAL` | `1` | トライアルのライセンス。 |
| `ELM_KIND_SERVER` | `2` | `elm-server` 用のサーバーライセンス。 |

### 鍵のスコープ

インストール鍵を保存する場所。

| マクロ | 値 | スコープ |
| --- | --- | --- |
| `ELM_SCOPE_USER` | `0` | ユーザースコープ。 |
| `ELM_SCOPE_MACHINE` | `1` | マシンスコープ。 |

### アクティベーションの状態

| マクロ | 値 | 状態 |
| --- | --- | --- |
| `ELM_STATE_NONE` | `0` | 保存してあるアクティベーションがない。 |
| `ELM_STATE_ACTIVE` | `1` | アクティベーションを保存してあり、トークンの更新をしていれば、最後の更新は成功した。 |
| `ELM_STATE_REFRESH_FAILING` | `2` | トークンの更新が失敗している。保存してあるライセンスファイルを、期限が切れるまで使う。 |
| `ELM_STATE_REFUSED` | `3` | サービスがライセンスを拒否した（失効、一時停止、期限切れ）。保存してあるファイルを、期限が切れるまで使う。ファイルの有効期限と猶予期間が過ぎるまでは `elm_activation_check` が成功し続けるので、どうするかはアプリが決める。 |
| `ELM_STATE_CLONE_DETECTED` | `4` | サービスがこのインストールの別のコピーを見つけ、自動のトークンの更新が止まる。保存してあるファイルを、期限が切れるまで使う。ファイルの有効期限と猶予期間が過ぎるまでは `elm_activation_check` が成功し続けるので、どうするかはアプリが決める。 |

### リースの状態

| マクロ | 値 | 状態 |
| --- | --- | --- |
| `ELM_LEASE_HELD` | `0` | リースは有効だ。借用でないリースは、バックグラウンドで更新する。 |
| `ELM_LEASE_RETRYING` | `1` | 最後のリースの更新が失敗した。クライアントは、リースの有効期限まで再試行する。 |
| `ELM_LEASE_RECLAIMING` | `2` | 再起動のあと、サーバーがリースを知らなかった。クライアントがリースを再登録している。 |
| `ELM_LEASE_LOST` | `3` | 借用でないリースが、リースの更新に成功する前に期限切れになった、リースの再登録を拒否された、SaaS 型のサービスがリースをもう知らなかった、またはクローンを検出した。借用は、期限切れで `ELM_LEASE_LOST` になることはなく、`ELM_LEASE_HELD` のままだ。 |
| `ELM_LEASE_RELEASED` | `4` | アプリがリースを返したか、`elm_client_destroy` が返した。`elm_client_destroy` は借用を返さない。借用が返却済みになるのは、サーバーが引き取ったときか、サーバーがその借用を知らなかったときだけだ。 |

### その他のマクロ

| マクロ | 値 | 意味 |
| --- | --- | --- |
| `ELM_ABI_VERSION` | `1` | ヘッダーが宣言する ABI のバージョン。 |
| `ELM_API` | Windows では `__declspec(dllimport)`、それ以外では空 | 宣言した各関数に付ける。 |
| `ELM_FUNCTION` | `ELM_FUNCTION(ret, name, args)` | 関数 `name` とポインター型 `name_fn` を宣言する。 |
