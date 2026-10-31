/*
 * The C test of libelasticlm (design §17.4). It loads the published library at run time and calls every function through
 * a pointer of the header's type (name_fn), so the header's declarations are what the calls are compiled against.
 *
 * Usage (run.ps1 drives both steps):
 *   test_elasticlm thumbprint <library> <key folder>
 *     Creates this installation's key in the folder and prints its thumbprint.
 *   test_elasticlm verify <library> <key folder> <root.pub.pem> <signing key ID> <unbound.elmlic> <bound.elmlic>
 *     Runs the verification tests and prints "ok" or "not ok" for each; exits 1 when one fails.
 *   test_elasticlm activation <library> <key folder> <root.pub.pem> <service URL> <license key>
 *     Runs the activation tests against the elm-service at the URL (design §17.5).
 *   test_elasticlm lease <library> <key folder> <root.pub.pem> <server URL> <service URL> <license key>
 *     Runs the lease tests against the elm-server (https) and the elm-service at the URLs (design §17.6).
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
    elm_activation_create_fn activation_create;
    elm_activation_activate_fn activation_activate;
    elm_activation_start_trial_fn activation_start_trial;
    elm_activation_refresh_fn activation_refresh;
    elm_activation_deactivate_fn activation_deactivate;
    elm_activation_check_fn activation_check;
    elm_activation_create_activation_request_fn activation_create_activation_request;
    elm_activation_create_refresh_request_fn activation_create_refresh_request;
    elm_activation_import_response_fn activation_import_response;
    elm_activation_create_deactivation_proof_fn activation_create_deactivation_proof;
    elm_activation_forget_deactivation_proof_fn activation_forget_deactivation_proof;
    elm_activation_id_fn activation_id;
    elm_activation_state_fn activation_state;
    elm_activation_destroy_fn activation_destroy;
    elm_string_free_fn string_free;
    elm_client_create_fn client_create;
    elm_client_acquire_lease_fn client_acquire_lease;
    elm_client_borrow_lease_fn client_borrow_lease;
    elm_client_release_lease_fn client_release_lease;
    elm_client_get_borrowed_leases_fn client_get_borrowed_leases;
    elm_client_reset_server_pin_fn client_reset_server_pin;
    elm_lease_id_fn lease_id;
    elm_lease_feature_id_fn lease_feature_id;
    elm_lease_expires_at_fn lease_expires_at;
    elm_lease_seats_fn lease_seats;
    elm_lease_is_borrow_fn lease_is_borrow;
    elm_lease_state_fn lease_state;
    elm_lease_last_error_fn lease_last_error;
    elm_lease_free_fn lease_free;
    elm_lease_list_free_fn lease_list_free;
    elm_client_destroy_fn client_destroy;
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
    LOAD(activation_create, "elm_activation_create");
    LOAD(activation_activate, "elm_activation_activate");
    LOAD(activation_start_trial, "elm_activation_start_trial");
    LOAD(activation_refresh, "elm_activation_refresh");
    LOAD(activation_deactivate, "elm_activation_deactivate");
    LOAD(activation_check, "elm_activation_check");
    LOAD(activation_create_activation_request, "elm_activation_create_activation_request");
    LOAD(activation_create_refresh_request, "elm_activation_create_refresh_request");
    LOAD(activation_import_response, "elm_activation_import_response");
    LOAD(activation_create_deactivation_proof, "elm_activation_create_deactivation_proof");
    LOAD(activation_forget_deactivation_proof, "elm_activation_forget_deactivation_proof");
    LOAD(activation_id, "elm_activation_id");
    LOAD(activation_state, "elm_activation_state");
    LOAD(activation_destroy, "elm_activation_destroy");
    LOAD(string_free, "elm_string_free");
    LOAD(client_create, "elm_client_create");
    LOAD(client_acquire_lease, "elm_client_acquire_lease");
    LOAD(client_borrow_lease, "elm_client_borrow_lease");
    LOAD(client_release_lease, "elm_client_release_lease");
    LOAD(client_get_borrowed_leases, "elm_client_get_borrowed_leases");
    LOAD(client_reset_server_pin, "elm_client_reset_server_pin");
    LOAD(lease_id, "elm_lease_id");
    LOAD(lease_feature_id, "elm_lease_feature_id");
    LOAD(lease_expires_at, "elm_lease_expires_at");
    LOAD(lease_seats, "elm_lease_seats");
    LOAD(lease_is_borrow, "elm_lease_is_borrow");
    LOAD(lease_state, "elm_lease_state");
    LOAD(lease_last_error, "elm_lease_last_error");
    LOAD(lease_free, "elm_lease_free");
    LOAD(lease_list_free, "elm_lease_list_free");
    LOAD(client_destroy, "elm_client_destroy");
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

/* The state callback of the activation test: counts the changes and keeps the last one. */
static int state_changes;
static int32_t last_previous = -1;
static int32_t last_current = -1;

