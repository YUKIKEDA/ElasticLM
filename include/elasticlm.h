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
 *   failure they set their outputs to NULL and 0. A NULL output argument is ELM_INVALID_ARGUMENT (1).
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

/* Error numbers used below (the full list is design §19). */
#define ELM_OK 0
#define ELM_INVALID_ARGUMENT 1
#define ELM_MALFORMED 2
#define ELM_UNTRUSTED_KEY 3
#define ELM_BAD_SIGNATURE 4
#define ELM_CLOCK_TAMPERED 6
#define ELM_EXPIRED 10
#define ELM_WRONG_PRODUCT 7
#define ELM_WRONG_INSTALLATION 8
#define ELM_FINGERPRINT_MISMATCH 9
#define ELM_FEATURE_NOT_LICENSED 11
#define ELM_FINGERPRINT_UNAVAILABLE 14
#define ELM_INSTALLATION_KEY_UNREADABLE 15
#define ELM_WRONG_KIND 16
#define ELM_KEY_REVOKED 17
#define ELM_INTERNAL_ERROR 99

/* License kinds. */
#define ELM_KIND_STANDARD 0
#define ELM_KIND_TRIAL 1
#define ELM_KIND_SERVER 2

/* Where an installation key is kept (design §10.2). */
#define ELM_SCOPE_USER 0
#define ELM_SCOPE_MACHINE 1

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
    const char* clock_guard_path;         /* NULL: "clock" in the installation's folder, or no state without one */
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

/* The message of the last function on this thread that returns a number: empty after a success, and valid until the next
 * such call on this thread. */
ELM_FUNCTION(const char*, elm_last_error_message, (void))

/* The library's version, valid until the process ends. */
ELM_FUNCTION(const char*, elm_version, (void))

/* ---- Release (NULL is ignored) ---- */

ELM_FUNCTION(void, elm_installation_free, (elm_installation* installation))
ELM_FUNCTION(void, elm_verifier_destroy, (elm_verifier* verifier))
ELM_FUNCTION(void, elm_license_free, (elm_license* license))

#ifdef __cplusplus
}
#endif

#endif /* ELASTICLM_H */
