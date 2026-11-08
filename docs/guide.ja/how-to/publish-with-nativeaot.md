---
sidebar_position: 15
---

# NativeAOT で発行する

このガイドは、ElasticLM を使う .NET のアプリを出荷し、NativeAOT で 1 つのネイティブ実行ファイルとして発行したい開発者に向けている。NativeAOT の実行ファイルは起動が速く、ユーザーのマシンに .NET ランタイムが要らない。ElasticLM のパッケージは、このための作りになっている。リフレクションも実行時に生成するコードも使わないので、これらを参照するアプリを NativeAOT で発行しても、パッケージからの警告は出ない。このガイドは、[ライセンスを発行して .NET のアプリで確かめる](../tutorials/verify-a-license-offline.md)を終えた状態から始める。`app` にあるそのアプリ、ルート公開鍵 `root.pub.pem`、ライセンスファイル `first.elmlic` がある状態だ。

<!-- steps:after page=docs/guide/tutorials/verify-a-license-offline.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで打つ。NativeAOT は、プラットフォームのネイティブのツールでアプリをコンパイルする。そのツールは .NET SDK には含まれていない。

- **Windows**: Visual Studio 2022 以降かその Build Tools と、ワークロード「Desktop development with C++」。発行には、Visual Studio Installer のフォルダーが `PATH` にあることも要る。発行のときに動く Visual Studio のスクリプトが、そこにある `vswhere.exe` を名前だけで呼ぶからだ。PowerShell では、発行するターミナルで `$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"` と打つ。
- **Linux**: `clang` と `zlib` の開発用ファイル。Ubuntu なら `sudo apt-get install clang zlib1g-dev` などで入れる。
- **macOS**: Xcode のコマンドラインツール。`xcode-select --install` で入れる。

`CONTRIBUTING.md` の [Build from source](https://github.com/YUKIKEDA/ElasticLM/blob/main/CONTRIBUTING.md#build-from-source) にも、同じツールと同じ `PATH` の設定を挙げている。NativeAOT は、それを動かしたシステム向けにビルドする。Windows 向けには Windows で、Linux 向けには Linux で、macOS 向けには macOS で発行する。

## NativeAOT を有効にする

プロパティ `PublishAot` で、アプリの NativeAOT を有効にする。ここでは、チュートリアルのプロジェクトファイルをそのままにしておくために、新しいファイル `app/Directory.Build.props` にこれを書く。このファイルは、フォルダー `app` にあるすべてのプロジェクトが読み込む。

<!-- steps:file path=app/Directory.Build.props -->
```xml
<Project>
  <PropertyGroup>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
</Project>
```

自分のアプリでは、代わりにプロジェクトファイルの `PropertyGroup` に `<PublishAot>true</PublishAot>` を足してもよい。`PublishAot` を設定すると、NativeAOT でコンパイルできないコードを報告するアナライザーがビルドでも動く。そのため、発行するときだけでなく、作業しているあいだもその警告を見られる。

## アプリを発行する

フォルダー `publish` にアプリを発行する。`-p:TreatWarningsAsErrors=true` は、ビルドと NativeAOT のコンパイラーのすべての警告をエラーにする。そのため、警告が 1 つもなかったときだけ、コマンドはエラーなしで終わる。2 つ目のプロパティは、既知の脆弱性があるパッケージについての NuGet の警告（NU1901 から NU1904）を警告のままにする。そうしないと、.NET SDK のパッケージに新しいアドバイザリーが出たとき、SDK を更新するまで発行が止まってしまう。それでも、これらの警告には目を通す。

<!-- steps:run -->
```shell
dotnet publish app -c Release -p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904 -o publish
```

`;` は警告のコードを区切る文字で、`-p` の値の中では `%3B` と書く。そのまま `;` を書くと、シェルでコマンドが終わるか、プロパティが終わり、`dotnet` は `MSB1006` で止まる。

最初の発行では、自分のシステム用の NativeAOT のコンパイラーのパッケージをダウンロードし、そのあとアプリと、アプリが使う .NET の部分をネイティブコードにコンパイルする。速いネットワークでは、約 15 秒かかった。最後に `app -> …/publish/` のような行を表示する。

`-r` を付けなければ、`dotnet publish` は、それを動かしたシステム向けにビルドする。たとえば 64 ビットの Windows では `win-x64`、Apple シリコンの Mac では `osx-arm64` だ。同じシステムの別のプロセッサー向けにビルドするには、`-r win-arm64` のように、そのランタイム識別子を `-r` で指定する。ランタイム識別子は [.NET RID catalog](https://learn.microsoft.com/dotnet/core/rid-catalog) に一覧がある。それには、Visual Studio の ARM64 のビルドツールや Linux のクロスツールチェーンのように、そのプロセッサー用のネイティブのツールも要る。どれが要るかは、.NET のドキュメントの [Cross-compilation](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile) に書いてある。

発行が、エラーに変わった警告で止まったときは、メッセージに、NativeAOT でコンパイルできないコードか、その出どころのパッケージが書いてある。ElasticLM のパッケージからの警告はバグなので、リポジトリの [issues](https://github.com/YUKIKEDA/ElasticLM/issues) に報告する。Windows で `'vswhere.exe' is not recognized as an internal or external command` というエラーのあとに `link.exe` についての `error MSB3073` が続くときは、Visual Studio Installer のフォルダーが `PATH` にないか、C++ のワークロードが入っていない。

## 発行したアプリを動かす

フォルダー `publish` には、1 つの実行ファイル `app`（Windows では `app.exe`）になったアプリと、その隣にデバッグシンボル（Windows では `app.pdb`、Linux では `app.dbg`、macOS では `app.dSYM`）が入っている。チュートリアルと同じ引数で動かす。

<!-- steps:run -->
```shell
publish/app root.pub.pem first.elmlic
```

`dotnet run` のときと同じ行を表示する。

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

この実行ファイルには .NET ランタイムが要らない。デバッグシンボルを除いて、同じシステムとプロセッサーのマシンにコピーすれば、そこで動く。Linux では、そのマシンに、発行したマシンと同じかそれより新しいバージョンの C ライブラリ（glibc）も要る。アプリが `InvariantGlobalization` を設定していなければ、グローバリゼーションのための ICU ライブラリも要る。要件は [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) に挙げられている。出荷する各リリースのシンボルは取っておき、クラッシュレポートのスタックトレースを読めるようにする。

出荷する前に、チュートリアルにあるとおり、`root.pub.pem` をファイルから読む代わりに、ルート公開鍵をコードに埋め込む。鍵のテキストは文字列の定数にでき、NativeAOT はそれを実行ファイルに含める。

## 関連するページ

- [概要](../explanation/overview.md): パッケージに含まれるものと、対応するシステム。
- [.NET API](../reference/dotnet-api.md): アプリが使うすべての型。
- .NET のドキュメントの [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/): NativeAOT の制限と、発行のオプション。
