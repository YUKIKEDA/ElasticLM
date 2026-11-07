---
sidebar_position: 4
---

# ライセンスを発行して C のアプリで確かめる

このチュートリアルでは、ベンダーの鍵を作り、その鍵でライセンスを発行する。そして、そのライセンスを手元のマシンで、ネットワークを使わずに確かめる小さな C のプログラムを書く。このプログラムは、ElasticLM の C 向けのライブラリ `libelasticlm` をリンクする。ElasticLM を初めて使う人のうち、ターミナルを使えて、C のプログラムをビルドできる人に向けている。同じことを .NET で行うのが[ライセンスを発行して .NET のアプリで確かめる](verify-a-license-offline.md)だ。

## 始める前に

C のコンパイラーが要る。

- Windows では、「C++ によるデスクトップ開発」のワークロードを入れた Visual Studio か Build Tools for Visual Studio。
- Linux では `cc`（gcc か clang）。
- macOS では Xcode Command Line Tools（`xcode-select --install`）。

ダウンロードしたファイルを確かめるには、[GitHub CLI](https://cli.github.com/)（`gh`）も要る。このチュートリアルで `gh` を使うのは、この確認のときだけだ。

Windows では、スタートメニューから「x64 Native Tools Command Prompt」を開き、そこで `powershell` と打つ。このページのコマンドは、すべてその PowerShell で打つ。このプロンプトは、64 ビットの x64 のプログラムを作るようにコンパイラー `cl` を設定し、PowerShell はその設定を引き継ぐ。「Developer Command Prompt」は使わない。32 ビットの x86 のプログラムを作るので、x64 のライブラリをリンクできない。Windows の ARM64 のマシンでは、代わりに「ARM64 Native Tools Command Prompt」を開き、下のアーカイブは `win-arm64` のものを取る。Linux と macOS では、どのターミナルでもよい。

このチュートリアル用に、空のフォルダーを作る。そのフォルダーでターミナルを開き、このページのコマンドはすべてそこで打つ。Native Tools のプロンプトは別のフォルダーで開くので、Windows では、まずその PowerShell で `cd` とチュートリアルのフォルダーのパスを打つ。

`elm` ツールとライブラリは、GitHub のプレリリースからダウンロードする。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)を開き、「Pre-release」のラベルが付いたリリースのうち、いちばん新しいものを使う。GitHub が「Latest」のラベルを付けるのはプレリリースでないリリースだけなので、そのラベルを目安にしない。

リリースの Assets から、次の 3 つのファイルをチュートリアルのフォルダーに保存する。`<RID>` は自分のシステムを表し、`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`（Apple シリコン搭載の Mac）、`osx-x64`（Intel プロセッサー搭載の Mac）のどれかだ。

- アーカイブ `elm-<version>-<RID>`。Windows 用は `.zip` ファイル、それ以外は `.tar.gz` ファイルになっている。
- アーカイブ `libelasticlm-<version>-<RID>`。ファイルの形式（`.zip` か `.tar.gz`）は `elm` のアーカイブと同じ。
- ファイルのハッシュの一覧 `SHA256SUMS`。

名前が `-symbols` で終わるアーカイブにはデバッグシンボルしか入っていないので、そうでないほうを取る。

展開したり使ったりする前に、チュートリアルのフォルダーにある 2 つのアーカイブを、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめる。この節には、プログラムに必要な Linux と macOS のバージョンも書いてある。

macOS では、続けて、この節のとおりに 2 つのアーカイブから quarantine 属性を外す。`xattr -d com.apple.quarantine elm-<version>-<RID>.tar.gz` と打ち、`libelasticlm` のアーカイブにも同じように打つ。Web ブラウザーはダウンロードしたファイルにこの属性を付け、そのようなアーカイブから `tar` が展開したファイルにも、すべて同じ属性が付く。属性が付いていて公証（notarization）を受けていないファイルを、Gatekeeper は開かない。`gh release download` と `curl` は属性を付けない。そのときは `xattr` が `No such xattr: com.apple.quarantine` と表示し、外すものはない。

`elm` のアーカイブを `tools` という名前のフォルダーに展開する。Windows の PowerShell では `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` と打つ。Linux と macOS では `mkdir -p tools` と打ってから `tar -xzf elm-<version>-<RID>.tar.gz -C tools` と打つ。これで `tools` には、鍵を作り、ライセンスを発行するコマンドラインツール `elm`（Windows では `elm.exe`）と、`LICENSE` が入る。

<!-- steps:prebuilt name=elm -->

