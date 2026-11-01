---
sidebar_position: 10
---

# C API

This page lists every function, type, and constant of `libelasticlm`, the C library of ElasticLM, for developers of C and C++ apps. The library offers the same license verification, activation, and leases as the .NET packages behind a C ABI; read the [Overview](../explanation/overview.md) first for what each of them does. A release publishes on the [GitHub Releases page](https://github.com/YUKIKEDA/ElasticLM/releases) one archive per platform, `libelasticlm-<version>-<rid>.zip` for Windows and `libelasticlm-<version>-<rid>.tar.gz` for Linux and macOS, that holds the library, the header `elasticlm.h`, and `LICENSE`; a separate `-symbols` archive holds the debug symbols. The comments in `elasticlm.h` are the full reference for every argument, and the copy in the archive is the one that matches your library. The header of the `main` branch is at <https://github.com/YUKIKEDA/ElasticLM/blob/main/include/elasticlm.h>.

## Rules for every function

These rules are stated at the top of `elasticlm.h` and hold for every function unless the function's own comment names an exception.

### The library file

- The library is `elasticlm.dll` on Windows, `libelasticlm.so` on Linux, and `libelasticlm.dylib` on macOS. It is built for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`.
- It is a NativeAOT library and cannot be unloaded: do not call `FreeLibrary` or `dlclose` on it.
- `ELM_FUNCTION` declares each function and also a pointer type to it named after the function with `_fn` (`elm_verifier_create_fn`), for programs that load the library at run time with `LoadLibrary` and `GetProcAddress` or `dlopen` and `dlsym`. The Windows archive holds no import library (`.lib`).

### Strings

- Strings are UTF-8 and end with NUL, in both directions.
- A string you pass must be valid UTF-8. A PEM may be at most 16 KiB and any other string at most 1 KiB before its NUL. A longer string, or one that is not valid UTF-8, is `ELM_INVALID_ARGUMENT`.
- License meta keys and values may hold NUL, so `elm_license_meta_at` and `elm_license_meta_get` also give their lengths in bytes.

### Errors

- The functions under "Functions that return a number" in the header return an error number. `0` (`ELM_OK`) is success; every other number is one of the [error codes](error-codes.md).
- Each of them empties this thread's error message when it starts and writes it when it fails. `elm_last_error_message` returns that message: empty after a success, and valid until the next such call on the same thread.
- On every failure they set their outputs to `NULL` and `0`. An `elm_feature` with a right `struct_size` gets its other members set to `NULL` and `0`. Two outputs are exceptions: `retry_after_seconds` gets the time to wait on a failure, and `*needed` of `elm_activation_id` gets the size when the buffer is too small.
- A `NULL` output argument is `ELM_INVALID_ARGUMENT`.
- The functions under "Functions that return a value" never fail. With a `NULL` handle they return `NULL`, `0`, or `-1`, as the tables below say, and they do not change the error message.
- No .NET exception leaves the library. An internal failure is `ELM_INTERNAL_ERROR` (`99`).

### Structs

- Every struct you pass starts with `struct_size`. Set it to `sizeof` the struct; any other value is `ELM_INVALID_ARGUMENT`.
- Boolean members are `int32_t` and must be `0` or `1`.
- A zero-filled struct gives the defaults.

### Ownership and freeing

- The library frees what it returns, in the matching `_free` or `_destroy` function. These ignore `NULL`.
- Freeing a handle twice, or using it after it is freed, is undefined behavior. A handle of another type is refused where the library can tell.
- A string that a license returns is valid until `elm_license_free`. The ID and feature of a lease are valid until `elm_lease_free`.
- The text of an offline file is freed with `elm_string_free`, which clears it first: an activation request holds the license key.

### Threads and callbacks

- Handles may be used from several threads at once. A license may be read from several threads, but not freed while another thread reads it.
- The online calls of an activation, and acquiring, borrowing, releasing, and listing borrows on a client, wait until they end and cannot be cancelled. The header gives the longest wait of each.
- The callbacks an operation causes have returned before its function returns, and none starts after the handle's `_destroy` returns. Inside a callback, the blocking functions of the same handle return `ELM_INVALID_STATE` and its `_destroy` does nothing. [Callbacks](#callbacks) lists what each callback may call.
- Calling `elm_activation_destroy` or `elm_client_destroy` while another thread is inside a function of the same handle is undefined behavior.

### ABI version

`ELM_ABI_VERSION` is `1`. A new version may add functions and add members at the end of structs.

## Functions

Each table lists the functions in the order of the header. "Error number" means the function returns `int32_t` under the rules in [Errors](#errors).

### Installation and verification

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_installation_open` | Error number | Opens or creates this installation's key for a product and reads the machine fingerprints. When no OS ID can be read, the fingerprint list is empty and licenses with fingerprints fail with `ELM_FINGERPRINT_UNAVAILABLE`. |
| `elm_installation_thumbprint` | `const char*`; `NULL` for `NULL` | The thumbprint of the installation key. |
| `elm_verifier_create` | Error number | Makes a verifier from the vendor's root keys, the revoked key IDs, and a clock guard. The installation may be freed right after the call. |
| `elm_verifier_verify` | Error number | Verifies a license file against an `elm_requirement` and gives a new `elm_license`. A file larger than 65536 bytes is `ELM_MALFORMED`. The library keeps nothing of the file. |

### License readers

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_license_expires_at` | Error number | Gives the expiry in UNIX seconds, with `*has_value` `0` when the license does not expire. |
| `elm_license_feature` | Error number | Writes the feature at an index (`0` to count - 1) into an `elm_feature`. |
| `elm_license_meta_at` | Error number | Gives the meta pair at an index, in the license's order, with the lengths of the key and value. |
| `elm_license_meta_get` | Error number | Gives the value of a meta key and its length, or `*value` `NULL` (still `ELM_OK`) when the key is not there. |
| `elm_license_id` | `const char*`; `NULL` for `NULL` | The license ID. |
| `elm_license_product` | `const char*`; `NULL` for `NULL` | The product ID. |
| `elm_license_kind` | `int32_t`: an `ELM_KIND_*` value; `-1` for `NULL` | The license kind. |
| `elm_license_issued_at` | `int64_t`: UNIX seconds; `0` for `NULL` | When the license was issued. |
| `elm_license_grace_period_seconds` | `int64_t`; `0` for `NULL` | The grace period in seconds. |
| `elm_license_in_grace_period` | `int32_t`: `1` or `0`; `-1` for `NULL` | Whether the license is past its expiry but inside its grace period. |
| `elm_license_feature_count` | `size_t`; `0` for `NULL` | The number of features. |
| `elm_license_meta_count` | `size_t`; `0` for `NULL` | The number of meta pairs. |

### Activation

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_activation_create` | Error number | Makes the activation of this installation with the hosted activation service. |
| `elm_activation_activate` | Error number | Activates online with a license key and gives a new `elm_license`. |
| `elm_activation_start_trial` | Error number | Starts a trial online and gives a new `elm_license`. |
| `elm_activation_refresh` | Error number | Refreshes the kept activation online and gives a new `elm_license`. |
| `elm_activation_deactivate` | Error number | Deactivates this installation online. |
| `elm_activation_check` | Error number | Checks the kept activation offline for a kind and an optional feature and version, and gives a new `elm_license`. |
| `elm_activation_create_activation_request` | Error number | Makes the text of an offline activation request for a license key. `*replaced` is `1` when a waiting request was replaced. |
| `elm_activation_create_refresh_request` | Error number | Makes the text of an offline refresh request. |
| `elm_activation_create_deactivation_proof` | Error number | Makes the text of an offline deactivation proof. |
| `elm_activation_forget_deactivation_proof` | Error number | Forgets the kept deactivation proof. |
| `elm_activation_import_response` | Error number | Keeps the service's answer to the waiting offline request and gives a new `elm_license`. An answer over 262144 bytes is `ELM_MALFORMED`. |
| `elm_activation_id` | Error number | Copies the current activation ID (empty when none) into your buffer. `*needed` gets the size with the NUL, also when the buffer is too small. |
| `elm_activation_state` | `int32_t`: an `ELM_STATE_*` value; `-1` for `NULL` | The state of the activation. |

[Activation](../explanation/activation.md) explains online and offline activation, refresh, and trials.

### Client and leases

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_client_create` | Error number | Makes a lease client of an on-premises license server, or of the hosted service's leases when `license_key` is set. It does not contact the server. |
| `elm_client_acquire_lease` | Error number | Acquires a lease of a feature for one or more seats, gives a new `elm_lease`, and renews it in the background. |
| `elm_client_borrow_lease` | Error number | Borrows a lease for 1 to 3162240000 seconds and gives a new `elm_lease`. A borrow is not renewed and stays `ELM_LEASE_HELD` after its expiry. |
| `elm_client_release_lease` | Error number | Returns a lease this client made. A lease that is not a borrow is `ELM_LEASE_RELEASED` afterwards even when the return failed. |
| `elm_client_get_borrowed_leases` | Error number | Gives the saved borrows that are still valid as an array of new handles, freed with `elm_lease_list_free`. |
| `elm_client_reset_server_pin` | Error number | Deletes the pin of an on-premises server. `*deleted` is `1` when a pin was deleted; a client with a license key has no pin. |
| `elm_lease_id` | Error number | Gives the lease ID. |
| `elm_lease_feature_id` | Error number | Gives the lease's feature ID. |
| `elm_lease_expires_at` | Error number | Gives the expiry of the current lease token in UNIX seconds; a renewal moves it. |
| `elm_lease_seats` | `int32_t`; `-1` for `NULL` | The number of seats. |
| `elm_lease_is_borrow` | `int32_t`: `1` or `0`; `-1` for `NULL` | Whether the lease is a borrow. |
| `elm_lease_state` | `int32_t`: an `ELM_LEASE_*` value; `-1` for `NULL` | The state of the lease. |
| `elm_lease_last_error` | `int32_t`: an error number; `0` when none; `-1` for `NULL` | The error of the last failed renewal, reclaim, or release. It stays after a recovery. |

A lease handle can still be read after `elm_client_destroy`. [Floating licenses](../explanation/floating-licenses.md) explains leases, renewal, borrowing, and the server pin.

### Errors and version

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_last_error_message` | `const char*` | The message of the last function on this thread that returns an error number: empty after a success. |
| `elm_version` | `const char*` | The library's version, valid until the process ends. |

### Release

| Function | Returns | What it does |
| --- | --- | --- |
| `elm_installation_free` | `void` | Frees an installation. |
| `elm_verifier_destroy` | `void` | Frees a verifier. |
| `elm_license_free` | `void` | Frees a license and its strings. |
| `elm_activation_destroy` | `void` | Waits for running callbacks (no time bound), then stops the automatic refresh, waiting up to 5 seconds for it. |
| `elm_lease_free` | `void` | Frees a lease handle without returning the lease. |
| `elm_lease_list_free` | `void` | Frees the array of `elm_client_get_borrowed_leases` and its handles, with the same count. Freeing an element first is undefined behavior. |
| `elm_client_destroy` | `void` | Waits for running callbacks and for an operation in progress (no time bound), then returns the leases that are not borrows within 5 seconds. |
| `elm_string_free` | `void` | Clears and frees a string that a function says to free with it. |

## Handle types

Every handle is an opaque struct that the library allocates.

| Type | What it is | Made by | Freed by |
| --- | --- | --- | --- |
| `elm_installation` | This installation's key and the machine fingerprints for one product. | `elm_installation_open` | `elm_installation_free` |
| `elm_verifier` | A license verifier: the vendor's root keys and a clock guard. | `elm_verifier_create` | `elm_verifier_destroy` |
| `elm_license` | A verified license. Every string it returns is valid until it is freed. | `elm_verifier_verify` and the activation functions that give a license | `elm_license_free` |
| `elm_activation` | This installation's activation with the hosted service. | `elm_activation_create` | `elm_activation_destroy` |
| `elm_client` | A client of an on-premises license server or of the hosted service's leases. | `elm_client_create` | `elm_client_destroy` |
| `elm_lease` | A lease. Its state, expiry, and last error are read each time, since renewals change them. Freeing it does not return the lease. | `elm_client_acquire_lease`, `elm_client_borrow_lease`, `elm_client_get_borrowed_leases` | `elm_lease_free`, or `elm_lease_list_free` for a list |

## Callbacks

| Type | Signature | Called when |
| --- | --- | --- |
| `elm_state_callback` | `void (*)(void* user, int32_t previous, int32_t current, int32_t error, const char* message)` | The activation's state changes. `previous` and `current` are `ELM_STATE_*` values; `error` is `0` and `message` empty when there is no error. |
| `elm_lease_callback` | `void (*)(void* user, const char* lease_id, int32_t previous, int32_t current, int32_t error, const char* message)` | A lease changes state. `previous` and `current` are `ELM_LEASE_*` values; compare `lease_id` with `elm_lease_id`. A renewal that keeps failing reports `ELM_LEASE_RETRYING` to `ELM_LEASE_RETRYING` again with its error; an acquire and a renewal without an error report nothing. |

- The strings are valid during the call only.
- `elm_state_callback` runs on the thread that ends the operation that changed the state, which can be your own thread inside that call, or on a library thread for the automatic refresh. It may come before `elm_activation_create` returns, when a refresh is already due and you have no handle yet. Inside it, `elm_activation_state`, `elm_activation_id`, and `elm_activation_check` may be called.
- `elm_lease_callback` runs on a library thread: the one that ended a release's request, or the one of the automatic renewal. Inside it, the `elm_lease_*` functions, `elm_lease_free`, `elm_lease_list_free`, and `elm_client_reset_server_pin` may be called.
- Do not leave a callback by a C++ exception or `longjmp`, and do not take a lock that the thread calling into the library holds. Return soon, since a callback can hold a library thread. A thread that the callback starts is treated like any other thread.

## Structs

The members are in the order of the header. Each struct starts with `uint32_t struct_size`, set to `sizeof` the struct.

### `elm_installation_options`

Passed to `elm_installation_open`.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_installation_options)`. |
| `product_id` | `const char*` | Required. The product ID. |
| `scope` | `int32_t` | `ELM_SCOPE_USER` or `ELM_SCOPE_MACHINE`. |
| `directory` | `const char*` | `NULL` for the [default folder](../explanation/devices-and-proofs.md#where-the-key-is-stored) of the scope, or your own folder. |
| `create` | `int32_t` | `1`: create the key when there is none. `0`: no key is `ELM_INSTALLATION_KEY_UNREADABLE`. |

### `elm_verifier_options`

Passed to `elm_verifier_create`.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_verifier_options)`. |
| `root_keys_pem` | `const char* const*` | The vendor's root public keys in PEM: 1 to 16. |
| `root_key_count` | `size_t` | The number of root keys. |
| `revoked_key_ids` | `const char* const*` | Key IDs revoked by the vendor: 0 to 1024. |
| `revoked_key_count` | `size_t` | The number of revoked key IDs. |
| `installation` | `const elm_installation*` | `NULL`, or the installation whose key signs the clock guard's state. The verifier keeps its own copy. |
| `clock_guard_path` | `const char*` | The clock guard's state file. `NULL`: `clock` in the installation's folder, or no state without an installation. Without an installation the state file is not signed, so whoever can write it can reset the guard. |
| `clock_skew_seconds` | `int32_t` | `0` for the default, 300; otherwise 1 to 3600. |
| `reject_trial_as_standard` | `int32_t` | `0` (default): a standard requirement also accepts a trial license. `1`: it does not. |