static void on_state(void* user, int32_t previous, int32_t current, int32_t error, const char* message)
{
    (void)error;
    (void)message;
    if (user == (void*)&state_changes) {
        state_changes++;
        last_previous = previous;
        last_current = current;
    }
}

/*
 * An activation with the hosted service, from C (design §17.5).
 * Preconditions: elm-service on loopback, started by run.ps1 with product acme-cad, feature pro, and a license of 3
 *   activations whose key is passed in.
 * Steps: create the activation with the state callback; activate; check feature pro; read the state and the ID; make a
 *   refresh request and free it; deactivate; read the state; call the remaining functions with a NULL activation.
 * Expected: ELM_OK for each; the callback saw NONE->ACTIVE before activate returned and ACTIVE->NONE before deactivate
 *   returned; the state is ACTIVE then NONE; the ID is not empty while active; the request is text; each remaining
 *   function returns ELM_INVALID_ARGUMENT for the NULL activation.
 */
static void test_activation(const char* folder, const char* root_pem, const char* url, const char* key)
{
    elm_activation_options options;
    memset(&options, 0, sizeof options);
    options.struct_size = sizeof options;
    options.product_id = "acme-cad";
    options.service_url = url;
    options.root_keys_pem = &root_pem;
    options.root_key_count = 1;
    options.directory = folder;
    options.on_state = on_state;
    options.user = &state_changes;
    elm_activation* activation = NULL;
    check(elm.activation_create(&options, &activation) == ELM_OK && activation != NULL, "activation created");
    if (activation == NULL) {
        return;
    }

    elm_license* license = NULL;
    int64_t retry = -1;
    check(elm.activation_activate(activation, key, &license, &retry) == ELM_OK && license != NULL && retry == 0, "activate");
    check(state_changes == 1 && last_previous == ELM_STATE_NONE && last_current == ELM_STATE_ACTIVE, "callback before activate returned");
    elm.license_free(license);

    license = NULL;
    check(elm.activation_check(activation, "pro", NULL, ELM_KIND_STANDARD, &license) == ELM_OK, "check");
    elm.license_free(license);
    check(elm.activation_state(activation) == ELM_STATE_ACTIVE, "state active");

    char id[128];
    size_t needed = 0;
    check(elm.activation_id(activation, id, sizeof id, &needed) == ELM_OK && needed > 1 && strlen(id) == needed - 1, "activation id");

    char* request = NULL;
    int32_t replaced = -1;
    check(elm.activation_create_refresh_request(activation, 0, &request, &replaced) == ELM_OK && request != NULL && replaced == 0, "refresh request");
    elm.string_free(request);

    check(elm.activation_deactivate(activation) == ELM_OK, "deactivate");
    check(state_changes == 2 && last_current == ELM_STATE_NONE && elm.activation_state(activation) == ELM_STATE_NONE, "callback before deactivate returned");
    elm.activation_destroy(activation);

    license = NULL;
    check(elm.activation_start_trial(NULL, &license, NULL) == ELM_INVALID_ARGUMENT, "start_trial with NULL");
    check(elm.activation_refresh(NULL, &license, NULL) == ELM_INVALID_ARGUMENT, "refresh with NULL");
    check(elm.activation_create_activation_request(NULL, key, 0, &request, &replaced) == ELM_INVALID_ARGUMENT, "activation request with NULL");
    uint8_t byte = '{';
    check(elm.activation_import_response(NULL, &byte, 1, &license) == ELM_INVALID_ARGUMENT, "import with NULL");
    check(elm.activation_create_deactivation_proof(NULL, &request, &replaced) == ELM_INVALID_ARGUMENT, "proof with NULL");
    check(elm.activation_forget_deactivation_proof(NULL) == ELM_INVALID_ARGUMENT, "forget with NULL");
    check(elm.activation_state(NULL) == -1, "state of NULL");
    elm.string_free(NULL);
    elm.activation_destroy(NULL);
}

/* The lease callback of the lease test: counts the changes and keeps the last one. */
static volatile int lease_changes;
static volatile int32_t lease_last_current = -1;

