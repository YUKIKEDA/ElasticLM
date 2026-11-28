---
sidebar_position: 23
---

# Run elm-server as a system service

This guide is for the customer's administrator who runs `elm-server` for good: as a service of the operating system, so that it starts with the machine and starts again when it fails. It covers Linux with systemd, macOS with launchd, and Windows. You should already know how to set up a server, as in [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md), and how it gets its license, as in [Activate a server license with elm-service](activate-a-server-license-with-elm-service.md).

<!-- steps:e2e tests=SystemdServiceTests.Systemd_ThePageSteps_RunRestartAndStopTheServer,SystemdServiceTests.Systemd_HardenedUnit_LendsAndTakesBackALease,LaunchdServiceTests.Launchd_ThePageSteps_RunRestartAndStopTheServer,WindowsServiceTests.NewService_ThePageSteps_StartActivateAndStopTheServer -->

## Before you start

You need:

- `elm-server` and `elm` for the machine, from the archives `elm-server-<version>-<RID>` and `elm-<version>-<RID>` on the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases), checked as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`.
- The vendor's root public key, `root.pub.pem` below, and the product ID, `acme-cad` below.
- The license key of a server license from the vendor's `elm-service`, and the service's address, `https://licensing.example.com` below. With the setting `service.url`, the server then renews its license there by itself ([Renew online](activate-a-server-license-with-elm-service.md#renew-online)).
- An administrator's terminal: `sudo` on Linux and macOS, and PowerShell 7 opened with "Run as administrator" on Windows.

If the vendor issues the server license file by hand instead, leave out `service.url` and the step that activates. Send the vendor the installation key's thumbprint and the machine fingerprints from the line that the first start writes (on Windows, in the event log), and put the file they send at the path of `license`, owned by the service's account, for example on Linux with `sudo install -m 0600 -o elm-server -g elm-server server.elmlic /var/lib/elm-server/`.

Each section below puts the files in these places. You may choose others, as long as they keep the rules after the table.

