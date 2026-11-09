---
sidebar_position: 21
---

# Sign in with OIDC

This guide is for the vendor's operator who lets administrators sign in to `elm-service` with their identity provider (IdP) through OpenID Connect (OIDC), instead of handing out API keys. With OIDC, `elm admin login` signs an administrator in from the command line, the admin UI signs them in from a browser, and the IdP's groups decide their roles. The guide starts Keycloak in a container as the IdP to try it, writes its settings, turns OIDC on in the service of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md), and then says what to set in Microsoft Entra ID and what to keep in mind with any IdP. It runs on a Linux host with Docker; on Windows or macOS, follow it in a Linux virtual machine, or in a Linux distribution of WSL with Docker in it.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`, on a Linux host with Docker. If you followed the tutorial on Windows or macOS, `tools` needs the tutorial's Linux archives there.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). Do it before OIDC is on: afterwards `setup` makes keys for the new owner below.

Keycloak listens on port 8180 of the host here. The service keeps listening on `http://127.0.0.1:8080`: type `127.0.0.1`, not `localhost`, as in the tutorial, since the IdP accepts only the addresses registered for the service.

## Write the IdP's settings

Keycloak reads a realm, the IdP's settings for one organization, from a JSON file when it starts. Create a folder named `import` in the tutorial folder, and in it a file named `elm-realm.json` with this content. The password and the client secret in it are for trying only:

<!-- steps:file path=import/elm-realm.json -->
```json
{
  "realm": "elm",
  "enabled": true,
  "accessTokenLifespan": 300,
  "groups": [
    {
      "name": "elm-owners"
    }
  ],
  "users": [
    {
      "id": "6f1c2a4e-5b7d-4e8f-9a0b-1c2d3e4f5a6b",
      "username": "alice",
      "enabled": true,
      "email": "alice@example.com",
      "emailVerified": true,
      "firstName": "Alice",
      "lastName": "Example",
      "requiredActions": [],
      "credentials": [
        {
          "type": "password",
          "value": "alice-try-only",
          "temporary": false
        }
      ],
      "groups": ["/elm-owners"],
      "realmRoles": ["default-roles-elm"]
    }
  ],
  "clients": [
    {
      "clientId": "elm-cli",
      "name": "elm command-line tool",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "consentRequired": false,
      "attributes": {
        "oauth2.device.authorization.grant.enabled": "true"
      },
      "protocolMappers": [
        {
          "name": "elm-service audience",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.custom.audience": "elm-service",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        },
        {
          "name": "groups",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-group-membership-mapper",
          "config": {
            "claim.name": "groups",
            "full.path": "false",
            "access.token.claim": "true",
            "id.token.claim": "true",
            "userinfo.token.claim": "false"
          }
        }
      ]
    },
    {
      "clientId": "elm-web",
      "name": "elm-service admin UI",
      "enabled": true,
      "publicClient": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "elm-web-try-only",
      "standardFlowEnabled": true,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "consentRequired": false,
      "fullScopeAllowed": false,
      "redirectUris": ["http://127.0.0.1:8080/auth/callback"],
      "webOrigins": [],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "http://127.0.0.1:8080/"
      },
      "protocolMappers": [
        {
          "name": "groups",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-group-membership-mapper",
          "config": {
            "claim.name": "groups",
            "full.path": "false",
            "access.token.claim": "true",
            "id.token.claim": "true",
            "userinfo.token.claim": "false"
          }
        }
      ]
    }
  ]
}
```

What each part is for:

- The group `elm-owners` holds the administrators who get the `owner` role. The user `alice` is in it. Her `id` is fixed here so that the service's settings below can name her; Keycloak puts it in her tokens as `sub`. `realmRoles` gives her the realm's default roles, which a user created by an import does not get otherwise; without `offline_access` among them, Keycloak refuses the refresh token that `elm admin login` asks for.
- `elm-cli` is the client of `elm admin login`: a public client, with no secret, that uses the device authorization flow only. Its audience mapper puts `elm-service`, the value of the service's `oidc.audience`, in the access token's `aud`; the service refuses an access token without it. Its `name` is what Keycloak shows on the approval page, so that an administrator sees which program asks.
- `elm-web` is the client of the admin UI: a confidential client with a secret, that uses the authorization code flow with PKCE only. Its redirect URI and the address after sign-out are the service's address with `/auth/callback` and `/`, exactly, without wildcards. `fullScopeAllowed` off keeps the realm's other roles out of its tokens.
- Both clients put the user's groups in a top-level `groups` claim with plain group names (`full.path` off), in the access token and in the ID token, since the service reads the roles from that claim.

