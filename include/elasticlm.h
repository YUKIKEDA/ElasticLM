/*
 * elasticlm.h: the C ABI of libelasticlm (ElasticLM design §17).
 *
 * The library is elasticlm.dll on Windows, libelasticlm.so on Linux, and libelasticlm.dylib on macOS. It is a NativeAOT
 * library: it cannot be unloaded (do not call FreeLibrary or dlclose on it).
 *
 * Rules for every function:
 * - Strings are UTF-8 and end with NUL, both ways. Strings from the caller must be valid UTF-8; a PEM may be at most
 *   16 KiB and any other string at most 1 KiB before its NUL.
 * - Functions that return int32_t under "functions that return a number" return an ElasticLM error number (design §19):
 *   0 (ELM_OK) is success. They empty this thread's error message when they start and write it when they fail. On every
 *   failure they set their outputs to NULL and 0; an elm_feature with a right struct_size gets its other members set to
 *   NULL and 0. A NULL output argument is ELM_INVALID_ARGUMENT (1). Exceptions are named at the function:
 *   retry_after_seconds may be NULL and gets the retry time on a failure; *needed gets the size on a too-small buffer.
 * - Functions that return a value never fail. With a NULL handle they return NULL, 0, or -1 as each one says, and they do
 *   not change the error message.
 * - Every struct you pass starts with struct_size; set it to sizeof the struct. Any other value is ELM_INVALID_ARGUMENT.
 *   Boolean members are int32_t and must be 0 or 1. Zero-filled structs give the defaults.
 * - The library frees what it returns, in the matching _free or _destroy, which ignore NULL. Freeing a handle twice, or
 *   using it after it is freed, is undefined behavior. A handle of another type is refused where it can be told.
 * - Handles may be used from several threads at once. Read a license from several threads, but do not free it while
 *   another thread reads it.
 * - No .NET exception leaves the library; an internal failure is ELM_INTERNAL_ERROR (99).
 */
#ifndef ELASTICLM_H
#define ELASTICLM_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The version of this ABI. A new version may add functions and add members at the end of structs. */
#define ELM_ABI_VERSION 1

/* Error numbers (design §19). */
#define ELM_OK 0
#define ELM_INVALID_ARGUMENT 1
#define ELM_MALFORMED 2
#define ELM_UNTRUSTED_KEY 3
#define ELM_BAD_SIGNATURE 4
#define ELM_CERTIFICATE_EXPIRED 5
#define ELM_CLOCK_TAMPERED 6
#define ELM_WRONG_PRODUCT 7
#define ELM_WRONG_INSTALLATION 8
#define ELM_FINGERPRINT_MISMATCH 9
#define ELM_EXPIRED 10
#define ELM_FEATURE_NOT_LICENSED 11
#define ELM_FEATURE_VERSION_NOT_LICENSED 12
#define ELM_FEATURE_EXPIRED 13
#define ELM_FINGERPRINT_UNAVAILABLE 14
#define ELM_INSTALLATION_KEY_UNREADABLE 15
#define ELM_WRONG_KIND 16
#define ELM_KEY_REVOKED 17
#define ELM_LEASE_OUT_OF_BOUNDS 18
#define ELM_INVALID_STATE 19
#define ELM_PROOF_INVALID 20
#define ELM_NONCE_REQUIRED 21
#define ELM_REPLAYED 22
#define ELM_SERVER_PIN_MISMATCH 23
#define ELM_PAYLOAD_TOO_LARGE 24
#define ELM_LICENSE_NOT_FOUND 30
#define ELM_LICENSE_SUSPENDED 31
#define ELM_LICENSE_REVOKED 32
#define ELM_ACTIVATION_LIMIT 33
#define ELM_DEACTIVATION_LIMIT 34
#define ELM_TRIAL_USED 35
#define ELM_CLONE_DETECTED 36
#define ELM_REQUEST_EXPIRED 37
#define ELM_REQUEST_MISMATCH 38
#define ELM_TRIAL_NOT_OFFERED 39
#define ELM_NO_SEATS 40
#define ELM_UNKNOWN_LEASE 41
#define ELM_BORROW_NOT_ALLOWED 42
#define ELM_SERVER_RECOVERING 43
#define ELM_RETURN_NOT_ALLOWED 44
#define ELM_UNAUTHORIZED 50
#define ELM_FORBIDDEN 51
#define ELM_RATE_LIMITED 52
#define ELM_SERVER_BUSY 53
#define ELM_NOT_FOUND 54
#define ELM_CONFLICT 55
#define ELM_ACTIVATION_NOT_FOUND 56
#define ELM_NETWORK_ERROR 90
#define ELM_INTERNAL_ERROR 99

