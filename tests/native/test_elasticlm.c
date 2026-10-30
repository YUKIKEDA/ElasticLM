/*
 * The C test of libelasticlm (design §17.4). It loads the published library at run time and calls every function through
 * a pointer of the header's type (name_fn), so the header's declarations are what the calls are compiled against.
 *
 * Usage (run.ps1 drives both steps):
 *   test_elasticlm thumbprint <library> <key folder>
 *     Creates this installation's key in the folder and prints its thumbprint.
 *   test_elasticlm verify <library> <key folder> <root.pub.pem> <signing key ID> <unbound.elmlic> <bound.elmlic>
 *     Runs the tests below and prints "ok" or "not ok" for each; exits 1 when one fails.
 */
/* With -std=c11, glibc declares POSIX functions such as sysconf only when asked to. */
#if defined(__linux__)
#define _POSIX_C_SOURCE 200809L
#endif

#include "elasticlm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <psapi.h>
static void* open_library(const char* path) { return (void*)LoadLibraryA(path); }
static void* find_function(void* library, const char* name) { return (void*)GetProcAddress((HMODULE)library, name); }
#else
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
static void* open_library(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static void* find_function(void* library, const char* name) { return dlsym(library, name); }
#endif
#if defined(__APPLE__)
#include <mach/mach.h>
#endif

/* Every exported function, as a pointer of the header's type. */
static struct {
    elm_installation_open_fn installation_open;
    elm_installation_thumbprint_fn installation_thumbprint;
    elm_installation_free_fn installation_free;
    elm_verifier_create_fn verifier_create;
    elm_verifier_verify_fn verifier_verify;
    elm_verifier_destroy_fn verifier_destroy;
    elm_license_id_fn license_id;
    elm_license_product_fn license_product;
    elm_license_kind_fn license_kind;
    elm_license_issued_at_fn license_issued_at;
    elm_license_expires_at_fn license_expires_at;
    elm_license_grace_period_seconds_fn license_grace_period_seconds;
    elm_license_in_grace_period_fn license_in_grace_period;
    elm_license_feature_count_fn license_feature_count;
    elm_license_feature_fn license_feature;
    elm_license_meta_count_fn license_meta_count;
    elm_license_meta_at_fn license_meta_at;
    elm_license_meta_get_fn license_meta_get;
    elm_license_free_fn license_free;
    elm_last_error_message_fn last_error_message;
    elm_version_fn version;
} elm;

static int failures;
static int number;

static void check(int passed, const char* name)
{
    number++;
    printf("%s %d - %s\n", passed ? "ok" : "not ok", number, name);
    if (!passed) {
        failures++;
        printf("# last error: %s\n", elm.last_error_message ? elm.last_error_message() : "(none)");
    }
}

/* Converts the symbol's address to the field's pointer type without a cast between object and function pointers. */
#define LOAD(field, name)                                                       \
    do {                                                                        \
        void* address = find_function(library, name);                           \
        if (address == NULL) {                                                  \
            fprintf(stderr, "missing export %s\n", name);                       \
            return 0;                                                           \
        }                                                                       \
        memcpy(&elm.field, &address, sizeof(void*));                            \
    } while (0)

static int load(const char* path)
{
    void* library = open_library(path);
    if (library == NULL) {
        fprintf(stderr, "cannot load %s\n", path);
        return 0;
    }

    LOAD(installation_open, "elm_installation_open");
    LOAD(installation_thumbprint, "elm_installation_thumbprint");
    LOAD(installation_free, "elm_installation_free");
    LOAD(verifier_create, "elm_verifier_create");
    LOAD(verifier_verify, "elm_verifier_verify");
    LOAD(verifier_destroy, "elm_verifier_destroy");
    LOAD(license_id, "elm_license_id");
    LOAD(license_product, "elm_license_product");
    LOAD(license_kind, "elm_license_kind");
    LOAD(license_issued_at, "elm_license_issued_at");
    LOAD(license_expires_at, "elm_license_expires_at");
    LOAD(license_grace_period_seconds, "elm_license_grace_period_seconds");
    LOAD(license_in_grace_period, "elm_license_in_grace_period");
    LOAD(license_feature_count, "elm_license_feature_count");
    LOAD(license_feature, "elm_license_feature");
    LOAD(license_meta_count, "elm_license_meta_count");
    LOAD(license_meta_at, "elm_license_meta_at");
    LOAD(license_meta_get, "elm_license_meta_get");
    LOAD(license_free, "elm_license_free");
    LOAD(last_error_message, "elm_last_error_message");
    LOAD(version, "elm_version");
    return 1;
}

static char* read_file(const char* path, size_t* length)
{
    FILE* file = fopen(path, "rb");
    if (file == NULL) {
        return NULL;
    }

    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    char* bytes = (char*)calloc((size_t)size + 1, 1);
    if (bytes != NULL && fread(bytes, 1, (size_t)size, file) != (size_t)size) {
        free(bytes);
        bytes = NULL;
    }

    fclose(file);
    *length = (size_t)size;
    return bytes;
}

static elm_installation* open_installation(const char* folder)
{
    elm_installation_options options;
    memset(&options, 0, sizeof options);
    options.struct_size = sizeof options;
    options.product_id = "acme-cad";
    options.scope = ELM_SCOPE_USER;
    options.directory = folder;
    options.create = 1;
    elm_installation* installation = NULL;
    return elm.installation_open(&options, &installation) == ELM_OK ? installation : NULL;
}

static elm_verifier* create_verifier(const char* root_pem, const char* revoked)
{
    elm_verifier_options options;
    memset(&options, 0, sizeof options);
    options.struct_size = sizeof options;
    options.root_keys_pem = &root_pem;
    options.root_key_count = 1;
    options.revoked_key_ids = &revoked;
    options.revoked_key_count = revoked == NULL ? 0 : 1;
    elm_verifier* verifier = NULL;
    return elm.verifier_create(&options, &verifier) == ELM_OK ? verifier : NULL;
}

static elm_requirement requirement(const elm_installation* installation)
{
    elm_requirement req;
    memset(&req, 0, sizeof req);
    req.struct_size = sizeof req;
    req.product_id = "acme-cad";
    req.kind = ELM_KIND_STANDARD;
    req.feature_id = "pro";
    req.feature_version = "2.5";
    req.installation = installation;
    return req;
}

/*
 * An unbound license verifies, and every value of it is read.
 * Preconditions: unbound.elmlic, issued by run.ps1 for acme-cad: license c-unbound, standard, feature pro >=2.0 <3.0,
 *   meta customer=Contoso, an expiry.
 * Steps: verify with feature pro 2.5; read the values; read feature 0 and the meta pair.
 * Expected: ELM_OK and an empty message; c-unbound, acme-cad, standard, an expiry, not in grace; pro with its range and no
 *   seats; customer=Contoso by index and by key; a missing key gives NULL.
 */
static void test_unbound(elm_verifier* verifier, const char* file, size_t length)
{
    elm_requirement req = requirement(NULL);
    elm_license* license = NULL;
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &req, &license) == ELM_OK && license != NULL, "unbound verifies");
    check(strcmp(elm.last_error_message(), "") == 0, "the message is empty after a success");
    check(strcmp(elm.license_id(license), "c-unbound") == 0 && strcmp(elm.license_product(license), "acme-cad") == 0, "id and product");
    check(elm.license_kind(license) == ELM_KIND_STANDARD && elm.license_in_grace_period(license) == 0, "kind and grace");
    int32_t has = 0;
    int64_t expires = 0;
    check(elm.license_expires_at(license, &has, &expires) == ELM_OK && has == 1 && expires > elm.license_issued_at(license), "expiry");
    check(elm.license_grace_period_seconds(license) >= 0, "grace period");

    elm_feature feature;
    memset(&feature, 0, sizeof feature);
    feature.struct_size = sizeof feature;
    check(elm.license_feature_count(license) == 1 && elm.license_feature(license, 0, &feature) == ELM_OK, "one feature");
    check(strcmp(feature.id, "pro") == 0 && strcmp(feature.version_range, ">=2.0 <3.0") == 0 && feature.has_seats == 0, "feature pro");

    const char* key = NULL;
    const char* value = NULL;
    size_t key_len = 0;
    size_t value_len = 0;
    check(elm.license_meta_count(license) == 1 && elm.license_meta_at(license, 0, &key, &key_len, &value, &value_len) == ELM_OK, "one meta pair");
    check(key_len == 8 && memcmp(key, "customer", 8) == 0 && value_len == 7 && memcmp(value, "Contoso", 7) == 0, "meta by index");
    check(elm.license_meta_get(license, "customer", &value, &value_len) == ELM_OK && strcmp(value, "Contoso") == 0, "meta by key");
    check(elm.license_meta_get(license, "missing", &value, &value_len) == ELM_OK && value == NULL, "missing meta key");
    elm.license_free(license);
}