## Start Keycloak

Create the Keycloak container, copy the `import` folder into it, and start it. Keycloak imports the realm when it starts:

<!-- steps:run -->
```shell
docker create --name elm-keycloak -p 127.0.0.1:8180:8080 quay.io/keycloak/keycloak@sha256:27b3230fbabb8c9ff3c66c864cf09dcd8a41aca82e5100679d109ef0eeb05741 start-dev --import-realm
docker cp import elm-keycloak:/opt/keycloak/data/
docker start elm-keycloak
docker exec elm-keycloak bash -c 'for i in $(seq 120); do exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /realms/elm/.well-known/openid-configuration HTTP/1.0\r\n\r\n" >&3 && head -n 1 <&3 | grep -q " 200 " && exit 0; sleep 1; done; exit 1'
```

`start-dev` runs Keycloak for development, with plain http and data that is gone with the container. While Keycloak starts, the last line prints `Connection refused` once a second; that is expected. It waits until Keycloak answers for the realm, which takes about half a minute. The service reads the IdP's settings only when a sign-in first needs them: before Keycloak answers, a sign-in fails with `The identity provider cannot be reached.`, and the next one tries again.

## Turn OIDC on in the service

The admin UI's sign-in needs two secrets. Make the key encryption key, which protects the sign-in's keys in the database, with the same command as the nonce secret:

<!-- steps:run -->
```shell
elm secret create --out kek.secret
```

Put the web client's secret, the value of `secret` in the realm, into a file:

<!-- steps:file path=web-client.secret -->
```text
elm-web-try-only
```

In production, let the IdP make the client's secret, and keep both files where only the service's account can read them, as the [Secrets](../reference/service-configuration.md#secrets) of the configuration say.

Replace the content of `service.json` with the tutorial's settings and these additions:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "http://127.0.0.1:8180/realms/elm",
    "sub": "6f1c2a4e-5b7d-4e8f-9a0b-1c2d3e4f5a6b"
  },
  "oidc": {
    "issuer": "http://127.0.0.1:8180/realms/elm",
    "audience": "elm-service",
    "cliClientId": "elm-cli",
    "roleMap": {
      "elm-owners": "owner"
    },
    "webClientId": "elm-web",
    "webClientSecretFile": "web-client.secret"
  },
  "dataProtection": {
    "kekFile": "kek.secret"
  }
}
```

- `oidc.issuer` is the realm's address, which the service compares with the tokens' `iss` as text. Plain `http` is allowed only on a loopback address, as here.
- `oidc.audience` is the value the access token's `aud` must hold, and differs from both client IDs.
- `oidc.roleMap` gives the role `owner` to the members of `elm-owners`. The roles are `owner`, `admin`, `support`, and `viewer`.
- `bootstrapOwner` now names `alice`: with OIDC on, its `iss` must equal `oidc.issuer`, or the service does not start. The bootstrap owner always signs in as `owner`, whatever `roleMap` says, and `elm-service setup` makes keys for that owner, so that an owner locked out of the IdP can still get in.
- Leave `publicUrls` out: it takes `https` addresses only, and the admin UI's address is then `http://127.0.0.1:8080/`.

Press Ctrl+C in the second terminal, and start the service there again; `ELM_SIGNING_PASSPHRASE` is still set in that terminal:

<!-- steps:background ready=http://127.0.0.1:8080/auth/login -->
```shell
elm-service --config service.json
```

At this start, the log shows a warning `No XML encryptor configured. Key {…} may be persisted to storage in unencrypted form.` and the line `A Data Protection key was made: 1.`: the service encrypts the sign-in's keys with the key encryption key itself before it stores them, so the framework's own encryptor is not used. `/auth/login` sends a browser to Keycloak's sign-in page. That the page answers shows that the service reads the realm, and that Keycloak knows `elm-web` and its redirect URI: with another redirect URI, Keycloak answers with an error page instead.

The service tells `elm` where to sign in:

<!-- steps:run capture=oidc-client pattern="(elm-cli)" -->
```shell
elm admin request GET /v1/admin/oidc --url http://127.0.0.1:8080
```

`elm` prints the issuer, the client ID `elm-cli`, and the scopes:

```text
{"issuer":"http://127.0.0.1:8180/realms/elm","clientId":"elm-cli","scopes":["openid","offline_access"]}
```

The tutorial's API key still works. List the administrators with it:

<!-- steps:run capture=tutorial-owner pattern="(tutorial)" -->
```shell
elm admin request GET /v1/admin/admins --url http://127.0.0.1:8080
```

The list holds the tutorial's owner, `https://idp.example` `tutorial`. `alice` appears, with her `sub` `6f1c2a4e-5b7d-4e8f-9a0b-1c2d3e4f5a6b` in `subject`, once she has signed in.

## Sign in with elm

Remove `ELM_API_KEY` from the first terminal, since `elm` prefers it to a sign-in: `Remove-Item Env:ELM_API_KEY` in PowerShell, `unset ELM_API_KEY` in Bash or zsh. Then sign in:

```shell
elm admin login --url http://127.0.0.1:8080
```

`elm` prints an address of Keycloak and a code on standard error, and also an address with the code in it, and waits. Open the address in a browser, enter the code if asked, sign in as `alice` with the password `alice-try-only`, and check that the page that asks you to grant access names `elm command-line tool` before you choose Yes. `elm` prints `Signed in to http://127.0.0.1:8080/.`, saves the sign-in, in place of any earlier one, and `elm admin request` uses it for this service until it ends: try `elm admin request GET /v1/admin/me --url http://127.0.0.1:8080`, which shows her `sub` in `subject` with the role `owner`. Stay signed in for the sections below; `elm admin logout` ends the sign-in, and [Environment variables](../reference/elm-cli.md#environment-variables) in the `elm` reference says where it is saved.

Never type a code that you did not ask for yourself: someone who sends you a code and an address is asking you to sign them in.

## Sign in to the admin UI

Open `http://127.0.0.1:8080/` in a browser, and sign in as `alice`. The service keeps the sign-in in a session cookie, which ends after `session.idleMinutes` (30) without a request, or after `session.maxHours` (12). Signing out of the admin UI ends the service's session and opens Keycloak's sign-out page, which asks you to confirm; Keycloak's session ends only if you do, so on a shared browser always confirm. [Security model](../explanation/security-model.md#around-the-hosted-service-and-its-administrators) lists what else a sign-in does not cover.

This guide has not been tried in a browser. Over plain `http` on the loopback address, the service and Keycloak both set cookies marked `Secure`, so a browser that does not send such cookies back over `http` cannot sign in here. The service accepted the sign-in over `http://127.0.0.1:8080` from a client that sends them back.

In production, serve the admin UI over https: put TLS in front of the service, or give it [`tls`](../reference/service-configuration.md#tls), and set `publicUrls` to the https address, which the redirect URI then uses.

## Retire the tutorial's owner

Once `alice` has signed in, the tutorial's owner is no longer needed. Its API key expires anyway 90 days after `setup` made it. Do this in production; in this guide, skip it if you will go back to the tutorial's service and its other how-to guides afterwards: once the owner is disabled and OIDC is off again, no API key gets into the tutorial's service, and `setup` refuses a disabled bootstrap owner, so only starting the tutorial again from the beginning helps. Signed in as `alice`, find the owner's `id` in `GET /v1/admin/admins`, and disable it:

```shell
elm admin request POST /v1/admin/admins/<id>/disable --url http://127.0.0.1:8080
```

`elm` prints nothing when it succeeds; `GET /v1/admin/admins` then shows the owner with `"enabled":false`. Its API keys, the tutorial's key included, stop working at once. An owner can disable another owner, but never the bootstrap owner, themselves, or the last enabled owner.

## Microsoft Entra ID

With Microsoft Entra ID, register two applications in your tenant: one for `elm admin login`, a public client that uses the device authorization flow, and one for the admin UI, a confidential client with a secret and the redirect URI `<your address>/auth/callback`. Expose an API scope on a third application that stands for the service, put that scope in `oidc.cliScopes` beside `openid` and `offline_access`, and set `oidc.audience` to that application's client ID, which v2.0 access tokens carry in `aud` (see Microsoft's [access token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference)). Then:

- `oidc.issuer` is the issuer of your tenant: v2.0 tokens carry an `iss` that names your tenant and ends in `/v2.0` (same reference).
- In the manifest of the API's application, set `api.requestedAccessTokenVersion` to `2`. Otherwise Entra ID issues v1.0 access tokens, whose `iss` does not end in `/v2.0` and so differs from `oidc.issuer` (see Microsoft's [app manifest reference](https://learn.microsoft.com/en-us/entra/identity-platform/reference-microsoft-graph-app-manifest#api-attribute)).
- Set `groupMembershipClaims`, for example to `SecurityGroup`, in the manifest of the applications of the admin UI and of the service, so that both tokens carry `groups`. The claim holds the groups' object IDs, so the keys of `oidc.roleMap` are those IDs. When a user is in more than 200 groups, Entra ID leaves the claim out of the token, and the service then sees no role for that user: keep administrators in few groups.
- Set `oidc.subjectClaim` to `oid`. Entra ID's `sub` differs between two applications for the same user, while `oid` is the same in every application of the tenant (Microsoft's access token claims reference and [ID token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference)). The admin UI and `elm admin login` use two applications, so with `sub` one person would become two administrators.
- Entra ID puts `oid` in a token only when the request asks for the `profile` scope (both references say so), so add `profile` to `oidc.cliScopes` and set `oidc.webScopes` to `openid profile`.
- `bootstrapOwner.sub` is then the first owner's `oid`: the object ID of the user in your tenant.

This guide has not been tried with Entra ID; check each setting against Microsoft's documentation of your tenant.

## Change the identity claim

`oidc.subjectClaim` names the claim whose value identifies an administrator, `sub` by default. The service does not move administrators from one claim to another: after a change, each person becomes a new administrator at their next sign-in. To change it on a running service, such as from `sub` to `oid` for Entra ID:

1. In the same restart that changes `oidc.subjectClaim`, change `bootstrapOwner.sub` to the first owner's value of the new claim.
2. Sign in, and make the API keys again as the new administrator. The keys of an old administrator stop working after `apiKeys.inactiveAdminDays`, because nobody signs in as that administrator any more.
3. Disable the old administrators with `POST /v1/admin/admins/<id>/disable`, as in [Retire the tutorial's owner](#retire-the-tutorials-owner).

## Settings to keep in mind with any IdP

- Show the client's name on the approval page of the device flow, so that an administrator sees which program asks.
- Keep `oidc.audience` different from both client IDs; the service refuses to start otherwise.
- Put the role claim, and the claim of `oidc.subjectClaim`, in both the ID token and the access token, with the same value.
- Allow the admin UI's client the authorization code flow only, and turn off everything else for it, such as service accounts and direct grants in Keycloak (`fullScopeAllowed: false` keeps unrelated roles out of its tokens).
- Keep the IdP's token lifetimes short: the service checks access tokens itself and does not ask the IdP, so a signed-out token stays valid until it expires.
- `apiKeys.inactiveAdminDays` (90) stops the API keys of an administrator who has not signed in for that many days.

## Stop Keycloak

The container of this guide is for trying only, and `start-dev` keeps its data in the container. Run `elm admin logout` and stop the service first, then stop the container and remove it: `docker stop elm-keycloak`, then `docker rm -v elm-keycloak`. Delete `kek.secret` and `web-client.secret` too, remove the `oidc` and `dataProtection` settings from `service.json`, and put back the tutorial's `bootstrapOwner` (`https://idp.example`, `tutorial`) to go back to the tutorial's service; then read the tutorial's API key into `ELM_API_KEY` again, or, without it, make a new one with `elm-service setup --config service.json` as in [Before you start](#before-you-start). This works only if you did not disable the tutorial's owner. Delete the `import` folder too: its file holds the trial password and client secret.

## See also

- [`oidc`](../reference/service-configuration.md#oidc), [`dataProtection`](../reference/service-configuration.md#dataprotection), and [`session`](../reference/service-configuration.md#session) in the `elm-service` configuration.
- [`elm admin login`](../reference/elm-cli.md#elm-admin-login) and [`elm admin logout`](../reference/elm-cli.md#elm-admin-logout).
- [Security model](../explanation/security-model.md): what a sign-in protects, and what it does not.