/* License kinds. */
#define ELM_KIND_STANDARD 0
#define ELM_KIND_TRIAL 1
#define ELM_KIND_SERVER 2

/* Where an installation key is kept (design §10.2). */
#define ELM_SCOPE_USER 0
#define ELM_SCOPE_MACHINE 1

/* The state of an activation (design §12.6.2). */
#define ELM_STATE_NONE 0
#define ELM_STATE_ACTIVE 1
#define ELM_STATE_REFRESH_FAILING 2
#define ELM_STATE_REFUSED 3
#define ELM_STATE_CLONE_DETECTED 4

#if defined(_WIN32)
#define ELM_API __declspec(dllimport)
#else
#define ELM_API
#endif

/* Declares a function and, for callers that load the library at run time, the type of a pointer to it: name_fn. */
#define ELM_FUNCTION(ret, name, args) \
    typedef ret(*name##_fn) args;     \
    ELM_API ret name args;

/* This installation's key and the machine fingerprints for one product. */
typedef struct elm_installation elm_installation;

/* A license verifier: the vendor's root keys and a clock guard. */
typedef struct elm_verifier elm_verifier;

/* A verified license. Every string it returns is valid until elm_license_free. */
typedef struct elm_license elm_license;

/* This installation's activation with the hosted service (design §12.6, §17.5). */
typedef struct elm_activation elm_activation;

/*
 * Called when the activation's state changes, with the error number (0 when none) and its message (empty when none),
 * valid during the call only. It runs on the thread that ends the operation that changed the state, which can be the
 * caller's own thread inside that call, or on a library thread for the automatic refresh. The callbacks an operation
 * causes have returned before the operation's function returns; none starts after elm_activation_destroy returns.
 * Inside it, the same activation's blocking functions return ELM_INVALID_STATE and elm_activation_destroy does nothing;
 * elm_activation_state, elm_activation_id, and elm_activation_check may be called. Do not leave it by a C++ exception or
 * longjmp; do not take a lock that the thread calling into the library holds; return soon, since it can hold a library
 * thread. A thread the callback starts is not told apart from other threads.
 */
typedef void (*elm_state_callback)(void* user, int32_t previous, int32_t current, int32_t error, const char* message);

typedef struct {
    uint32_t struct_size;
    const char* product_id; /* required */
    int32_t scope;          /* ELM_SCOPE_* */
    const char* directory;  /* NULL for the default folder of design §10.2 */
    int32_t create;         /* 1: create the key when there is none; 0: ELM_INSTALLATION_KEY_UNREADABLE */
} elm_installation_options;

typedef struct {
    uint32_t struct_size;
    const char* const* root_keys_pem;     /* the vendor's root public keys in PEM: 1 to 16 */
    size_t root_key_count;
    const char* const* revoked_key_ids;   /* key IDs revoked by the vendor: 0 to 1024 */
    size_t revoked_key_count;
    const elm_installation* installation; /* NULL, or the key that signs the clock guard's state; copied */
    const char* clock_guard_path;         /* NULL: "clock" in the installation's folder, or no state without one.
                                           * Without an installation the state file is not signed: whoever can write
                                           * it can reset the guard. */
    int32_t clock_skew_seconds;           /* 0: the default, 300; otherwise 1 to 3600 */
    int32_t reject_trial_as_standard;     /* 0 (default): a standard requirement also accepts a trial license */
} elm_verifier_options;

typedef struct {
    uint32_t struct_size;
    const char* product_id;               /* required */
    int32_t kind;                         /* ELM_KIND_* */
    const char* feature_id;               /* NULL: no feature required */
    const char* feature_version;          /* NULL, or major.minor[.patch] */
    const elm_installation* installation; /* NULL for a license that is not node-locked; used during the call only */
} elm_requirement;

typedef struct {
    uint32_t struct_size;      /* set by you; the library writes this struct */
    const char* id;
    const char* version_range; /* NULL when the feature has none */
    int32_t has_seats;
    int32_t seats;
    int32_t has_expires_at;
    int64_t expires_at;        /* UNIX seconds */
} elm_feature;

typedef struct {
    uint32_t struct_size;
    const char* product_id;               /* required */
    const char* service_url;              /* https, or http on a loopback address; NULL: offline files only */
    const char* const* root_keys_pem;     /* the vendor's root public keys in PEM: 1 to 16 */
    size_t root_key_count;
    const char* const* revoked_key_ids;   /* key IDs revoked by the vendor: 0 to 1024 */
    size_t revoked_key_count;
    int32_t scope;                        /* ELM_SCOPE_* */
    const char* directory;                /* NULL for the default folder of design §10.2 */
    int32_t send_client_info;             /* 1: send the host and user names (design §4.4) */
    const char* app_version;              /* NULL, or at most 64 bytes */
    int32_t clock_skew_seconds;           /* 0: the default, 300; otherwise 1 to 3600 */
    int32_t reject_trial_as_standard;     /* 0 (default): a standard requirement also accepts a trial license */
    elm_state_callback on_state;          /* NULL, or the state callback */
    void* user;                           /* passed to on_state; keep it valid until elm_activation_destroy returns */
} elm_activation_options;

/* ---- Functions that return a number ---- */

/* Opens or creates this installation's key and reads the machine fingerprints of the product. Free with
 * elm_installation_free. When no OS ID can be read, the list is empty and fingerprinted licenses fail with
 * ELM_FINGERPRINT_UNAVAILABLE. */
ELM_FUNCTION(int32_t, elm_installation_open, (const elm_installation_options* options, elm_installation** out))

/* Makes a verifier. Destroy with elm_verifier_destroy. The installation may be freed right after this call. */
ELM_FUNCTION(int32_t, elm_verifier_create, (const elm_verifier_options* options, elm_verifier** out))

/* Verifies a license file. The library reads len bytes at file and keeps nothing; len larger than the buffer is undefined
 * behavior. A file larger than 65536 bytes is ELM_MALFORMED. On success *out is a new license; free it with
 * elm_license_free. The requirement's installation must be of the requirement's product. */
ELM_FUNCTION(int32_t, elm_verifier_verify, (elm_verifier* verifier, const uint8_t* file, size_t len, const elm_requirement* requirement, elm_license** out))

/* *has_value is 1 and *value the expiry in UNIX seconds, or *has_value is 0 when the license does not expire. */
ELM_FUNCTION(int32_t, elm_license_expires_at, (const elm_license* license, int32_t* has_value, int64_t* value))

/* Writes feature index (0 to count - 1) into *out, whose struct_size you set. */
ELM_FUNCTION(int32_t, elm_license_feature, (const elm_license* license, size_t index, elm_feature* out))

/* Meta pair index, in the license's order. Keys and values may hold NUL; the lengths are in bytes before the final NUL. */
ELM_FUNCTION(int32_t, elm_license_meta_at, (const elm_license* license, size_t index, const char** key, size_t* key_len, const char** value, size_t* value_len))

/* The value of a meta key, or *value = NULL when there is none (still ELM_OK). */
ELM_FUNCTION(int32_t, elm_license_meta_get, (const elm_license* license, const char* key, const char** value, size_t* value_len))

/* Makes the activation of this installation. Destroy with elm_activation_destroy. */
ELM_FUNCTION(int32_t, elm_activation_create, (const elm_activation_options* options, elm_activation** out))

/*
 * The online operations wait until they end, with no way to cancel them. The longest wait is the sum of: another
 * operation of the same activation, the automatic refresh included (no bound); another process's lock on this
 * installation (10 s); and up to 3 sends of at most 30 s each. retry_after_seconds may be NULL; otherwise it gets 0, or
 * when to try again after a refusal (seconds, rounded up). On success *out is a new license; free it with
 * elm_license_free.
 */
ELM_FUNCTION(int32_t, elm_activation_activate, (elm_activation* activation, const char* license_key, elm_license** out, int64_t* retry_after_seconds))
ELM_FUNCTION(int32_t, elm_activation_start_trial, (elm_activation* activation, elm_license** out, int64_t* retry_after_seconds))
ELM_FUNCTION(int32_t, elm_activation_refresh, (elm_activation* activation, elm_license** out, int64_t* retry_after_seconds))
ELM_FUNCTION(int32_t, elm_activation_deactivate, (elm_activation* activation))

/* Checks the kept activation offline. feature_id and feature_version may be NULL; kind is ELM_KIND_*. */
ELM_FUNCTION(int32_t, elm_activation_check, (elm_activation* activation, const char* feature_id, const char* feature_version, int32_t kind, elm_license** out))

/*
 * Offline files (design §12.8). *out is the file's text; free it with elm_string_free, which clears it first. An
 * activation request holds the license key: keep it as you keep the key. *replaced is 1 when a waiting request or a kept
 * proof was replaced. renew is 0 or 1.
 */
ELM_FUNCTION(int32_t, elm_activation_create_activation_request, (elm_activation* activation, const char* license_key, int32_t renew, char** out, int32_t* replaced))
ELM_FUNCTION(int32_t, elm_activation_create_refresh_request, (elm_activation* activation, int32_t renew, char** out, int32_t* replaced))
ELM_FUNCTION(int32_t, elm_activation_create_deactivation_proof, (elm_activation* activation, char** out, int32_t* replaced))
ELM_FUNCTION(int32_t, elm_activation_forget_deactivation_proof, (elm_activation* activation))

/* Keeps the service's answer to the waiting request: len bytes of UTF-8 at response, at most 262144 (more is
 * ELM_MALFORMED); len larger than the buffer is undefined behavior. */
ELM_FUNCTION(int32_t, elm_activation_import_response, (elm_activation* activation, const uint8_t* response, size_t len, elm_license** out))

/* Copies the current activation ID (empty when none) into buffer. *needed gets the size with the NUL, also when size is
 * too small (ELM_INVALID_ARGUMENT, buffer untouched). buffer may be NULL when size is 0. */
ELM_FUNCTION(int32_t, elm_activation_id, (const elm_activation* activation, char* buffer, size_t size, size_t* needed))

/* ---- Functions that return a value ---- */

ELM_FUNCTION(const char*, elm_installation_thumbprint, (const elm_installation* installation)) /* NULL for NULL */
ELM_FUNCTION(const char*, elm_license_id, (const elm_license* license)) /* NULL for NULL */
ELM_FUNCTION(const char*, elm_license_product, (const elm_license* license)) /* NULL for NULL */
ELM_FUNCTION(int32_t, elm_license_kind, (const elm_license* license)) /* ELM_KIND_*, -1 for NULL */
ELM_FUNCTION(int64_t, elm_license_issued_at, (const elm_license* license)) /* UNIX seconds, 0 for NULL */
ELM_FUNCTION(int64_t, elm_license_grace_period_seconds, (const elm_license* license)) /* 0 for NULL */
ELM_FUNCTION(int32_t, elm_license_in_grace_period, (const elm_license* license)) /* 1 or 0, -1 for NULL */
ELM_FUNCTION(size_t, elm_license_feature_count, (const elm_license* license)) /* 0 for NULL */
ELM_FUNCTION(size_t, elm_license_meta_count, (const elm_license* license)) /* 0 for NULL */
ELM_FUNCTION(int32_t, elm_activation_state, (const elm_activation* activation)) /* ELM_STATE_*, -1 for NULL */

/* The message of the last function on this thread that returns a number: empty after a success, and valid until the next
 * such call on this thread. */
ELM_FUNCTION(const char*, elm_last_error_message, (void))

/* The library's version, valid until the process ends. */
ELM_FUNCTION(const char*, elm_version, (void))

/* ---- Release (NULL is ignored) ---- */

ELM_FUNCTION(void, elm_installation_free, (elm_installation* installation))
ELM_FUNCTION(void, elm_verifier_destroy, (elm_verifier* verifier))
ELM_FUNCTION(void, elm_license_free, (elm_license* license))

/* Waits for running callbacks (no time bound), then stops the automatic refresh, waiting up to 5 s for it. Calling it
 * while another thread is inside a function of the same activation is undefined behavior. */
ELM_FUNCTION(void, elm_activation_destroy, (elm_activation* activation))

/* Clears and frees a string that a function above says to free with it. */
ELM_FUNCTION(void, elm_string_free, (char* text))

#ifdef __cplusplus
}
#endif

#endif /* ELASTICLM_H */