| What | Linux | macOS | Windows |
| --- | --- | --- | --- |
| `elm-server`, `elm`, the settings file, `root.pub.pem` | `/opt/elm-server/` (programs), `/etc/elm-server/` (settings, root key) | `/usr/local/elm-server/` | `C:\Program Files\elm-server\` |
| Data folder, with the server license file | `/var/lib/elm-server` | `/Library/Application Support/elm-server` | `C:\ProgramData\elm-server` |
| Service definition | `/etc/systemd/system/elm-server.service` | `/Library/LaunchDaemons/elm-server.plist` | registered with the service control manager |
| Operational log | the journal | `/Library/Logs/elm-server/elm-server.log` | the Application event log |
| Account | the system user `elm-server` | the role account `_elm-server` | LocalSystem |

- **Only root, or on Windows the administrators, may write the programs, the settings file, the root key, and the service definition, and every folder above them.** Whoever can replace `elm-server` or `elm` runs their own code as the service's account, which can read the server's installation key and sign leases. Whoever can change the settings or the root key changes which keys the server trusts and where it listens. Whoever can change the service definition runs code as root, or on Windows as LocalSystem.
- **Only the service's account may open the data folder, besides root on Linux and macOS and the Administrators group and SYSTEM on Windows.** It holds the server's installation key and its administrator token, and the server refuses a data folder that others may change ([Which folders the server accepts](../reference/server-configuration.md#which-folders-the-server-accepts)). Put the server license file there too.
- **Give every path as an absolute path**, in the settings and in the service definition. The server reads a relative path from the folder it runs in, which the service manager chooses.
- **Keep secrets out of the service definition.** Set no `ELM_` variable there. Type the license key when `elm server activate` asks for it. If you use your own TLS certificate ([Use your own TLS certificate](use-your-own-tls-certificate.md)), give it as a PEM certificate and key file (`tls.certificate` and `tls.key`), and let only the service's account read the key file. A PKCS#12 file with a password needs `ELM_TLS_PASSWORD`, so use PEM files here.
- **Let the clients reach the port.** The server listens on port 7070 of every IPv4 address. If a firewall runs on the machine, allow incoming TCP connections to that port.

## Linux (systemd)

Create a system user for the server, with a group of the same name and no shell to log in with:

```shell
sudo useradd --system --user-group --shell /usr/sbin/nologin elm-server
```

In the folder with `elm-server`, `elm`, and `root.pub.pem`, create `elm-server.json`:

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

Put the programs, the settings, and the root key where only root may write them, and create the data folder, owned by the server's user, that only that user may open:

```shell
sudo install -d -m 0755 /opt/elm-server /etc/elm-server
sudo install -m 0755 elm-server elm /opt/elm-server/
sudo install -m 0644 elm-server.json root.pub.pem /etc/elm-server/
sudo install -d -o elm-server -g elm-server -m 0700 /var/lib/elm-server
```

Run `elm-server` and `elm` as the server's user from now on, not as root: the data folder belongs to that user, and `elm` refuses it for its license commands when another user runs them, root included ([elm server](../reference/elm-cli.md#elm-server)). Run those commands from a folder that the user can read, such as `/`, since your own folder may be closed to it. Go there, and start the server once from the terminal:

```shell
cd /
sudo -u elm-server /opt/elm-server/elm-server --config /etc/elm-server/elm-server.json
```

It creates its installation key in the data folder, and stops with exit code 1, since it has no license yet: the line starts with `error: The server license '/var/lib/elm-server/server.elmlic' does not exist.` and names the key. Activate the server. `sudo` does not pass your environment on, so `elm` asks for the license key; paste it there:

```shell
sudo -u elm-server /opt/elm-server/elm server activate --product acme-cad --data /var/lib/elm-server --trust /etc/elm-server/root.pub.pem --service https://licensing.example.com --license /var/lib/elm-server/server.elmlic
```

`elm` ends with exit code 0 and prints the license ID, the sequence number 1, and the path it wrote, and then `The server is not running; it reads the license when it starts.` Go back to the folder with the downloaded files (`cd -` goes back to the folder you came from), and create the unit, `elm-server.service`, there:

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

- `Type=exec`: systemd takes the service as started once `elm-server` runs.
- `Restart=on-failure` and `RestartSec=5`: when the server ends with an exit code other than 0, or it is killed by a signal such as `SIGKILL`, systemd starts it again 5 seconds later. A stop with `systemctl stop`, which sends `SIGTERM`, is not a failure.
- `TimeoutStopSec=60`: systemd waits 60 seconds for the server to stop, and then kills it ([Stop the server and check its records](#stop-the-server-and-check-its-records)). Without the setting, systemd waits its default time, which the machine's systemd settings decide (90 seconds on Ubuntu 26.04).

The lines after `TimeoutStopSec` narrow what someone who runs code inside the server's process, for example through a flaw in the server, can do ([systemd.exec(5)](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html) describes each). They apply only to the process that systemd starts from this unit, not to other processes of the same user, such as one started with `sudo -u elm-server`:

- `NoNewPrivileges=yes`, an empty `CapabilityBoundingSet=`, and `RestrictSUIDSGID=yes`: the process holds no Linux capability, gains no privilege, not even by running a set-user-ID program, and cannot make a set-user-ID or set-group-ID file.
- `ProtectSystem=strict` with `ReadWritePaths=/var/lib/elm-server`: the file system is read-only to the process, except the data folder, where the server keeps everything it writes unless you move the data folder, the license, or the usage log (below), and its own `/tmp` (next item). `ProtectHome=yes`, `PrivateTmp=yes`, and `PrivateDevices=yes`: it sees no home folder, gets a `/tmp` and `/var/tmp` of its own, which no other process shares and which do not outlast the run, and sees no hardware device.
- `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`: it can open only the kinds of socket the server uses: the local administration socket in the data folder, and IPv4 and IPv6 for its connections and name lookups.
- `ProtectKernelTunables`, `ProtectKernelModules`, `ProtectKernelLogs`, `ProtectControlGroups`, `ProtectClock`, `ProtectHostname`, `RestrictNamespaces`, `RestrictRealtime`, `LockPersonality`, and `MemoryDenyWriteExecute`: it cannot change the kernel's settings, load kernel modules, read the kernel's log, change control groups, set the clock or the host name, create namespaces, take real-time scheduling, change its execution domain, or make memory both writable and executable.
- `SystemCallArchitectures=native`: the process may make only the system calls of the machine's own architecture. `SystemCallFilter=@system-service` and `SystemCallErrorNumber=EPERM`: a system call outside the set that systemd lists for services (`systemd-analyze syscall-filter @system-service` prints it) fails with the error `EPERM`.

What these lines do not stop: the process still reads and writes the data folder, so code inside it can still read the installation key and the administrator token, sign leases, and change the records, as the rules above say, and use the local administration socket. It can still read the files that every user may read, such as those in `/etc`, and still connect to any address over the network. On macOS and Windows, the service gets no such limits.

The unit was tried with systemd 259 on Ubuntu 26.04. The newest of these lines, `ProtectClock=`, came with systemd 245 (systemd.exec(5) gives the version of each). systemd ignores a line it does not know, writes a warning that names the unit file and the line to the journal (systemd 259 writes `/etc/systemd/system/elm-server.service:<line>: Unknown key '<name>' in section [Service], ignoring.`; other versions word it differently), and runs the server without that limit. On an older systemd, look for such warnings after `daemon-reload` with `sudo journalctl -b | grep 'elm-server.service'`. After you install the unit (below), `sudo systemd-analyze security elm-server` lists each setting, and its last line rates the unit's exposure: `Overall exposure level for elm-server.service: 1.7 OK` there, against 9.2 (UNSAFE) for the unit without these lines.

If the server starts from the terminal, as above, but not under the unit, look for the reason in `sudo journalctl -u elm-server`. The settings below change what the unit must allow:

- **Another data folder.** If you set `dataDirectory` to another folder, create it as above and replace `/var/lib/elm-server` in `ReadWritePaths=` with it.
- **Files outside the data folder that the server writes.** If you put the server license (`license`) or the usage log (`log.usage`, [elm-server configuration](../reference/server-configuration.md)) outside the data folder, create each of their folders owned by `elm-server`, as for the data folder, and add it to `ReadWritePaths=`, separated by a space. `ReadWritePaths=` lifts the write protection for a folder, but does not let the user write where its owner and mode do not, and a folder it names that does not exist stops the unit before the server runs, with no `error:` line. Without the folder in `ReadWritePaths=`, the server stops at its start, with `error: The usage log '…' cannot be written: Read-only file system …` for the usage log, or for the license when `service.url` is set, `error: service.url is set, and the server license path '…' cannot be written: …`. Without `service.url`, the server only reads the license, and its folder needs no change.
- **Files the server reads.** The process cannot see files under `/home`, `/root`, or `/run/user`, and has a `/tmp` of its own, so keep the settings file, the server license, the root keys, and any TLS certificate and key file in other folders, such as `/etc/elm-server` and the data folder.
- **A port below 1024.** With the kernel's default (`net.ipv4.ip_unprivileged_port_start` 1024), a user other than root cannot listen on such a port. Add `AmbientCapabilities=CAP_NET_BIND_SERVICE`, which gives the process that right, and change the line `CapabilityBoundingSet=` to `CapabilityBoundingSet=CAP_NET_BIND_SERVICE`, which keeps it. With the empty line, systemd drops the right, and the server stops at its start with `error: elm-server cannot listen on … Permission denied`, as it does without the right. Then set the port in `listen`, and let it through the firewall.

After you change the unit, install it again as below, and run `sudo systemctl daemon-reload` and `sudo systemctl restart elm-server`.

Install it, and start the server:

```shell
sudo install -m 0644 elm-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now elm-server
```

`enable` starts it with the machine from now on, and `--now` starts it now. The server's operational log goes to the journal: `sudo journalctl -u elm-server` shows it, with the line `server.started: listening on port 7070 …` once the server listens. Only root and the members of the groups that the distribution lets read the journal, often `systemd-journal` or `adm`, can read it. To ask the server itself, run `sudo -u elm-server /opt/elm-server/elm server status --data /var/lib/elm-server`: it shows the server license, whether the server is still reclaiming leases after its start (waiting for running apps to report the leases they hold, [Floating licenses](../explanation/floating-licenses.md)), and the seats in use.

## macOS (launchd)

If a web browser downloaded the archives, remove the quarantine attribute from them after checking them, before you extract them: `xattr -d com.apple.quarantine <archive>`. An archive without the attribute makes `xattr` say so, and then there is nothing to remove. macOS Gatekeeper does not open the programs otherwise ([Security model](../explanation/security-model.md#releases-and-this-site)).

Create a group and a role account for the server, both named `_elm-server`. `sysadminctl` makes a role account only with a user ID from 450 to 499; without `-GID`, the account's group is `staff`, which every user of the Mac is in. List the IDs taken in that range by accounts and groups, and pick one that is free in both. The commands below use 451; put your ID in their place, and expect it in the output of `id`:

```shell
dscl . -list /Users UniqueID | awk '$2 >= 450 && $2 < 500'
dscl . -list /Groups PrimaryGroupID | awk '$2 >= 450 && $2 < 500'
sudo dseditgroup -o create -r "elm-server" -i 451 _elm-server
sudo sysadminctl -addUser _elm-server -fullName "elm-server" -UID 451 -GID 451 -roleAccount
id _elm-server
```

`sysadminctl` ends with exit code 0 also when it refuses, so check the account with `id`: it prints `uid=451(_elm-server) gid=451(_elm-server)` and the groups that every account is in. In the folder with `elm-server`, `elm`, and `root.pub.pem`, create `elm-server.json`:

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

Put the programs, the settings, and the root key where only root may write them. Create the data folder, owned by the account, and the log file, owned by the account in a folder that only root may write:

```shell
sudo install -d -m 0755 /usr/local/elm-server /Library/Logs/elm-server
sudo install -m 0755 elm-server elm /usr/local/elm-server/
sudo install -m 0644 elm-server.json root.pub.pem /usr/local/elm-server/
sudo install -d -o _elm-server -g _elm-server -m 0700 "/Library/Application Support/elm-server"
sudo install -m 0640 -o _elm-server -g _elm-server /dev/null /Library/Logs/elm-server/elm-server.log
```

launchd opens the log file as the account. In a folder that only root may write, it cannot create the file, and the server does not start; so the file is created here. As on Linux, run `elm-server` and `elm` as the account from a folder it can read, start the server once, and activate it; `elm` asks for the license key. `cd -` then goes back to the folder with the downloaded files:

```shell
cd /
sudo -u _elm-server /usr/local/elm-server/elm-server --config /usr/local/elm-server/elm-server.json
sudo -u _elm-server /usr/local/elm-server/elm server activate --product acme-cad --data "/Library/Application Support/elm-server" --trust /usr/local/elm-server/root.pub.pem --service https://licensing.example.com --license "/Library/Application Support/elm-server/server.elmlic"
cd -
```

The first command stops with exit code 1 and the line `error: The server license '/Library/Application Support/elm-server/server.elmlic' does not exist.`, after it has created the installation key. The second ends with exit code 0 and prints the license ID, the sequence number 1, and the path it wrote, and then that the server is not running. In the folder with the downloaded files, create the plist, `elm-server.plist`:

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

- `RunAtLoad`: launchd starts the server when it loads the plist, and so at each start of the machine.
- `KeepAlive` with `SuccessfulExit` `false`: launchd starts the server again when it ends with an exit code other than 0, or a signal kills it, and not after a stop with exit code 0. It starts a program at most once every 10 seconds: a server that ran longer comes back within about a second, and one that stopped sooner, after the rest of the 10 seconds.
- `ExitTimeOut` 60: launchd waits 60 seconds for the server to stop before it kills it. Without the key, launchd waits only its own default time, which was 5 seconds on macOS 26, less than a stop can take.
- `StandardOutPath` and `StandardErrorPath`: the server's operational log, the file you created above.

Install it, and start the server:

```shell
sudo install -m 0644 elm-server.plist /Library/LaunchDaemons/
sudo launchctl bootstrap system /Library/LaunchDaemons/elm-server.plist
```

`launchctl bootstrap` prints nothing when it loads the plist. The log shows the line `server.started: listening on port 7070 …` once the server listens: `sudo tail /Library/Logs/elm-server/elm-server.log`. `sudo launchctl print system/elm-server` shows the job's state, its process, and its last exit code. To ask the server itself, run `sudo -u _elm-server /usr/local/elm-server/elm server status --data "/Library/Application Support/elm-server"`. To change the plist later, unload it first with `sudo launchctl bootout system/elm-server`, which also stops the server, and then load it again with `launchctl bootstrap`.

## Windows

In PowerShell 7 opened with "Run as administrator", in the folder with `elm-server.exe`, `elm.exe`, and `root.pub.pem`, create `elm-server.json`. In the settings, a `\` in a path is written `\\`:

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

Put the four files in `C:\Program Files\elm-server`, where only administrators may write:

```powershell
New-Item -ItemType Directory 'C:\Program Files\elm-server'
Copy-Item elm-server.exe, elm.exe, elm-server.json, root.pub.pem 'C:\Program Files\elm-server'
```

Register the service. It runs as LocalSystem, the account a service gets when none is given:

```powershell
New-Service -Name elm-server -BinaryPathName '"C:\Program Files\elm-server\elm-server.exe" --config "C:\Program Files\elm-server\elm-server.json"' -StartupType Automatic
```

Start it once, and let the service create its data folder and installation key. Do not create the data folder yourself, and do not run `elm-server` in the terminal first: a folder that your account created names your account in its access rules, and the service refuses it ([elm-server configuration](../reference/server-configuration.md#windows-service)). The service stops at once, since it has no license yet, and `Start-Service` reports an error:

```powershell
Start-Service elm-server
```

The reason, with the installation key's thumbprint and the machine fingerprints, is in the Application event log as event ID 50:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'elm-server'; Id = 50 } -MaxEvents 1 | Select-Object -ExpandProperty Message
```