`elm` と打つだけで動くように、このターミナルで `tools` フォルダーを `PATH` に足す。Windows の PowerShell では `$env:PATH = "$PWD/tools;$env:PATH"` と打ち、Bash か zsh では `export PATH="$PWD/tools:$PATH"` と打つ。

`libelasticlm` のアーカイブを `app` という名前のフォルダーに展開する。プログラムを書いてビルドするのも、このフォルダーだ。ファイルはアーカイブの最上位にあるので、そのまま展開すると、ファイルはチュートリアルのフォルダーに直接出てくる。Windows の PowerShell では `Expand-Archive libelasticlm-<version>-<RID>.zip -DestinationPath app` と打つ。Linux と macOS では `mkdir -p app` と打ってから `tar -xzf libelasticlm-<version>-<RID>.tar.gz -C app` と打つ。これで `app` には、ライブラリ（Windows では `elasticlm.dll` とインポートライブラリ `elasticlm.lib`、Linux では `libelasticlm.so`、macOS では `libelasticlm.dylib`）、ヘッダー `elasticlm.h`、`LICENSE` が入る。

<!-- steps:prebuilt name=libelasticlm -->

## 鍵を作る

鍵は 2 つ要る。アプリが信頼するルート鍵と、ライセンスに署名する署名鍵だ。署名鍵が署名したものをアプリが受け入れるように、ルート鍵が署名鍵を証明する。