[Clock rollback detection and its limits](../explanation/license-files-and-verification.md#clock-rollback-detection-and-its-limits) explains the clock guard.

### `elm_requirement`

Passed to `elm_verifier_verify`: what the app requires of the license.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_requirement)`. |
| `product_id` | `const char*` | Required. The product ID. |
| `kind` | `int32_t` | An `ELM_KIND_*` value. |
| `feature_id` | `const char*` | `NULL` when no feature is required. |
| `feature_version` | `const char*` | `NULL`, or `major.minor[.patch]`. |
| `installation` | `const elm_installation*` | `NULL` for a license that is not node-locked. It must be of the same product, and is used during the call only. |

### `elm_feature`

Written by `elm_license_feature`. You set `struct_size`; the library writes the other members. Its strings are valid until `elm_license_free`.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_feature)`, set by you. |
| `id` | `const char*` | The feature ID. |
| `version_range` | `const char*` | The licensed version range, or `NULL` when the feature has none. |
| `has_seats` | `int32_t` | `1` when the feature has a seat count. |
| `seats` | `int32_t` | The seat count, or `0`. |
| `has_expires_at` | `int32_t` | `1` when the feature has its own expiry. |
| `expires_at` | `int64_t` | The feature's expiry in UNIX seconds, or `0`. |