static void on_lease(void* user, const char* lease_id, int32_t previous, int32_t current, int32_t error, const char* message)
{
    (void)lease_id;
    (void)previous;
    (void)error;
    (void)message;
    if (user == (void*)&lease_changes) {
        lease_changes++;
        lease_last_current = current;
    }
}

static void wait_seconds(int64_t seconds)
{
#if defined(_WIN32)
    Sleep((DWORD)(seconds * 1000));
#else
    sleep((unsigned int)seconds);
#endif
}

static elm_client* make_client(const char* folder, const char* root_pem, const char* url, const char* key)
{
    elm_client_options options;
    memset(&options, 0, sizeof options);
    options.struct_size = sizeof options;
    options.product_id = "acme-cad";
    options.server_url = url;
    options.license_key = key;
    options.root_keys_pem = &root_pem;
    options.root_key_count = 1;
    options.directory = folder;
    options.on_lease = on_lease;
    options.user = (void*)&lease_changes;
    elm_client* client = NULL;
    if (elm.client_create(&options, &client) != ELM_OK) {
        fprintf(stderr, "elm_client_create: %s\n", elm.last_error_message());
    }

    return client;
}

/*
 * Leases from an on-premises server over https and from the hosted service, from C (design §17.6).
 * Preconditions: elm-server on https loopback, started by run.ps1 a moment ago with a server license of 15-second leases,
 *   seats of pro, and borrows allowed; elm-service with a cloud-floating policy and a license whose key is passed in.
 * Steps: on the server, acquire pro (refused while the server reclaims), wait retry_after_seconds, acquire again; read the
 *   lease; release it; borrow pro for an hour and list the borrows; delete the server's pin; destroy. On the service,
 *   acquire pro and release it; delete the pin. Call the remaining functions with NULL.
 * Expected: ELM_SERVER_RECOVERING with retry_after_seconds > 0, then ELM_OK; feature pro, 1 seat, not a borrow, HELD, no
 *   error, an expiry in the future; the release ELM_OK, RELEASED, and its callback before it returned; the borrow listed
 *   with its ID; deleted 1 on the server and 0 on the service; ELM_INVALID_ARGUMENT or -1 for NULL handles.
 */
