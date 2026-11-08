---
sidebar_position: 1
---

# Set up a policy

This guide is for a vendor who runs `elm-service` and wants to decide the rules of the licenses they sell: how many devices may use one license, how long an app keeps working when it cannot refresh its license file, and whether the license is bound to devices or lends floating seats. A policy holds these rules, and every license made under it follows them. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, and the service has a product with a feature. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [Activation](../explanation/activation.md) explains products, policies, and licenses.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the files below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

In the outputs below, the IDs, the license key, and the times are fake. On your machine they are different every time.

## Choose the settings

Every setting has a default, so a policy needs only a product and a name. Decide these settings for what you sell; [Policy settings](../explanation/activation.md#policy-settings) lists all of them, and [`POST /v1/admin/policies`](../reference/administration-api.md#post-v1adminpolicies) gives their ranges.

| You decide | Setting | Default |
| --- | --- | --- |
| How many devices may hold an activation of one license at once | `maxActivations` | 1 |
| How long a license lasts, and whether that time starts when the license is created or at its first activation | `duration`, `expirationBasis` | No expiry, `fromIssue` |
| How long an app keeps working after its license file expires, for example while it has no network | `gracePeriod` | 7 days |
| How long one license file is valid, and when the app refreshes it | `tokenLifetime`, `refreshWindow` | 30 days, 20 days before the end |
| How many times devices may deactivate a license, in total | `deactivationLimit` | No limit |
| Whether each device is activated (node-locked) or the service lends seats to the devices that run the app at the moment (cloud-floating) | `floating` | `none`: node-locked |

Times are in seconds. All of these settings can change later, as in [Change a policy](#change-a-policy).

## Create a node-locked policy

A node-locked policy binds each license to the devices that activate it. This one lets three devices use one license, for one year from its creation (31,622,400 seconds, 366 days), and lets an app keep working for 3 days (259,200 seconds) after its license file expires:

<!-- steps:file path=three-devices-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, 3 devices","maxActivations":3,"duration":31622400,"gracePeriod":259200}
```

<!-- steps:run capture=three-devices-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body three-devices-policy.json --url http://127.0.0.1:8080
```

`elm` prints the policy with every setting, the defaults included:

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Standard, 3 devices","licenseKind":"standard","binding":"nodeLocked","maxActivations":3,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":31622400,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":259200,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":1209600,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

Its `id` is `<three-devices-policy-id>` below. A license under it is activated like the license of the tutorial. Create one:

<!-- steps:file path=three-devices-license.json -->
```json
{"policyId":"<three-devices-policy-id>","name":"Second customer","features":[{"code":"pro"}]}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses --body three-devices-license.json --url http://127.0.0.1:8080
```

`elm` prints the license:

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Second customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":"2027-01-16T09:30:00Z","suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00001"}
```

Its `expiresAt` is 366 days after its creation, from the policy's `duration`, and its `maxActivations` is `null`: the policy's value applies. A license can set its own value instead, such as `"maxActivations":5` in its file, for a customer who bought more devices.

## Create a cloud-floating policy

With `floating` `cloud`, licenses are not activated. Instead, the service lends each device that runs the app a lease on a seat, for a short time that the app renews while it runs, and a license has a number of seats for each feature. [Floating licenses](../explanation/floating-licenses.md) explains leases. This policy makes leases of 10 minutes (600 seconds, the default):

<!-- steps:file path=floating-policy.json -->
```json
{"productId":"<product-id>","name":"Floating","floating":"cloud","leaseDuration":600}
```

<!-- steps:run capture=floating-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body floating-policy.json --url http://127.0.0.1:8080
```

Its `id` is `<floating-policy-id>` below. Each feature of a license under it needs `seats`, the number of devices that may hold a lease at once. Create a license with 5 seats of `pro`:

<!-- steps:file path=floating-license.json -->
```json
{"policyId":"<floating-policy-id>","name":"Team","features":[{"code":"pro","seats":5}]}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses --body floating-license.json --url http://127.0.0.1:8080
```

`elm` prints the license, with `"seats":5` in its feature:

```text
{"id":"01960000-0000-7000-8000-000000000303","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000204","name":"Team","features":[{"code":"pro","ver":null,"seats":5,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00002"}
```

An app takes leases with `LicenseClient`, not with `ActivationClient`; the [.NET API](../reference/dotnet-api.md) describes both.

## Change a policy

Send only the settings you change. This gives the node-locked policy five devices and a grace period of 7 days (604,800 seconds):

<!-- steps:file path=policy-change.json -->
```json
{"maxActivations":5,"gracePeriod":604800}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/policies/<three-devices-policy-id> --body policy-change.json --url http://127.0.0.1:8080
```

`elm` prints the changed policy:

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Standard, 3 devices","licenseKind":"standard","binding":"nodeLocked","maxActivations":5,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":31622400,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":604800,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":1209600,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:31:00Z"}
```

A change applies to every license under the policy from the next license file or lease that the service issues; the files and leases already issued keep their values until they expire. A new `duration` applies only to licenses created after the change, and licenses that already have an expiry keep it. `productId`, `licenseKind`, and `binding` cannot change: the service answers `ELM_INVALID_ARGUMENT`. To change those, create another policy, and create new licenses under it.

To see the policies of your product, with their settings:

<!-- steps:run -->
```shell
elm admin request GET "/v1/admin/policies?productId=<product-id>" --url http://127.0.0.1:8080
```

The answer is one line of JSON with the policies in `items`. Each request is in the service's audit log, as [Audit log](../reference/administration-api.md#audit-log) describes.

## See also

- [Policy settings](../explanation/activation.md#policy-settings): what each setting decides.
- [Policies](../reference/administration-api.md#policies) in the administration API: every member, its range, and the settings that must fit together.
- [Offer a trial](offer-a-trial.md): a policy for trials.