/*
 * A node-locked license verifies with its installation only.
 * Preconditions: bound.elmlic, bound by run.ps1 to the thumbprint this program printed for the key folder.
 * Steps: verify with the installation; verify without it.
 * Expected: ELM_OK; ELM_WRONG_INSTALLATION with license NULL.
 */
static void test_bound(elm_verifier* verifier, elm_installation* installation, const char* file, size_t length)
{
    elm_requirement with = requirement(installation);
    elm_requirement without = requirement(NULL);
    elm_license* license = NULL;
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &with, &license) == ELM_OK, "bound verifies with its installation");
    elm.license_free(license);
    license = (elm_license*)&license;
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &without, &license) == ELM_WRONG_INSTALLATION && license == NULL, "bound fails without it");
}

/*
 * Refusals come back as numbers with a message.
 * Preconditions: a verifier with the signing key revoked; the unbound license.
 * Steps: verify with the revoked key; with product acme-other; with struct_size one less; with a null out; with a length
 *   over 65,536 bytes.
 * Expected: ELM_KEY_REVOKED; ELM_WRONG_PRODUCT; ELM_INVALID_ARGUMENT twice; ELM_MALFORMED; each with a message.
 */
static void test_refusals(elm_verifier* verifier, elm_verifier* revoked, const char* file, size_t length)
{
    elm_requirement req = requirement(NULL);
    elm_license* license = NULL;
    check(elm.verifier_verify(revoked, (const uint8_t*)file, length, &req, &license) == ELM_KEY_REVOKED && license == NULL, "revoked key");
    check(strlen(elm.last_error_message()) > 0, "a message after a refusal");

    elm_requirement other = requirement(NULL);
    other.product_id = "acme-other";
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &other, &license) == ELM_WRONG_PRODUCT, "wrong product");

    elm_requirement smaller = requirement(NULL);
    smaller.struct_size = sizeof smaller - 1;
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &smaller, &license) == ELM_INVALID_ARGUMENT, "struct_size one less");
    check(elm.verifier_verify(verifier, (const uint8_t*)file, length, &req, NULL) == ELM_INVALID_ARGUMENT, "null out");

    char* large = (char*)calloc(65537, 1);
    check(large != NULL && elm.verifier_verify(verifier, (const uint8_t*)large, 65537, &req, &license) == ELM_MALFORMED, "a file over 64 KiB");
    free(large);
}