It starts with `error: The server license 'C:\ProgramData\elm-server\server.elmlic' does not exist.` Activate the server in the same terminal; `elm` asks for the license key, and ends with exit code 0 after it prints the license ID, the sequence number 1, and the path it wrote:

```powershell
& 'C:\Program Files\elm-server\elm.exe' server activate --product acme-cad --data C:\ProgramData\elm-server --trust 'C:\Program Files\elm-server\root.pub.pem' --service https://licensing.example.com --license C:\ProgramData\elm-server\server.elmlic
```

Now that the server has its license, tell the service control manager to start it again when it fails, and start it:

```powershell
sc.exe failure elm-server reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service elm-server
```

`sc.exe failure` restarts the service 60 seconds after each failure; after the third, it repeats the last action for each later failure, and it counts the failures from zero again after a day (86,400 seconds) without one. A failure is an end without a stop that the server reported; a server that reports its stop and then ends with an error code is not restarted. This guide sets it only now, after the license. `Get-Service elm-server` shows `Running` while the service runs. The operational log goes to the Application event log, from `Warning` up ([Logs](../reference/logs.md#elm-server-as-a-windows-service)), so the line `server.started` is not there by default. To ask the server itself, run `& 'C:\Program Files\elm-server\elm.exe' server status --data C:\ProgramData\elm-server`.

## Stop the server and check its records

Stop and start the server with the service manager:

| | Linux | macOS | Windows |
| --- | --- | --- | --- |
| Stop | `sudo systemctl stop elm-server` | `sudo launchctl kill SIGTERM system/elm-server` | `Stop-Service elm-server` |
| Start again | `sudo systemctl start elm-server` | `sudo launchctl kickstart system/elm-server` | `Start-Service elm-server` |

The server stops listening, writes `server.stopped` to the usage log and the borrow record, signs them, and ends with exit code 0. With no request in progress, the stop takes as long as closing the records, well under a second. When requests are still being answered, the server waits until they end, at most 30 seconds, then cuts those still open and waits up to 1 second more before it closes the records: at most about 31 seconds and the time to close them. The service manager ends a server that takes longer: systemd after the unit's `TimeoutStopSec` (60 seconds), and launchd after the plist's `ExitTimeOut` (60 seconds). On Windows, a stop that takes more than 30 seconds is reported as finished at 30 seconds, and the process may end at any point after that; when Windows shuts down, it may end the service after the time in the registry value `WaitToKillServiceTimeout` under `HKLM\SYSTEM\CurrentControlSet\Control`, which `Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control -Name WaitToKillServiceTimeout` shows in milliseconds ([elm-server configuration](../reference/server-configuration.md#windows-service)). A server ended in any of these ways may not have written `server.stopped`; then its next start waits for the leases of the run before, as after a crash ([Floating licenses](../explanation/floating-licenses.md)); nothing needs repair.

To check that the records were closed, read the last two lines of the newest usage log file in the data folder, the last `usage-<UTC time>.jsonl` name in the list: the first is a `server.stopped` line, and the last is its signature, an `auth` line. If they are something else, the server was ended before it closed its records, as above.

```shell
sudo ls /var/lib/elm-server
sudo tail -n 2 /var/lib/elm-server/usage-<UTC time>.jsonl
```

On macOS, the folder is `"/Library/Application Support/elm-server"`, in quotes. On Windows, `Get-ChildItem C:\ProgramData\elm-server\usage-*.jsonl | Sort-Object Name | Select-Object -Last 1 | Get-Content -Tail 2` prints them. [Verify the usage log](verify-the-usage-log.md) checks the whole log; run its `elm` commands as the service's account (on Windows, in PowerShell opened with "Run as administrator") with the paths above. While the server is stopped, its newest files can be deleted, and the newest one cut right after a signature line, without a trace, so copy the newest file too when you keep copies of the log ([Keep copies of the finished files](verify-the-usage-log.md#keep-copies-of-the-finished-files)).

## Rotate the operational log

The server writes its operational log as long as it runs ([Logs](../reference/logs.md)). On Linux and macOS, it writes the entries from the level `Information` up, with lines for each request, to its standard output, which the service manager takes: on macOS, launchd opens the log file when it starts the server, and nothing opens the file again until the next start. On Windows, it writes the entries from `Warning` up to the Application event log. Each service manager keeps the log from growing without end in its own way.

**Linux.** journald keeps the journal and rotates it, and the unit needs nothing for it. How much space the journal takes and how long it keeps entries are set in `/etc/systemd/journald.conf` ([journald.conf(5)](https://www.freedesktop.org/software/systemd/man/latest/journald.conf.html)). By default it takes at most 10% of its file system and no more than 4 GiB (`SystemMaxUse=`), deleting the oldest archived files to stay within that, and keeps entries for no fixed time (`MaxRetentionSec=0`). journald also limits how many lines one service may write in a time: by default 10,000 lines in 30 seconds (`RateLimitBurst=` and `RateLimitIntervalSec=`), multiplied by a factor that grows with the disk space free for the journal (journald.conf(5) has a table of it). It drops the lines over the limit until the 30 seconds are over, and writes a line with the number it dropped. A distribution may change these defaults: `systemd-analyze cat-config systemd/journald.conf` shows the settings in force, and `sudo journalctl --disk-usage` the space the journal takes now. After you change a setting, run `sudo systemctl restart systemd-journald`.

**macOS.** Rotate the log file with a second launchd job that copies it and then cuts it to nothing, while the server goes on running. A rotation that moves the file away does not work here: the server keeps writing to the file it has open, wherever it is moved, until it starts again, and after a start it refuses new leases while it reclaims the leases of running apps: for the server license's lease time (10 minutes with the default policy), longer when leases of the run before can still be valid, and shorter when every seat is reclaimed ([Floating licenses](../explanation/floating-licenses.md)). Cutting the file works because launchd opens the log file for appending: after the cut, the server's next line goes to the start of the file. In the folder with the downloaded files, create the job's plist, `elm-server-log.plist`:

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

Every 600 seconds, launchd runs the command as root: when the log file holds 10,485,760 bytes (10 MiB) or more, it renames the copies `elm-server.log.0` to `.5` one number up, so the oldest, `.6`, is replaced, copies the log file to `elm-server.log.0` with its owner and mode, and cuts the log file to nothing. Since the job checks only every 600 seconds, each of the eight files, the log file and its seven copies, can grow to 10 MiB plus what the server writes in 600 seconds: the log takes at most about 80 MiB plus eight times that. The lines that the server writes between the copy and the cut are lost: in 20 rotations of a small log file on macOS 26 (the size to wait for set to 1 byte), with a program that wrote a line every millisecond, none was lost in 17 and one in 3. LOSS-10MIB Install the plist where only root may write, and load it:

```shell
sudo install -m 0644 elm-server-log.plist /Library/LaunchDaemons/
sudo launchctl bootstrap system /Library/LaunchDaemons/elm-server-log.plist
```

The job does not run when it is loaded, only every 600 seconds after. To check now, run `sudo launchctl kickstart system/elm-server-log`; it rotates the file only when it holds 10 MiB or more. Do not move or delete the log file itself: launchd cannot create it again in a folder that only root may write, and at its next start the server does not start, with `last exit code = 78: EX_CONFIG` in `sudo launchctl print system/elm-server`.

**Windows.** The event log's settings decide the Application log's maximum size and what happens when it is full, which `wevtutil gl Application` shows as `maxSize` in bytes (20,971,520 on a Windows 11 machine) and `retention`, with which `false` lets new events overwrite the oldest ones and `true` keeps the old events and discards the new ones ([wevtutil](https://learn.microsoft.com/windows-server/administration/windows-commands/wevtutil)), and which an administrator changes with `wevtutil sl Application /ms:<bytes>`.

## When the server keeps stopping

When the server cannot start, for example because its license expired or a setting is wrong, it ends with exit code 1 or 2 and one line that starts with `error:`. On Linux and macOS the service manager takes that as a failure and starts the server again, and it stops again: systemd every 5 seconds, and launchd every 10 seconds. On Windows, a start that stops before the server listens makes `Start-Service` report an error. A start that stops later, because the server cannot listen on its address or its check of the TLS certificate's intermediate CAs fails, shows the service as running and then ended without a reported stop, which `sc.exe failure` takes as a failure. The `error:` line is in the journal (`sudo journalctl -u elm-server`), in `/Library/Logs/elm-server/elm-server.log`, or in event ID 50 of the Application event log. Stop the service, correct the cause, and start it again. For an expired license, see [Renew a server license](renew-a-server-license.md), or [Activate a server license with elm-service](activate-a-server-license-with-elm-service.md) for a license that renews from the vendor's service.

## Remove the service

| Linux | macOS | Windows |
| --- | --- | --- |
| `sudo systemctl disable --now elm-server`, then `sudo rm /etc/systemd/system/elm-server.service` and `sudo systemctl daemon-reload` | `sudo launchctl bootout system/elm-server` and `sudo launchctl bootout system/elm-server-log`, then `sudo rm /Library/LaunchDaemons/elm-server.plist /Library/LaunchDaemons/elm-server-log.plist` | `Stop-Service elm-server`, then `sc.exe delete elm-server` |

The data folder keeps the server's installation key and its license; delete it only when you no longer need the activation, and if the server was activated with `elm-service`, free its activation first with `elm server deactivate`, as the service's account (on Windows, in PowerShell opened with "Run as administrator"; [Activate offline, and move a server](activate-a-server-license-with-elm-service.md#activate-offline-and-move-a-server)).
