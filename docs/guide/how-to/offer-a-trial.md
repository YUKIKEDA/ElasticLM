---
sidebar_position: 2
---

# Offer a trial

This guide is for a vendor who runs `elm-service`, wants a product to offer a trial, and wants to let a machine that already had a trial start one again, for example when a customer asks to try a new version. A trial is a license for a limited time that a device gets without a license key, once per machine. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, the service has a product with a feature, and you have the app of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md), which sets all of this up and starts a trial on your machine; [Trials](../explanation/activation.md#trials) explains how the service gives them.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps run the tutorial's app in the folder `app`, with the root public key `root.pub.pem`.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the files below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

In the outputs below, the IDs, the fingerprints, and the times are fake. On your machine they are different every time.

## Create a trial policy

A trial is a license under the product's trial policy, a policy whose `licenseKind` is `trial`. Choose how long a trial lasts with `trialDuration`, in seconds: from 1 day (86,400) to 365 days (31,536,000), 14 days by default. This one lasts 30 days (2,592,000 seconds). A trial policy has no grace period, so `gracePeriod` is 0:

<!-- steps:file path=month-trial-policy.json -->
```json
{"productId":"<product-id>","name":"Trial, 30 days","licenseKind":"trial","gracePeriod":0,"trialDuration":2592000}
```

<!-- steps:run capture=month-trial-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body month-trial-policy.json --url http://127.0.0.1:8080
```

`elm` prints the policy:

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Trial, 30 days","licenseKind":"trial","binding":"nodeLocked","maxActivations":1,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":null,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":0,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":2592000,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

A trial policy is node-locked, and it compares the machine's fingerprints (`fingerprintMatching` `any`, the default, or `all`): the service refuses other values with `ELM_INVALID_ARGUMENT`. It needs no list of features, since a trial gets every feature that the product has when the trial starts.

## Offer the trial

The product offers a trial only while its `trialPolicyId` names a trial policy. Set it to the policy's `id`, `<month-trial-policy-id>` here:

<!-- steps:file path=offer-trial.json -->
```json
{"trialPolicyId":"<month-trial-policy-id>"}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body offer-trial.json --url http://127.0.0.1:8080
```

`elm` prints the product with its new `trialPolicyId`:

```text
{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":"01960000-0000-7000-8000-000000000203","createdAt":"2026-01-15T09:30:00Z"}
```

From now on, an app that calls `StartTrialAsync` gets a trial of 30 days. A product names one trial policy at a time; a trial already running keeps the end it got when it started.

## Start a trial on a machine that had one

Start a trial with the tutorial's app, in a new data folder, `trial-data`. To the service it is a new installation of the app, but on the same machine:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem trial-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_USED: A fingerprint of this installation already had a trial of the product.
```

The service keeps a record of each trial with the fingerprints of the machine that had it, and refuses a second trial of the product to a machine with any of those fingerprints, whichever data folder or installation key asks. This machine had a trial in the tutorial. On a machine that never had a trial of the product, this run succeeds at once and prints `trial: pro`; follow the steps below all the same: they delete the record of that trial and start another one in a second data folder.

## See the trials

List the trial records of your product:

<!-- steps:run capture=trial-id pattern="\x22id\x22:\x22([0-9a-f]{8}-[0-9a-f-]{27})\x22" -->
```shell
elm admin request GET "/v1/admin/trials?productId=<product-id>" --url http://127.0.0.1:8080
```

`elm` prints a page of records:

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","fingerprints":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4","9b1e7c3a5d2f4e6081a7c9b3d5e7f901"],"createdAt":"2026-01-15T09:30:00Z"}]}
```

The `id` of a record is the ID of its trial license, `<trial-id>` below. `createdAt` is when the trial started. A list with more records than fit on one page has `next`; [Paging](../reference/administration-api.md#paging) explains how to get the rest. With many records, find the machine's record by the time its trial started, or by the machine's fingerprints if your app shows them ([Offline only](../explanation/overview.md#offline-only) names the calls that give them).

## Let the machine start a trial again

Delete the machine's record:

<!-- steps:run -->
```shell
elm admin request DELETE /v1/admin/trials/<trial-id> --url http://127.0.0.1:8080
```

The service answers with no body, so `elm` prints nothing. Deleting the record does not end the earlier trial: its license and its activation stay. Deactivating that activation, as in [Deactivate as the administrator](deactivate-a-device.md#deactivate-as-the-administrator) with the trial's ID as the license ID, frees its place and refuses its next refresh, but the trial's license file on the device stays valid until it expires.

Start the trial again, in a second new data folder, `second-trial-data`. A data folder that keeps an activation refuses to start a trial (`ELM_INVALID_STATE`), and on a machine whose first run above succeeded, `trial-data` keeps that trial:

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem second-trial-data
```

The app prints two lines. The first one is:

<!-- steps:expect -->
```text
trial: pro
```

The second line is the end of the new trial, 30 days from now under the new trial policy, in UTC, such as `expires: 2026-02-14 09:30:00Z`. The service records this trial too, so the machine gets no third trial unless you delete the new record.

The product keeps offering the trial. To stop, set its `trialPolicyId` to `null` with the same `PATCH`: an app that asks for a trial then gets `ELM_TRIAL_NOT_OFFERED`, and so does an app whose product ID names no product. Trials that already run go on until they end, and the records stay.

## See also

- [Trials](../explanation/activation.md#trials): how the service gives trials, and why a new machine ID gets a new trial.
- [`GET /v1/admin/trials`](../reference/administration-api.md#get-v1admintrials) and [`DELETE /v1/admin/trials/{id}`](../reference/administration-api.md#delete-v1admintrialsid) in the administration API.
- [Error codes](../reference/error-codes.md): `ELM_TRIAL_USED`, `ELM_TRIAL_NOT_OFFERED`, and `ELM_FINGERPRINT_UNAVAILABLE`.