/* The process's resident memory in KiB, to print around the thread test; 0 when it cannot be read. */
static long resident_kib(void)
{
#if defined(_WIN32)
    PROCESS_MEMORY_COUNTERS counters;
    return K32GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof counters) ? (long)(counters.WorkingSetSize / 1024) : 0;
#elif defined(__APPLE__)
    mach_task_basic_info_data_t info;
    mach_msg_type_number_t count = MACH_TASK_BASIC_INFO_COUNT;
    return task_info(mach_task_self(), MACH_TASK_BASIC_INFO, (task_info_t)&info, &count) == KERN_SUCCESS ? (long)(info.resident_size / 1024) : 0;
#else
    long pages = 0;
    long resident = 0;
    FILE* statm = fopen("/proc/self/statm", "r");
    if (statm != NULL) {
        if (fscanf(statm, "%ld %ld", &pages, &resident) != 2) {
            resident = 0;
        }
        fclose(statm);
    }
    return resident * (sysconf(_SC_PAGESIZE) / 1024);
#endif
}

/* One C thread: a failing call, then this thread's message, then the thread ends. */
static int thread_saw_its_message;

#if defined(_WIN32)
static DWORD WINAPI failing_call(LPVOID verifier)
#else
static void* failing_call(void* verifier)
#endif
{
    elm_requirement req = requirement(NULL);
    uint8_t byte = 0;
    int32_t code = elm.verifier_verify((elm_verifier*)verifier, &byte, 1, &req, NULL);
    thread_saw_its_message = code == ELM_INVALID_ARGUMENT && strcmp(elm.last_error_message(), "out is required.") == 0;
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

static int run_thread(elm_verifier* verifier)
{
    thread_saw_its_message = 0;
#if defined(_WIN32)
    HANDLE thread = CreateThread(NULL, 0, failing_call, verifier, 0, NULL);
    if (thread == NULL) {
        return 0;
    }
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
#else
    pthread_t thread;
    if (pthread_create(&thread, NULL, failing_call, verifier) != 0) {
        return 0;
    }
    pthread_join(thread, NULL);
#endif
    return thread_saw_its_message;
}

/*
 * Threads made by C enter the library, fail, read their own message, and end (design §17.4).
 * Preconditions: a verifier; a message left on this thread by a failed call.
 * Steps: 4,000 times, start a C thread that verifies with a null out, reads its message, and ends; read this thread's
 *   message; print the resident memory before, after 2,000 threads, and after 4,000 threads.
 * Expected: every thread sees ELM_INVALID_ARGUMENT and its own message; this thread's message is unchanged; no crash.
 *   The memory figures are printed for the PR (the 2,000 threads after the first 2,000 should not add much).
 */
static void test_c_threads(elm_verifier* verifier)
{
    elm_requirement req = requirement(NULL);
    elm_license* license = NULL;
    elm.verifier_verify(verifier, (const uint8_t*)"x", 1, &req, &license);
    char before[256];
    snprintf(before, sizeof before, "%s", elm.last_error_message());

    long start = resident_kib();
    long middle = 0;
    int all = 1;
    for (int i = 0; i < 4000; i++) {
        all = run_thread(verifier) && all;
        if (i == 1999) {
            middle = resident_kib();
        }
    }

    check(all, "4,000 C threads each see their own message");
    check(strlen(before) > 0 && strcmp(before, elm.last_error_message()) == 0, "this thread's message is unchanged");
    printf("# resident memory (KiB): %ld before, %ld after 2,000 threads, %ld after 4,000 threads\n", start, middle, resident_kib());
}

/*
 * Value functions take NULL handles, and the library reports a version.
 * Preconditions: none.
 * Steps: call the value functions with NULL; release NULL handles; read the version.
 * Expected: NULL, 0, or -1 as the header says; no crash; a version that is not empty.
 */
static void test_null_handles(void)
{
    check(elm.license_id(NULL) == NULL && elm.license_kind(NULL) == -1 && elm.license_in_grace_period(NULL) == -1, "null license");
    check(elm.license_feature_count(NULL) == 0 && elm.license_meta_count(NULL) == 0 && elm.installation_thumbprint(NULL) == NULL, "null counts");
    elm.license_free(NULL);
    elm.verifier_destroy(NULL);
    elm.installation_free(NULL);
    check(elm.version() != NULL && strlen(elm.version()) > 0, "version");
}

int main(int argc, char** argv)
{
    if (argc >= 4 && strcmp(argv[1], "thumbprint") == 0) {
        if (!load(argv[2])) {
            return 2;
        }

        elm_installation* installation = open_installation(argv[3]);
        if (installation == NULL) {
            fprintf(stderr, "cannot open the installation: %s\n", elm.last_error_message());
            return 1;
        }

        printf("%s\n", elm.installation_thumbprint(installation));
        elm.installation_free(installation);
        return 0;
    }

    if (argc != 8 || strcmp(argv[1], "verify") != 0) {
        fprintf(stderr, "usage: test_elasticlm thumbprint <library> <folder> | verify <library> <folder> <root.pub.pem> <signing kid> <unbound> <bound>\n");
        return 2;
    }

    if (!load(argv[2])) {
        return 2;
    }

    size_t root_length = 0;
    size_t unbound_length = 0;
    size_t bound_length = 0;
    char* root = read_file(argv[4], &root_length);
    char* unbound = read_file(argv[6], &unbound_length);
    char* bound = read_file(argv[7], &bound_length);
    elm_installation* installation = open_installation(argv[3]);
    elm_verifier* verifier = root == NULL ? NULL : create_verifier(root, NULL);
    elm_verifier* revoked = root == NULL ? NULL : create_verifier(root, argv[5]);
    if (unbound == NULL || bound == NULL || installation == NULL || verifier == NULL || revoked == NULL) {
        fprintf(stderr, "setup failed: %s\n", elm.last_error_message());
        return 1;
    }

    test_unbound(verifier, unbound, unbound_length);
    test_bound(verifier, installation, bound, bound_length);
    test_refusals(verifier, revoked, unbound, unbound_length);
    test_null_handles();
    test_c_threads(verifier);

    elm.verifier_destroy(revoked);
    elm.verifier_destroy(verifier);
    elm.installation_free(installation);
    free(root);
    free(unbound);
    free(bound);
    printf("1..%d\n", number);
    return failures == 0 ? 0 : 1;
}