`elm` は、それぞれの秘密鍵をパスフレーズで暗号化したファイルに保存する。鍵を作るときは、12 文字以上の新しいパスフレーズを尋ね、もう一度入力させる。あとのコマンドがその鍵を使うときは、そのパスフレーズをまた尋ねる。入力中は何も表示されない。このチュートリアルでは、2 つの鍵に同じパスフレーズを使う。環境変数 `ELM_KEY_PASSPHRASE` が設定されていれば、`elm` はパスフレーズをそこから取り、尋ねない。そのため、下の出力にあるパスフレーズを尋ねる行は表示されない。このターミナルで設定するには、PowerShell では `$env:ELM_KEY_PASSPHRASE = '<passphrase>'`、Bash か zsh では `export ELM_KEY_PASSPHRASE='<passphrase>'` と打つ。これはスクリプトや、誰も入力できないターミナルのためのものだ。実際の運用では、スクリプトにパスフレーズを書いておかない。詳しくは、`elm` のリファレンスの[パスフレーズ](../reference/elm-cli.md#パスフレーズ)を参照。

下の出力にある鍵 ID、トークン ID、日時は架空の値だ。手元のマシンでは毎回違う値になる。

ルート鍵を作る。

<!-- steps:run -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
```

`elm` は次のように表示する。

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
private key (encrypted): root.key.pem
public key: root.pub.pem
```

`root.key.pem` が秘密鍵で、`root.pub.pem` がその公開鍵だ。鍵 ID は、公開鍵から計算した短い名前だ。

署名鍵を作る。

<!-- steps:run -->
```shell
elm key signing create --out signing.key.pem --public-out signing.pub.pem
```

`elm` は次のように表示する。

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
private key (encrypted): signing.key.pem
public key: signing.pub.pem
```

ライセンスの署名に使うために、ルート鍵で署名鍵を証明する。

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
```

`elm` は次のように表示する。

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` が鍵証明書だ。有効期間は 1 年で、そのあいだ署名鍵は新しいライセンスに署名してよい。この有効期間は、署名鍵が署名したライセンスがいつまで有効かを制限しない。この点は、[鍵証明書](../explanation/trust-chain.md#鍵証明書)の「有効期間と発行時刻」で説明している。

## ライセンスを発行する

製品 `acme-cad` に機能 `pro` を付けたライセンスを発行する。期限は UTC の 2099 年 12 月 31 日 00:00 だ。

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
```

`elm` は次のように表示する。

```text
Passphrase for 'signing.key.pem':
license file: first.elmlic
license ID: first-license
token ID: FAKEjti000000000000001
```

`first.elmlic` がライセンスファイルだ。署名したライセンスと、署名鍵の鍵証明書が入っている。`--trust` を付けると、`elm` は新しいライセンスをルート公開鍵で検証し、検証に通ったときだけファイルを書く。このライセンスはデバイスに紐付けていないので、どのマシンでも有効だ。

## アプリを作る

次のコードで、ファイル `app/app.c` を作る。

<!-- sample: samples/VerifyOfflineC/app.c -->
<!-- steps:file path=app/app.c -->
```c
/*
 * Verifies a license file offline and prints whether it is valid.
 *
 *   app <root-public-key.pem> <license.elmlic>
 *
 * A real app embeds the root public key instead of reading it from a file.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "elasticlm.h"

#define PRODUCT_ID "acme-cad"
#define FEATURE_ID "pro"

/* Reads a whole file and ends it with a NUL, so that a PEM file can be used as a string; NULL when it cannot. */
static char* read_file(const char* path, size_t* length)
{
    FILE* file = fopen(path, "rb");
    char* bytes = NULL;
    long size;
    if (file == NULL) {
        return NULL;
    }
    if (fseek(file, 0, SEEK_END) == 0 && (size = ftell(file)) >= 0 && fseek(file, 0, SEEK_SET) == 0) {
        bytes = (char*)malloc((size_t)size + 1);
        if (bytes != NULL && fread(bytes, 1, (size_t)size, file) == (size_t)size) {
            bytes[size] = '\0';
            *length = (size_t)size;
        } else {
            free(bytes);
            bytes = NULL;
        }
    }
    fclose(file);
    return bytes;
}

int main(int argc, char** argv)
{
    elm_verifier_options options = {0};
    elm_requirement requirement = {0};
    elm_verifier* verifier = NULL;
    elm_license* license = NULL;
    const char* root_keys[1];
    char* root_key;
    char* file;
    size_t root_key_length;
    size_t file_length;
    int32_t error;

    if (argc != 3) {
        fprintf(stderr, "usage: app <root-public-key.pem> <license.elmlic>\n");
        return 2;
    }
    root_key = read_file(argv[1], &root_key_length);
    if (root_key == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[1]);
        return 2;
    }
    file = read_file(argv[2], &file_length);
    if (file == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[2]);
        free(root_key);
        return 2;
    }

    root_keys[0] = root_key;
    options.struct_size = sizeof options;
    options.root_keys_pem = root_keys;
    options.root_key_count = 1;
    error = elm_verifier_create(&options, &verifier);
    if (error != ELM_OK) {
        /* The first argument is not a public key in PEM. */
        fprintf(stderr, "error: %s\n", elm_last_error_message());
        free(file);
        free(root_key);
        return 2;
    }

    requirement.struct_size = sizeof requirement;
    requirement.product_id = PRODUCT_ID;
    requirement.kind = ELM_KIND_STANDARD;
    requirement.feature_id = FEATURE_ID;
    error = elm_verifier_verify(verifier, (const uint8_t*)file, file_length, &requirement, &license);
    if (error == ELM_OK) {
        printf("valid: license %s, product %s, feature %s\n", elm_license_id(license), elm_license_product(license), FEATURE_ID);
    } else {
        printf("invalid: %d: %s\n", (int)error, elm_last_error_message());
    }

    elm_license_free(license);
    elm_verifier_destroy(verifier);
    free(file);
    free(root_key);
    return error == ELM_OK ? 0 : 1;
}
```

プログラムは、ルート公開鍵から検証器を作る。信頼する鍵はこの 1 つだけだ。`struct_size` は、プログラムが構造体のどの版でビルドされたかをライブラリに伝える。0 のままのフィールドは既定の値になる。既定では、失効した鍵 ID の一覧は空で、時刻巻き戻し対策は使わない。そのため、このプログラムは時計を戻されても気づかない（戻されたことに気づける時刻巻き戻し対策は、[時刻巻き戻しの検出と、その限界](../explanation/license-files-and-verification.md#時刻巻き戻しの検出とその限界)で説明している）。次に、ライセンスファイルを検証する。署名、鍵証明書、有効期限、製品 `acme-cad`、機能 `pro` を確かめる。要件の `installation` は `NULL` で、これは、プログラムがデバイスに紐付けていないライセンスだけを使うことを示す。最後に、有効なら `valid` とライセンスの ID を、そうでなければ `invalid` とエラーの番号と理由を表示する。

このプログラムは、チュートリアルを短くするためにルート公開鍵をファイルから読む。実際のアプリは、`root.pub.pem` のテキストを文字列の定数などとしてコードに埋め込む。

## アプリをビルドする

ライブラリは、ビルドするプログラムと同じ `app` にある。プログラムは起動するとき、そこでライブラリを探す。Windows は DLL をまずプログラムのフォルダーで探し、Linux と macOS は、下の `-Wl,-rpath,...` のオプションでそこを探すようになる。

Windows では、`cl` でビルドし、インポートライブラリ `elasticlm.lib` をリンクする。`cl` は、オブジェクトファイル `app.obj` もチュートリアルのフォルダーに作る。

<!-- steps:run os=windows -->
```shell
cl /nologo /std:c11 app/app.c app/elasticlm.lib /Fe:app/app.exe
```

Linux では、`-L app -lelasticlm` で `app` の `libelasticlm.so` をリンクし、`$ORIGIN` はプログラムのフォルダーを表す。単引用符は外さない。Bash でも zsh でも PowerShell でも、単引用符があればシェルは `$ORIGIN` を変数として読まない。

<!-- steps:run os=linux -->
```shell
cc -std=c11 -o app/app app/app.c -L app -lelasticlm '-Wl,-rpath,$ORIGIN'
```

macOS では、まず `app` に quarantine 属性の付いたファイルが残っていないようにし、それからビルドする。アーカイブから属性を外す前にライブラリを展開した場合などに、属性が残る。`xattr -dr` はフォルダー全体をたどり、属性のないファイルがあってもエラーなしで終わる。`@executable_path` はプログラムのフォルダーを表す。ここでも単引用符は外さない。PowerShell はカンマを一覧の区切りとして読むからだ。`cc` は使っている Mac に合わせてビルドするので、アーカイブの `<RID>` はその Mac に合っていなければならない。

<!-- steps:run os=macos -->
```shell
xattr -dr com.apple.quarantine app
cc -std=c11 -o app/app app/app.c -L app -lelasticlm '-Wl,-rpath,@executable_path'
```

システムごとのファイル、オプション、探す場所と、ライブラリが実行時に必要とするもの（Linux では glibc 2.28 以降（`linux-musl-x64` では musl）、OpenSSL、CA の証明書）は、[C API](../reference/c-api.md) に挙げてある。

## アプリを動かす

ルート公開鍵とライセンスファイルを渡して、プログラムを動かす。この行はどのシステムでも同じで、Windows では PowerShell が `app/app.exe` を動かす。

<!-- steps:run -->
```shell
app/app root.pub.pem first.elmlic
```

プログラムは次のように表示する。

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

代わりに `invalid:` と表示されたときは、そのあとに続く番号で、何が失敗したかがわかる。`elasticlm.h` は、それぞれの番号を、エラーコードの名前を付けて定義している。

| 番号 | コード | このチュートリアルで考えられる原因 | 対処 |
| --- | --- | --- | --- |
| `3` | `ELM_UNTRUSTED_KEY` | 1 つ目の引数が、`signing.pub.pem` のような、`root.pub.pem` 以外の公開鍵になっている。 | `root.pub.pem` を渡す。 |
| `7` | `ELM_WRONG_PRODUCT` | ライセンスを、`acme-cad` 以外の `--product` で発行した。 | `--product acme-cad` を付けてライセンスを発行し直す。`first.elmlic` を上書きするために `--force` も付ける。 |
| `11` | `ELM_FEATURE_NOT_LICENSED` | ライセンスを `--feature pro` なしで発行した。 | `--feature pro` と `--force` を付けてライセンスを発行し直す。 |
| `2` | `ELM_MALFORMED` | 2 つ目の引数が、`signing.cert` のような、ライセンスファイルでないものになっている。 | `first.elmlic` を渡す。 |

プログラムが `error: A root key is not a P-256 public key in PEM.` で始まる行を表示したときは、1 つ目の引数が公開鍵のファイルではない。2 つの引数の順序を確かめる。`error: cannot read` の行には、読めなかったファイルの名前が出る。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げてある。

プログラムがまったく起動しないときは、プログラムがライブラリを見つけられなかった。`app` の中、プログラムの隣に `elasticlm.dll`、`libelasticlm.so`、`libelasticlm.dylib` のどれかがあるかを確かめる。Windows では、そのとき PowerShell は何も表示せず、`$LASTEXITCODE` は `-1073741515`（`0xC0000135`。DLL が見つからない）になる。

## 作ったもの

ルート鍵と署名鍵を作り、署名鍵を証明し、ライセンスを発行して、`libelasticlm` をリンクした C のプログラムでオフラインで検証した。実際の製品では、`root.key.pem` をオフラインに置く。この鍵は署名鍵を証明するためだけに使い、これを持つ人は誰でも、アプリが受け入れるライセンスを作れるからだ。

さらに知るには、次のページを読む。

- [C API](../reference/c-api.md): `libelasticlm` のすべての関数と、システムごとのファイル、オプション、探す場所。
- [ライセンスファイルと検証](../explanation/license-files-and-verification.md): ライセンスファイルに何が入っているかと、プログラムが行ったすべての確認。
- [トラストチェーン](../explanation/trust-chain.md): ルート鍵と署名鍵、その鍵を安全に保つ方法。
