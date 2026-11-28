---
sidebar_position: 23
---

# elm-server を OS のサービスとして動かす

このガイドは、`elm-server` を常時動かす顧客の管理者向けだ。OS のサービスとして動かし、マシンの起動と一緒に起動し、失敗したときは起動し直すようにする。対象は、systemd の Linux、launchd の macOS、Windows だ。[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)のようにサーバーを準備する方法と、[elm-service でサーバーライセンスをアクティベーションする](activate-a-server-license-with-elm-service.md)のようにサーバーがライセンスを受け取る方法を、先に知っておくこと。

<!-- steps:e2e tests=SystemdServiceTests.Systemd_ThePageSteps_RunRestartAndStopTheServer,SystemdServiceTests.Systemd_HardenedUnit_LendsAndTakesBackALease,LaunchdServiceTests.Launchd_ThePageSteps_RunRestartAndStopTheServer,WindowsServiceTests.NewService_ThePageSteps_StartActivateAndStopTheServer -->

## 始める前に

次のものが要る。

- そのマシン用の `elm-server` と `elm`。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)のアーカイブ `elm-server-<version>-<RID>` と `elm-<version>-<RID>` から取り出し、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめておく。
- ベンダーのルート公開鍵（下では `root.pub.pem`）と、製品 ID（下では `acme-cad`）。
- ベンダーの `elm-service` で作ったサーバーライセンスのライセンスキーと、そのサービスのアドレス（下では `https://licensing.example.com`）。設定 `service.url` があれば、サーバーはそのあと、そこで自分でライセンスを更新する（[オンラインで更新する](activate-a-server-license-with-elm-service.md#オンラインで更新する)）。
- 管理者のターミナル。Linux と macOS では `sudo`、Windows では「管理者として実行」で開いた PowerShell 7 を使う。

ベンダーがサーバーライセンスのファイルを手作業で発行するなら、`service.url` とアクティベーションの手順を省く。最初の起動が書く行（Windows ではイベントログにある）から、インストール鍵のサムプリントとマシンフィンガープリントをベンダーに送り、送られてきたファイルを `license` のパスに、サービスのアカウントを持ち主にして置く。Linux なら、たとえば `sudo install -m 0600 -o elm-server -g elm-server server.elmlic /var/lib/elm-server/` で置く。

下の各節は、ファイルを次の場所に置く。表のあとの規則を守るなら、別の場所でもよい。

| 置くもの | Linux | macOS | Windows |
| --- | --- | --- | --- |
| `elm-server`、`elm`、設定ファイル、`root.pub.pem` | `/opt/elm-server/`（プログラム）、`/etc/elm-server/`（設定とルート鍵） | `/usr/local/elm-server/` | `C:\Program Files\elm-server\` |
| データフォルダーと、サーバーライセンスのファイル | `/var/lib/elm-server` | `/Library/Application Support/elm-server` | `C:\ProgramData\elm-server` |
| サービスの定義 | `/etc/systemd/system/elm-server.service` | `/Library/LaunchDaemons/elm-server.plist` | サービスコントロールマネージャーに登録する |
| 運用ログ | ジャーナル | `/Library/Logs/elm-server/elm-server.log` | Application のイベントログ |
| アカウント | システムユーザー `elm-server` | ロールアカウント `_elm-server` | LocalSystem |

- **プログラム、設定ファイル、ルート鍵、サービスの定義と、その上のすべてのフォルダーは、root（Windows では管理者）だけが書き込めるようにする。** `elm-server` や `elm` を置き換えられる者は、サービスのアカウントの権限で自分のコードを動かせる。このアカウントは、サーバーのインストール鍵を読み、リースに署名できる。設定やルート鍵を変えられる者は、サーバーが信頼する鍵と、待ち受ける場所を変えられる。サービスの定義を変えられる者は、root（Windows では LocalSystem）の権限でコードを動かせる。
- **データフォルダーを開けるのは、サービスのアカウントのほかは、Linux と macOS では root、Windows では Administrators グループと SYSTEM だけにする。** このフォルダーには、サーバーのインストール鍵と管理者トークンが入っている。ほかの者が変えられるデータフォルダーを、サーバーは受け付けない（[サーバーが受け付けるフォルダー](../reference/server-configuration.md#サーバーが受け付けるフォルダー)）。サーバーライセンスのファイルも、ここに置く。
- **パスは、設定でもサービスの定義でも、すべて絶対パスで書く。** サーバーは相対パスを、自分が動いているフォルダーから読む。そのフォルダーは、サービスマネージャーが決める。
- **サービスの定義に秘密を書かない。** そこには `ELM_` で始まる変数を設定しない。ライセンスキーは、`elm server activate` に聞かれたときに入力する。自分の TLS 証明書を使うなら（[自分の TLS 証明書を使う](use-your-own-tls-certificate.md)）、PEM の証明書と鍵のファイル（`tls.certificate` と `tls.key`）で渡し、鍵のファイルはサービスのアカウントだけが読めるようにする。パスワード付きの PKCS#12 ファイルには `ELM_TLS_PASSWORD` が要るので、ここでは PEM のファイルを使う。
- **クライアントがポートに届くようにする。** サーバーは、すべての IPv4 アドレスのポート 7070 で待ち受ける。マシンでファイアウォールが動いているなら、そのポートへの TCP の接続を受け入れるようにする。

## Linux（systemd）

サーバー用のシステムユーザーを作る。同じ名前のグループを付け、ログインに使うシェルは付けない。

```shell
sudo useradd --system --user-group --shell /usr/sbin/nologin elm-server
```

`elm-server`、`elm`、`root.pub.pem` のあるフォルダーで、`elm-server.json` を作る。

<!-- sample: samples/ElmServerService/linux/elm-server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://0.0.0.0:7070",
  "license": "/var/lib/elm-server/server.elmlic",
  "dataDirectory": "/var/lib/elm-server",
  "service": { "url": "https://licensing.example.com" },
  "trust": { "rootKeys": ["/etc/elm-server/root.pub.pem"] }
}
```

プログラム、設定、ルート鍵を、root だけが書き込める場所に置く。データフォルダーは、サーバーのユーザーを持ち主にして、そのユーザーだけが開けるように作る。

```shell
sudo install -d -m 0755 /opt/elm-server /etc/elm-server
sudo install -m 0755 elm-server elm /opt/elm-server/
sudo install -m 0644 elm-server.json root.pub.pem /etc/elm-server/
sudo install -d -o elm-server -g elm-server -m 0700 /var/lib/elm-server
```

ここから先、`elm-server` と `elm` は root ではなく、サーバーのユーザーで実行する。データフォルダーはそのユーザーのものなので、ほかのユーザー（root も含む）が `elm` のライセンスのコマンドを実行すると、`elm` はそのフォルダーを受け付けない（[elm server](../reference/elm-cli.md#elm-server)）。自分のフォルダーはそのユーザーが開けないことがあるので、これらのコマンドは、`/` のように、そのユーザーが読めるフォルダーで実行する。そこへ移り、ターミナルからサーバーを 1 回起動する。

```shell
cd /
sudo -u elm-server /opt/elm-server/elm-server --config /etc/elm-server/elm-server.json
```

サーバーはデータフォルダーにインストール鍵を作り、まだライセンスがないので、終了コード 1 で止まる。出力される行は `error: The server license '/var/lib/elm-server/server.elmlic' does not exist.` で始まり、その鍵も載っている。サーバーをアクティベーションする。`sudo` はあなたの環境変数を引き継がないので、`elm` がライセンスキーを聞いてくる。そこに貼り付ける。

```shell
sudo -u elm-server /opt/elm-server/elm server activate --product acme-cad --data /var/lib/elm-server --trust /etc/elm-server/root.pub.pem --service https://licensing.example.com --license /var/lib/elm-server/server.elmlic
```

`elm` は、ライセンス ID、シーケンス番号 1、書き込んだパスを表示し、続けて `The server is not running; it reads the license when it starts.` と表示して、終了コード 0 で終わる。ダウンロードしたファイルのあるフォルダーに戻り（`cd -` で、移る前のフォルダーに戻る）、そこでユニット `elm-server.service` を作る。

<!-- sample: samples/ElmServerService/elm-server.service -->
```ini
[Unit]
Description=ElasticLM floating license server (elm-server)

[Service]
Type=exec
User=elm-server
Group=elm-server
ExecStart=/opt/elm-server/elm-server --config /etc/elm-server/elm-server.json
Restart=on-failure
RestartSec=5
TimeoutStopSec=60
NoNewPrivileges=yes
ProtectSystem=strict
ReadWritePaths=/var/lib/elm-server
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
CapabilityBoundingSet=
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
RestrictNamespaces=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM

[Install]
WantedBy=multi-user.target
```

- `Type=exec`: `elm-server` が動き出した時点で、systemd はサービスが起動したとみなす。
- `Restart=on-failure` と `RestartSec=5`: サーバーが 0 以外の終了コードで終わるか、`SIGKILL` などのシグナルで止められると、systemd は 5 秒後にもう一度起動する。`systemctl stop` による停止（`SIGTERM` を送る）は失敗に当たらない。
- `TimeoutStopSec=60`: systemd は、サーバーが止まるのを 60 秒待ち、そのあとで強制終了する（[サーバーを止めて記録を確かめる](#サーバーを止めて記録を確かめる)）。この設定がないと、systemd は既定の時間だけ待つ。その時間はマシンの systemd の設定で決まる（Ubuntu 26.04 では 90 秒）。

`TimeoutStopSec` より後の行は、サーバーの欠陥を突くなどして、サーバーのプロセスの中でコードを動かした者ができることを狭める（各行の説明は [systemd.exec(5)](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html) にある）。効くのは、systemd がこのユニットから起動したプロセスだけだ。`sudo -u elm-server` で起動したものなど、同じユーザーのほかのプロセスには効かない。

- `NoNewPrivileges=yes`、空の `CapabilityBoundingSet=`、`RestrictSUIDSGID=yes`: プロセスは Linux のケーパビリティを 1 つも持たず、set-user-ID のプログラムを実行しても特権を得られない。
- `ProtectSystem=strict` と `ReadWritePaths=/var/lib/elm-server`: プロセスが書き込めるのはデータフォルダーだけになる。ライセンスや利用ログを外に移さないかぎり（下で説明する）、サーバーが書くものはすべてここにある。ほかのファイルは、プロセスからは読み取り専用になる。`ProtectHome=yes`、`PrivateTmp=yes`、`PrivateDevices=yes`: ホームフォルダーは見えず、`/tmp` は自分専用のものになり、ハードウェアのデバイスも見えない。
- `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`: 開けるソケットの種類は、サーバーが使うもの、つまりデータフォルダーにあるローカルの管理用のソケットと、IPv4 と IPv6 の TCP だけになる。
- `ProtectKernelTunables`、`ProtectKernelModules`、`ProtectKernelLogs`、`ProtectControlGroups`、`ProtectClock`、`ProtectHostname`、`RestrictNamespaces`、`RestrictRealtime`、`LockPersonality`、`MemoryDenyWriteExecute`: カーネルの設定を変えること、カーネルモジュールを読み込むこと、カーネルのログを読むこと、コントロールグループを変えること、時計やホスト名を設定すること、名前空間を作ること、リアルタイムのスケジューリングを使うこと、実行ドメインを変えること、書き込みと実行の両方ができるメモリを作ることができなくなる。
- `SystemCallArchitectures=native`、`SystemCallFilter=@system-service`、`SystemCallErrorNumber=EPERM`: systemd がサービス向けに挙げる組（`systemd-analyze syscall-filter @system-service` で表示できる）にないシステムコールは、エラー `EPERM` で失敗する。

これらの行で止められないこともある。プロセスはデータフォルダーを読み書きできるので、その中のコードは、上の規則のとおりインストール鍵を読み、リースに署名でき、ローカルの管理用のソケットも使える。`/etc` の中のファイルなど、どのユーザーでも読めるファイルは読めるし、ネットワークのどのアドレスにも接続できる。macOS と Windows のサービスには、このような制限はない。

このユニットは、Ubuntu 26.04 の systemd 259 で試した。これらの行でいちばん新しい `ProtectClock=` は、systemd 245 で入った（行ごとの版は systemd.exec(5) にある）。systemd は、知らない行を無視し、ジャーナルに `Unknown key 'ProtectClock' in section [Service], ignoring.` のような警告を書いて、その制限なしでサーバーを動かす。古い systemd では、`daemon-reload` のあとに `sudo journalctl -b | grep 'elm-server.service'` でこのような警告を探す。下の手順でユニットを置いたあと、`sudo systemd-analyze security elm-server` を実行すると、設定ごとの一覧が表示され、最後の行にユニットの露出の度合いが出る。この環境では `Overall exposure level for elm-server.service: 1.7 OK` だった。これらの行のないユニットでは 9.2（UNSAFE）だった。

ターミナルからは起動するのに、ユニットでは起動しないときは、`sudo journalctl -u elm-server` で理由を探す。次の設定をするときは、ユニットで許すものを変える。

- **サーバーが書く、データフォルダーの外のファイル。** サーバーライセンス（`license`）や利用ログ（`log.usage`。[elm-server の設定](../reference/server-configuration.md)）をデータフォルダーの外に置くなら、それぞれのフォルダーを、データフォルダーと同じように `elm-server` を持ち主にして作り、`ReadWritePaths=` に空白で区切って足す。`ReadWritePaths=` はそのフォルダーの書き込みの保護を外すだけで、持ち主とモードが許さない場所に書けるようにはしない。足さないと、サーバーは起動で止まる。利用ログなら `error: The usage log '…' cannot be written: …`、`service.url` があるときのライセンスなら `error: service.url is set, and the server license path '…' cannot be written: …` の行が出る。`service.url` がなければ、サーバーはライセンスを読むだけなので、そのフォルダーは足さなくてよい。
- **サーバーが読むファイル。** プロセスからは `/home`、`/root`、`/run/user` の下のファイルが見えず、`/tmp` も自分専用のものになる。設定ファイル、ルート鍵、TLS の証明書と鍵のファイルは、`/etc/elm-server` のような別のフォルダーに置く。
- **1024 より小さいポート。** root でないユーザーは、そのようなポートで待ち受けられない。その権利をプロセスに渡す `AmbientCapabilities=CAP_NET_BIND_SERVICE` を足し、`CapabilityBoundingSet=` の行を、その権利を残す `CapabilityBoundingSet=CAP_NET_BIND_SERVICE` に変える。空の行のままだと、systemd はその権利を落とす。そのうえで `listen` にポートを設定し、ファイアウォールでそのポートを通す。

ユニットを変えたら、下のとおりに置き直し、`sudo systemctl daemon-reload` と `sudo systemctl restart elm-server` を実行する。

ユニットを置き、サーバーを起動する。

```shell
sudo install -m 0644 elm-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now elm-server
```

`enable` で、これからはマシンの起動と一緒にサーバーが起動する。`--now` で、今すぐ起動する。サーバーの運用ログはジャーナルに書かれる。`sudo journalctl -u elm-server` で見られ、待ち受けを始めると `server.started: listening on port 7070 …` の行が出る。ジャーナルを読めるのは、root と、ディストリビューションがジャーナルを読むことを許すグループ（多くは `systemd-journal` か `adm`）のメンバーだけだ。サーバーそのものに問い合わせるには、`sudo -u elm-server /opt/elm-server/elm server status --data /var/lib/elm-server` を実行する。サーバーライセンス、起動のあとのリースの再登録の期間中かどうか（動いているアプリが、持っているリースを知らせてくるのを待っているか。[フローティングライセンス](../explanation/floating-licenses.md)）、使用中のシートが表示される。

## macOS（launchd）

アーカイブを Web ブラウザーでダウンロードしたなら、確かめたあと、取り出す前に、`xattr -d com.apple.quarantine <archive>` で隔離の属性を外す。属性のないアーカイブでは、`xattr` がそのことを表示する。そのときは外すものはない。外さないと、macOS の Gatekeeper がプログラムを開かない（[セキュリティモデル](../explanation/security-model.md#リリースとこのサイト)）。

サーバー用に、どちらも `_elm-server` という名前のグループとロールアカウントを作る。`sysadminctl` がロールアカウントを作るのは、450 から 499 までのユーザー ID を指定したときだけだ。`-GID` を付けないと、アカウントのグループは `staff` になる。このグループには Mac のすべてのユーザーが入っている。その範囲で、アカウントとグループが使っている ID を一覧し、どちらでも空いている ID を選ぶ。下のコマンドは 451 を使っているので、選んだ ID に置き換える。`id` の出力にも、その ID が表示される。

```shell
dscl . -list /Users UniqueID | awk '$2 >= 450 && $2 < 500'
dscl . -list /Groups PrimaryGroupID | awk '$2 >= 450 && $2 < 500'
sudo dseditgroup -o create -r "elm-server" -i 451 _elm-server
sudo sysadminctl -addUser _elm-server -fullName "elm-server" -UID 451 -GID 451 -roleAccount
id _elm-server
```

`sysadminctl` は、作るのを断ったときも終了コード 0 で終わるので、`id` でアカウントを確かめる。`uid=451(_elm-server) gid=451(_elm-server)` と、すべてのアカウントが入っているグループが表示される。`elm-server`、`elm`、`root.pub.pem` のあるフォルダーで、`elm-server.json` を作る。

<!-- sample: samples/ElmServerService/macos/elm-server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://0.0.0.0:7070",
  "license": "/Library/Application Support/elm-server/server.elmlic",
  "dataDirectory": "/Library/Application Support/elm-server",
  "service": { "url": "https://licensing.example.com" },
  "trust": { "rootKeys": ["/usr/local/elm-server/root.pub.pem"] }
}
```

プログラム、設定、ルート鍵を、root だけが書き込める場所に置く。データフォルダーはアカウントを持ち主にして作り、ログのファイルは、root だけが書き込めるフォルダーの中に、アカウントを持ち主にして作る。

```shell
sudo install -d -m 0755 /usr/local/elm-server /Library/Logs/elm-server
sudo install -m 0755 elm-server elm /usr/local/elm-server/
sudo install -m 0644 elm-server.json root.pub.pem /usr/local/elm-server/
sudo install -d -o _elm-server -g _elm-server -m 0700 "/Library/Application Support/elm-server"
sudo install -m 0640 -o _elm-server -g _elm-server /dev/null /Library/Logs/elm-server/elm-server.log
```

launchd は、ログのファイルをアカウントの権限で開く。root だけが書き込めるフォルダーでは、launchd はファイルを作れず、サーバーが起動しない。そのため、ここでファイルを作っておく。Linux と同じように、アカウントが読めるフォルダーで、`elm-server` と `elm` をアカウントで実行する。サーバーを 1 回起動してから、アクティベーションする。`elm` がライセンスキーを聞いてくる。最後の `cd -` で、ダウンロードしたファイルのあるフォルダーに戻る。

```shell
cd /
sudo -u _elm-server /usr/local/elm-server/elm-server --config /usr/local/elm-server/elm-server.json
sudo -u _elm-server /usr/local/elm-server/elm server activate --product acme-cad --data "/Library/Application Support/elm-server" --trust /usr/local/elm-server/root.pub.pem --service https://licensing.example.com --license "/Library/Application Support/elm-server/server.elmlic"
cd -
```

1 つ目の `sudo` のコマンドは、インストール鍵を作ったあと、`error: The server license '/Library/Application Support/elm-server/server.elmlic' does not exist.` の行を出して、終了コード 1 で止まる。2 つ目は、ライセンス ID、シーケンス番号 1、書き込んだパスと、サーバーが動いていないことを表示して、終了コード 0 で終わる。ダウンロードしたファイルのあるフォルダーで、plist `elm-server.plist` を作る。

<!-- sample: samples/ElmServerService/elm-server.plist -->
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>elm-server</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/local/elm-server/elm-server</string>
    <string>--config</string>
    <string>/usr/local/elm-server/elm-server.json</string>
  </array>
  <key>UserName</key>
  <string>_elm-server</string>
  <key>GroupName</key>
  <string>_elm-server</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key>
    <false/>
  </dict>
  <key>ExitTimeOut</key>
  <integer>60</integer>
  <key>StandardOutPath</key>
  <string>/Library/Logs/elm-server/elm-server.log</string>
  <key>StandardErrorPath</key>
  <string>/Library/Logs/elm-server/elm-server.log</string>
</dict>
</plist>
```

- `RunAtLoad`: launchd は plist を読み込んだときにサーバーを起動する。そのため、マシンが起動するたびにも起動する。
- `KeepAlive` の `SuccessfulExit` が `false`: サーバーが 0 以外の終了コードで終わるか、シグナルで止められると、launchd はもう一度起動する。終了コード 0 で止まったときは起動しない。launchd がプログラムを起動するのは、10 秒に 1 回までだ。それより長く動いていたサーバーは 1 秒ほどで戻り、それより早く止まったサーバーは、10 秒の残りが過ぎてから戻る。
- `ExitTimeOut` の 60: launchd は、サーバーが止まるのを 60 秒待ってから強制終了する。このキーがないと、launchd は自分の既定の時間しか待たない。macOS 26 ではそれが 5 秒で、停止にかかることがある時間より短い。
- `StandardOutPath` と `StandardErrorPath`: サーバーの運用ログ。上で作ったファイルだ。

plist を置き、サーバーを起動する。

```shell
sudo install -m 0644 elm-server.plist /Library/LaunchDaemons/
sudo launchctl bootstrap system /Library/LaunchDaemons/elm-server.plist
```

`launchctl bootstrap` は、plist を読み込めたときは何も表示しない。サーバーが待ち受けを始めると、ログに `server.started: listening on port 7070 …` の行が出る。`sudo tail /Library/Logs/elm-server/elm-server.log` で見られる。`sudo launchctl print system/elm-server` は、ジョブの状態、プロセス、最後の終了コードを表示する。サーバーそのものに問い合わせるには、`sudo -u _elm-server /usr/local/elm-server/elm server status --data "/Library/Application Support/elm-server"` を実行する。あとで plist を変えるときは、先に `sudo launchctl bootout system/elm-server` で読み込みを外してから（これでサーバーも止まる）、`launchctl bootstrap` でもう一度読み込む。

## Windows

「管理者として実行」で開いた PowerShell 7 で、`elm-server.exe`、`elm.exe`、`root.pub.pem` のあるフォルダーに、`elm-server.json` を作る。設定の中のパスでは、`\` を `\\` と書く。

<!-- sample: samples/ElmServerService/windows/elm-server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://0.0.0.0:7070",
  "license": "C:\\ProgramData\\elm-server\\server.elmlic",
  "dataDirectory": "C:\\ProgramData\\elm-server",
  "service": { "url": "https://licensing.example.com" },
  "trust": { "rootKeys": ["C:\\Program Files\\elm-server\\root.pub.pem"] }
}
```

4 つのファイルを、管理者だけが書き込める `C:\Program Files\elm-server` に置く。

```powershell
New-Item -ItemType Directory 'C:\Program Files\elm-server'
Copy-Item elm-server.exe, elm.exe, elm-server.json, root.pub.pem 'C:\Program Files\elm-server'
```

サービスを登録する。アカウントを指定しないので、サービスは LocalSystem で動く。

```powershell
New-Service -Name elm-server -BinaryPathName '"C:\Program Files\elm-server\elm-server.exe" --config "C:\Program Files\elm-server\elm-server.json"' -StartupType Automatic
```

サービスを 1 回起動して、データフォルダーとインストール鍵をサービスに作らせる。データフォルダーを自分で作ったり、先にターミナルで `elm-server` を実行したりしない。自分のアカウントが作ったフォルダーは、アクセスルールにそのアカウントを名指すので、サービスはそのフォルダーを受け付けない（[elm-server の設定](../reference/server-configuration.md#windows-サービス)）。まだライセンスがないので、サービスはすぐに止まり、`Start-Service` はエラーを報告する。

```powershell
Start-Service elm-server
```

理由は、インストール鍵のサムプリントとマシンフィンガープリントとともに、Application のイベントログにイベント ID 50 として残る。

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'elm-server'; Id = 50 } -MaxEvents 1 | Select-Object -ExpandProperty Message
```

そのメッセージは `error: The server license 'C:\ProgramData\elm-server\server.elmlic' does not exist.` で始まる。同じターミナルでサーバーをアクティベーションする。`elm` はライセンスキーを聞いてきて、ライセンス ID、シーケンス番号 1、書き込んだパスを表示したあと、終了コード 0 で終わる。

```powershell
& 'C:\Program Files\elm-server\elm.exe' server activate --product acme-cad --data C:\ProgramData\elm-server --trust 'C:\Program Files\elm-server\root.pub.pem' --service https://licensing.example.com --license C:\ProgramData\elm-server\server.elmlic
```

サーバーがライセンスを持ったので、失敗したときにもう一度起動するようにサービスコントロールマネージャーに設定してから、サービスを起動する。

```powershell
sc.exe failure elm-server reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service elm-server
```

`sc.exe failure` は、失敗のたびに 60 秒後にサービスを起動し直す。3 回目より後の失敗では、最後の動作を繰り返す。失敗がないまま 1 日（86,400 秒）が過ぎると、失敗の数を 0 から数え直す。失敗とは、サーバーが停止を報告しないまま終わることだ。停止を報告してからエラーのコードで終わったサーバーは、起動し直さない。このガイドでは、この設定をライセンスのあとの今になってから行う。サービスが動いているあいだ、`Get-Service elm-server` は `Running` を表示する。運用ログは、Application のイベントログに、`Warning` 以上だけが書かれる（[ログ](../reference/logs.md#windows-サービスとしての-elm-server)）。そのため、既定では `server.started` の行はそこにない。サーバーそのものに問い合わせるには、`& 'C:\Program Files\elm-server\elm.exe' server status --data C:\ProgramData\elm-server` を実行する。

## サーバーを止めて記録を確かめる

サーバーは、サービスマネージャーで止め、起動し直す。

| | Linux | macOS | Windows |
| --- | --- | --- | --- |
| 止める | `sudo systemctl stop elm-server` | `sudo launchctl kill SIGTERM system/elm-server` | `Stop-Service elm-server` |
| 起動し直す | `sudo systemctl start elm-server` | `sudo launchctl kickstart system/elm-server` | `Start-Service elm-server` |

サーバーは待ち受けをやめ、利用ログと借用の記録に `server.stopped` を書いて署名し、終了コード 0 で終わる。処理中のリクエストがなければ、止まるまでの時間は記録を閉じる時間だけで、1 秒よりずっと短い。まだ答えているリクエストがあれば、サーバーはそれが終わるのを最長 30 秒待ち、まだ開いている接続を切って、さらに最長 1 秒待ってから記録を閉じる。合わせて、最長でおよそ 31 秒と、記録を閉じる時間になる。それより長くかかるサーバーは、サービスマネージャーが終わらせる。systemd はユニットの `TimeoutStopSec`（60 秒）のあとで、launchd は plist の `ExitTimeOut`（60 秒）のあとで終わらせる。Windows では、30 秒を超える停止は 30 秒の時点で終わったと報告され、そのあとはいつプロセスが終わってもおかしくない。Windows のシャットダウンでは、`HKLM\SYSTEM\CurrentControlSet\Control` のレジストリ値 `WaitToKillServiceTimeout` の時間が過ぎると、サービスが終わらされることがある。その値は、`Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control -Name WaitToKillServiceTimeout` でミリ秒の単位で表示できる（[elm-server の設定](../reference/server-configuration.md#windows-サービス)）。こうして終わらされたサーバーは、`server.stopped` を書いていないことがある。そのときは次の起動が、クラッシュのあとと同じく、前の実行のリースを待つ（[フローティングライセンス](../explanation/floating-licenses.md)）。直すものはない。

記録を閉じたことを確かめるには、データフォルダーの最新の利用ログのファイル（一覧で最後の `usage-<UTC time>.jsonl` の名前）の、最後の 2 行を読む。1 行目は `server.stopped` の行、最後の行はその署名である `auth` の行になる。そうでなければ、上のとおり、サーバーは記録を閉じる前に終わらされている。

```shell
sudo ls /var/lib/elm-server
sudo tail -n 2 /var/lib/elm-server/usage-<UTC time>.jsonl
```

macOS では、フォルダーは引用符で囲んだ `"/Library/Application Support/elm-server"` になる。Windows では、`Get-ChildItem C:\ProgramData\elm-server\usage-*.jsonl | Sort-Object Name | Select-Object -Last 1 | Get-Content -Tail 2` で表示できる。ログ全体は、[利用ログを検証する](verify-the-usage-log.md)の手順で確かめる。そのページの `elm` のコマンドは、上のパスで、サービスのアカウント（Windows では「管理者として実行」で開いた PowerShell）で実行する。サーバーが止まっているあいだは、新しいほうのファイルを削除したり、最新のファイルを署名の行の直後で切り詰めたりしても跡が残らないので、ログのコピーを取っておくなら、最新のファイルもコピーする（[書き終えたファイルのコピーを取っておく](verify-the-usage-log.md#書き終えたファイルのコピーを取っておく)）。

## 運用ログを回す

サーバーは、動いているあいだずっと運用ログを書く。Linux と macOS では、`Information` 以上の段で、リクエストごとの行も書く（[ログ](../reference/logs.md)）。サーバーは運用ログを標準出力に書き、それをサービスマネージャーが受け取る。macOS では、launchd がサーバーを起動するときにログのファイルを開き、次の起動まで、だれもそのファイルを開き直さない。ログが際限なく大きくならないようにする方法は、サービスマネージャーごとに違う。

**Linux。** ジャーナルは journald が持ち、回す。ユニットに足すものはない。ジャーナルが使う容量と、項目を残す期間は、`/etc/systemd/journald.conf` で設定する（[journald.conf(5)](https://www.freedesktop.org/software/systemd/man/latest/journald.conf.html)）。既定では、ジャーナルが使うのはファイルシステムの 10% までで、4 GB を超えない（`SystemMaxUse=`）。それに収めるため、古い閉じたファイルから消す。項目を残す期間に決まりはない（`MaxRetentionSec=0`）。journald は、1 つのサービスが一定の時間に書ける行の数も制限する。既定では 30 秒に 10,000 行（`RateLimitBurst=` と `RateLimitIntervalSec=`）で、ジャーナルに使える空き容量に応じて大きくなる係数を掛ける（その表は journald.conf(5) にある）。上限を超えた行は、30 秒が過ぎるまで捨てられ、捨てた行の数を書いた行が残る。ディストリビューションが既定を変えていることもある。`systemd-analyze cat-config systemd/journald.conf` で今効いている設定を、`journalctl --disk-usage` で今ジャーナルが使っている容量を見られる。設定を変えたら、`sudo systemctl restart systemd-journald` を実行する。

**macOS。** サーバーを動かしたまま、ログのファイルをコピーしてから空に切り詰める launchd のジョブを、もう 1 つ置いて回す。ファイルを別の場所に動かす回し方は、ここでは使えない。サーバーは、どこに動かされても開いているファイルに書き続け、それは起動し直すまで変わらない。そして起動し直すと、サーバーはリースを再登録してからでないと新しいリースを渡さない（[フローティングライセンス](../explanation/floating-licenses.md)）。切り詰める形が使えるのは、launchd がログのファイルを追記で開くからだ。切り詰めたあと、サーバーの次の行はファイルの先頭に書かれる。ダウンロードしたファイルのあるフォルダーで、ジョブの plist `elm-server-log.plist` を作る。

<!-- sample: samples/ElmServerService/macos/elm-server-log.plist -->
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>elm-server-log</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/sh</string>
    <string>-c</string>
    <string>log=/Library/Logs/elm-server/elm-server.log; [ "$(stat -f %z "$log")" -ge 10485760 ] || exit 0; for i in 5 4 3 2 1 0; do [ ! -f "$log.$i" ] || mv -f "$log.$i" "$log.$((i + 1))"; done; cp -p "$log" "$log.0" &amp;&amp; : &gt; "$log"</string>
  </array>
  <key>StartInterval</key>
  <integer>600</integer>
</dict>
</plist>
```

launchd は、600 秒ごとにこのコマンドを root で実行する。ログのファイルが 10,485,760 バイト（10 MiB）以上なら、コピーの `elm-server.log.0` から `.5` の番号を 1 つずつ上げ（いちばん古い `.6` は置き換わる）、ログのファイルを持ち主とモードごと `elm-server.log.0` にコピーし、ログのファイルを空に切り詰める。そのため、ログが使うのは、7 つのコピーと合わせておよそ 80 MiB までと、次に確かめるまでの 600 秒にサーバーが書く分になる。コピーしてから切り詰めるまでにサーバーが書いた行は失われる。macOS 26 で、1 ミリ秒ごとに 1 行を書くプログラムを 20 回回すと、17 回は 1 行も失われず、3 回は 1 行が失われた。plist を root だけが書き込める場所に置き、読み込む。

```shell
sudo install -m 0644 elm-server-log.plist /Library/LaunchDaemons/
sudo launchctl bootstrap system /Library/LaunchDaemons/elm-server-log.plist
```

このジョブは、読み込んだときには動かず、そのあと 600 秒ごとに動く。今すぐ回すには、`sudo launchctl kickstart system/elm-server-log` を実行する。ログのファイルそのものは、動かしたり消したりしない。root だけが書き込めるフォルダーでは launchd がファイルを作り直せず、サーバーは次の起動で起動しない。そのとき `sudo launchctl print system/elm-server` には `last exit code = 78: EX_CONFIG` が出る。

**Windows。** Application のイベントログの最大の大きさと、いっぱいになったときの扱いは、イベントログの設定で決まり、`wevtutil gl Application` が `maxSize`（バイト。ある Windows 11 のマシンでは 20,971,520）と `retention`（`false` なら新しいイベントがいちばん古いイベントを上書きし、`true` なら古いイベントを残して新しいイベントを捨てる。[wevtutil](https://learn.microsoft.com/windows-server/administration/windows-commands/wevtutil)）として表示し、管理者は `wevtutil sl Application /ms:<bytes>` で大きさを変えられる。

## サーバーが止まり続けるとき

ライセンスの期限が切れた、設定が間違っているなど、サーバーが起動できないときは、`error:` で始まる 1 行を書いて、終了コード 1 か 2 で終わる。Linux と macOS では、サービスマネージャーはこれを失敗とみなしてサーバーをもう一度起動し、サーバーはまた止まる。systemd は 5 秒ごと、launchd は 10 秒ごとに繰り返す。Windows では、サーバーが待ち受けを始める前に止まる起動では、`Start-Service` がエラーを報告する。そのあとで止まる起動（サーバーがアドレスで待ち受けられないときや、TLS 証明書の中間 CA の確認に失敗したとき）では、サービスはいったん実行中になってから、停止を報告せずに終わる。`sc.exe failure` は、これを失敗とみなす。`error:` の行は、ジャーナル（`sudo journalctl -u elm-server`）、`/Library/Logs/elm-server/elm-server.log`、Application のイベントログのイベント ID 50 にある。サービスを止めて原因を直し、もう一度起動する。ライセンスの期限が切れたときは、[サーバーライセンスを更新する](renew-a-server-license.md)を、ベンダーのサービスから更新するライセンスなら[elm-service でサーバーライセンスをアクティベーションする](activate-a-server-license-with-elm-service.md)を参照する。

## サービスを取り除く

| Linux | macOS | Windows |
| --- | --- | --- |
| `sudo systemctl disable --now elm-server` のあと、`sudo rm /etc/systemd/system/elm-server.service` と `sudo systemctl daemon-reload` | `sudo launchctl bootout system/elm-server` と `sudo launchctl bootout system/elm-server-log` のあと、`sudo rm /Library/LaunchDaemons/elm-server.plist /Library/LaunchDaemons/elm-server-log.plist` | `Stop-Service elm-server` のあと、`sc.exe delete elm-server` |

データフォルダーには、サーバーのインストール鍵とライセンスが残っている。アクティベーションが要らなくなったときだけ削除する。サーバーを `elm-service` でアクティベーションしたなら、先に、サービスのアカウント（Windows では「管理者として実行」で開いた PowerShell）で `elm server deactivate` を実行して、アクティベーションを解除する（[オフラインでアクティベーションし、サーバーを移す](activate-a-server-license-with-elm-service.md#オフラインでアクティベーションしサーバーを移す)）。