static void test_lease(const char* folder, const char* root_pem, const char* server_url, const char* service_url, const char* key)
{
    char server_folder[1024];
    char service_folder[1024];
    snprintf(server_folder, sizeof server_folder, "%s-server", folder);
    snprintf(service_folder, sizeof service_folder, "%s-service", folder);

    elm_client* client = make_client(server_folder, root_pem, server_url, NULL);
    check(client != NULL, "client of the server created");
    if (client == NULL) {
        return;
    }

    elm_lease* lease = NULL;
    int64_t retry = -1;
    int32_t code = elm.client_acquire_lease(client, "pro", NULL, 1, &lease, &retry);
    check(code == ELM_SERVER_RECOVERING && lease == NULL && retry > 0, "refused while the server reclaims, with retry_after");
    if (retry > 0) {
        wait_seconds(retry + 1);
    }

    code = elm.client_acquire_lease(client, "pro", NULL, 1, &lease, &retry);
    check(code == ELM_OK && lease != NULL && retry == 0, "acquire over https");
    if (lease == NULL) {
        fprintf(stderr, "acquire: %s\n", elm.last_error_message());
        elm.client_destroy(client);
        return;
    }

    const char* id = NULL;
    const char* feature = NULL;
    int64_t expires = 0;
    check(elm.lease_id(lease, &id) == ELM_OK && id != NULL && id[0] != '\0', "lease id");
    check(elm.lease_feature_id(lease, &feature) == ELM_OK && strcmp(feature, "pro") == 0, "lease feature");
    check(elm.lease_expires_at(lease, &expires) == ELM_OK && expires > 0, "lease expiry");
    check(elm.lease_seats(lease) == 1 && elm.lease_is_borrow(lease) == 0, "lease seats and not a borrow");
    check(elm.lease_state(lease) == ELM_LEASE_HELD && elm.lease_last_error(lease) == 0, "lease held");

    int before = lease_changes;
    check(elm.client_release_lease(client, lease) == ELM_OK && elm.lease_state(lease) == ELM_LEASE_RELEASED, "release");
    check(lease_changes == before + 1 && lease_last_current == ELM_LEASE_RELEASED, "callback before release returned");
    elm.lease_free(lease);

    elm_lease* borrow = NULL;
    check(elm.client_borrow_lease(client, "pro", 3600, NULL, 1, &borrow, NULL) == ELM_OK && elm.lease_is_borrow(borrow) == 1, "borrow");
    elm_lease** borrowed = NULL;
    size_t count = 0;
    const char* borrow_id = NULL;
    const char* listed_id = NULL;
    check(elm.client_get_borrowed_leases(client, &borrowed, &count) == ELM_OK && count == 1 && elm.lease_id(borrow, &borrow_id) == ELM_OK
              && elm.lease_id(borrowed[0], &listed_id) == ELM_OK && strcmp(borrow_id, listed_id) == 0,
          "borrow listed");
    elm.lease_list_free(borrowed, count);
    elm.lease_free(borrow);

    int32_t deleted = -1;
    check(elm.client_reset_server_pin(client, server_url, &deleted) == ELM_OK && deleted == 1, "server pin deleted");
    elm.client_destroy(client);

    elm_client* hosted = make_client(service_folder, root_pem, service_url, key);
    check(hosted != NULL, "client of the service created");
    if (hosted != NULL) {
        elm_lease* cloud = NULL;
        check(elm.client_acquire_lease(hosted, "pro", NULL, 1, &cloud, NULL) == ELM_OK, "acquire from the service");
        check(elm.client_release_lease(hosted, cloud) == ELM_OK, "release to the service");
        elm.lease_free(cloud);
        deleted = -1;
        check(elm.client_reset_server_pin(hosted, service_url, &deleted) == ELM_OK && deleted == 0, "no pin for the service");
        elm.client_destroy(hosted);
    }

    elm_lease* none = NULL;
    check(elm.client_acquire_lease(NULL, "pro", NULL, 1, &none, NULL) == ELM_INVALID_ARGUMENT, "acquire with NULL");
    check(elm.client_borrow_lease(NULL, "pro", 60, NULL, 1, &none, NULL) == ELM_INVALID_ARGUMENT, "borrow with NULL");
    check(elm.client_release_lease(NULL, NULL) == ELM_INVALID_ARGUMENT, "release with NULL");
    check(elm.client_get_borrowed_leases(NULL, &borrowed, &count) == ELM_INVALID_ARGUMENT, "borrow list with NULL");
    check(elm.client_reset_server_pin(NULL, server_url, &deleted) == ELM_INVALID_ARGUMENT, "pin with NULL");
    check(elm.lease_id(NULL, &id) == ELM_INVALID_ARGUMENT && elm.lease_feature_id(NULL, &feature) == ELM_INVALID_ARGUMENT
              && elm.lease_expires_at(NULL, &expires) == ELM_INVALID_ARGUMENT,
          "lease reads with NULL");
    check(elm.lease_seats(NULL) == -1 && elm.lease_is_borrow(NULL) == -1 && elm.lease_state(NULL) == -1 && elm.lease_last_error(NULL) == -1,
          "lease values of NULL");
    elm.lease_free(NULL);
    elm.lease_list_free(NULL, 0);
    elm.client_destroy(NULL);
}

int main(int argc, char** argv)
{
    if (argc == 8 && strcmp(argv[1], "lease") == 0) {
        if (!load(argv[2])) {
            return 2;
        }

        size_t root_length = 0;
        char* root = read_file(argv[4], &root_length);
        if (root == NULL) {
            fprintf(stderr, "cannot read %s\n", argv[4]);
            return 1;
        }

        test_lease(argv[3], root, argv[5], argv[6], argv[7]);
        free(root);
        printf("1..%d\n", number);
        return failures == 0 ? 0 : 1;
    }

    if (argc == 7 && strcmp(argv[1], "activation") == 0) {
        if (!load(argv[2])) {
            return 2;
        }

        size_t root_length = 0;
        char* root = read_file(argv[4], &root_length);
        if (root == NULL) {
            fprintf(stderr, "cannot read %s\n", argv[4]);
            return 1;
        }

        test_activation(argv[3], root, argv[5], argv[6]);
        free(root);
        printf("1..%d\n", number);
        return failures == 0 ? 0 : 1;
    }

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
        fprintf(stderr, "usage: test_elasticlm thumbprint <library> <folder> | verify <library> <folder> <root.pub.pem> <signing kid> <unbound> <bound> | activation <library> <folder> <root.pub.pem> <url> <key> | lease <library> <folder> <root.pub.pem> <server url> <service url> <key>\n");
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