### `elm_activation_options`

Passed to `elm_activation_create`.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_activation_options)`. |
| `product_id` | `const char*` | Required. The product ID. |
| `service_url` | `const char*` | The URL of `elm-service`: `https`, or `http` on a loopback address. `NULL`: offline files only. |
| `root_keys_pem` | `const char* const*` | The vendor's root public keys in PEM: 1 to 16. |
| `root_key_count` | `size_t` | The number of root keys. |
| `revoked_key_ids` | `const char* const*` | Key IDs revoked by the vendor: 0 to 1024. |
| `revoked_key_count` | `size_t` | The number of revoked key IDs. |
| `scope` | `int32_t` | `ELM_SCOPE_USER` or `ELM_SCOPE_MACHINE`. |
| `directory` | `const char*` | `NULL` for the default folder of the scope, or your own folder. |
| `send_client_info` | `int32_t` | `1`: send the host and user names. |
| `app_version` | `const char*` | `NULL`, or the app's version, at most 64 bytes. |
| `clock_skew_seconds` | `int32_t` | `0` for the default, 300; otherwise 1 to 3600. |
| `reject_trial_as_standard` | `int32_t` | `0` (default): a standard requirement also accepts a trial license. |
| `on_state` | `elm_state_callback` | `NULL`, or the state callback. |
| `user` | `void*` | Passed to `on_state`. Make it ready before `elm_activation_create` and keep it valid until `elm_activation_destroy` returns. |

### `elm_client_options`

Passed to `elm_client_create`.

| Member | Type | Meaning |
| --- | --- | --- |
| `struct_size` | `uint32_t` | `sizeof(elm_client_options)`. |
| `product_id` | `const char*` | Required. The product ID. |
| `server_url` | `const char*` | Required. `https` for an on-premises server; with a license key, `https`, or `http` on a loopback address. |
| `license_key` | `const char*` | `NULL`: an on-premises server. Otherwise the license key for leases of the hosted service. |
| `root_keys_pem` | `const char* const*` | The vendor's root public keys in PEM: 1 to 16. |
| `root_key_count` | `size_t` | The number of root keys. |
| `revoked_key_ids` | `const char* const*` | Key IDs revoked by the vendor: 0 to 1024. |
| `revoked_key_count` | `size_t` | The number of revoked key IDs. |
| `expected_server_license_id` | `const char*` | `NULL`, or the server license to pin. Not allowed with `license_key`. |
| `scope` | `int32_t` | `ELM_SCOPE_USER` or `ELM_SCOPE_MACHINE`. |
| `directory` | `const char*` | `NULL` for the default folder of the scope, or your own folder. |
| `send_client_info` | `int32_t` | `1`: send the host and user names. |
| `app_version` | `const char*` | `NULL`, or the app's version, at most 64 bytes. |
| `clock_skew_seconds` | `int32_t` | `0` for the default, 300; otherwise 1 to 3600. |
| `reject_trial_as_standard` | `int32_t` | `0` (default): a standard requirement also accepts a trial license. |
| `on_lease` | `elm_lease_callback` | `NULL`, or the lease callback. |
| `user` | `void*` | Passed to `on_lease`. Keep it valid until `elm_client_destroy` returns. |

## Constants

### Error numbers

`elasticlm.h` defines a macro for every error code, with the code's name and number (`ELM_NO_SEATS` is `40`). The [error codes](error-codes.md) page lists them all with their meanings.

| Numbers | Group |
| --- | --- |
| `0` to `24` | [Success, checks, and signed requests](error-codes.md#success-checks-and-signed-requests-0-to-24) |
| `30` to `44` | [Licenses, activations, and leases](error-codes.md#licenses-activations-and-leases-30-to-44) |
| `50` to `56` | [Authentication, limits, and administration](error-codes.md#authentication-limits-and-administration-50-to-56) |
| `90` and `99` | [Network and internal errors](error-codes.md#network-and-internal-errors-90-and-99) |

### License kinds

| Macro | Value | Kind |
| --- | --- | --- |
| `ELM_KIND_STANDARD` | `0` | A standard license. |
| `ELM_KIND_TRIAL` | `1` | A trial license. |
| `ELM_KIND_SERVER` | `2` | A server license for `elm-server`. |

### Key scopes

Where the installation key is kept.

| Macro | Value | Scope |
| --- | --- | --- |
| `ELM_SCOPE_USER` | `0` | The user scope. |
| `ELM_SCOPE_MACHINE` | `1` | The machine scope. |

### Activation states

| Macro | Value | State |
| --- | --- | --- |
| `ELM_STATE_NONE` | `0` | No activation is kept. |
| `ELM_STATE_ACTIVE` | `1` | An activation is kept and its last refresh, if any, succeeded. |
| `ELM_STATE_REFRESH_FAILING` | `2` | Refreshes fail; the kept license file is used until it ends. |
| `ELM_STATE_REFUSED` | `3` | The service refused the license: revoked, suspended, or expired. The kept file is used until it ends. |
| `ELM_STATE_CLONE_DETECTED` | `4` | The service found another copy of this installation. The kept file is used until it ends. |

### Lease states

| Macro | Value | State |
| --- | --- | --- |
| `ELM_LEASE_HELD` | `0` | The lease is valid. A lease that is not a borrow is renewed in the background. |
| `ELM_LEASE_RETRYING` | `1` | The last renewal failed; the client tries again until the lease expires. |
| `ELM_LEASE_RECLAIMING` | `2` | The server did not know the lease after a restart; the client is reclaiming it. |
| `ELM_LEASE_LOST` | `3` | The lease expired, its reclaim was refused, or a clone was detected. |
| `ELM_LEASE_RELEASED` | `4` | The app released the lease, or the client was destroyed. |

### Other macros

| Macro | Value | Meaning |
| --- | --- | --- |
| `ELM_ABI_VERSION` | `1` | The version of the ABI that the header declares. |
| `ELM_API` | `__declspec(dllimport)` on Windows, empty elsewhere | Marks each declared function. |
| `ELM_FUNCTION` | `ELM_FUNCTION(ret, name, args)` | Declares the function `name` and the pointer type `name_fn`. |
