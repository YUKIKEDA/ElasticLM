/*
 * Verifies a license file offline and prints whether it is valid.
 *
 *   app <root-public-key.pem> <license.elmlic>
 *
 * A real app embeds the root public key instead of reading it from a file.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "elasticlm.h"

#define PRODUCT_ID "acme-cad"
#define FEATURE_ID "pro"

/* Reads a whole file and ends it with a NUL, so that a PEM file can be used as a string; NULL when it cannot. */
static char* read_file(const char* path, size_t* length)
{
    FILE* file = fopen(path, "rb");
    char* bytes = NULL;
    long size;
    if (file == NULL) {
        return NULL;
    }
    if (fseek(file, 0, SEEK_END) == 0 && (size = ftell(file)) >= 0 && fseek(file, 0, SEEK_SET) == 0) {
        bytes = (char*)malloc((size_t)size + 1);
        if (bytes != NULL && fread(bytes, 1, (size_t)size, file) == (size_t)size) {
            bytes[size] = '\0';
            *length = (size_t)size;
        } else {
            free(bytes);
            bytes = NULL;
        }
    }
    fclose(file);
    return bytes;
}

int main(int argc, char** argv)
{
    elm_verifier_options options = {0};
    elm_requirement requirement = {0};
    elm_verifier* verifier = NULL;
    elm_license* license = NULL;
    const char* root_keys[1];
    char* root_key;
    char* file;
    size_t root_key_length;
    size_t file_length;
    int32_t error;

    if (argc != 3) {
        fprintf(stderr, "usage: app <root-public-key.pem> <license.elmlic>\n");
        return 2;
    }
    root_key = read_file(argv[1], &root_key_length);
    if (root_key == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[1]);
        return 2;
    }
    file = read_file(argv[2], &file_length);
    if (file == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[2]);
        free(root_key);
        return 2;
    }

    root_keys[0] = root_key;
    options.struct_size = sizeof options;
    options.root_keys_pem = root_keys;
    options.root_key_count = 1;
    error = elm_verifier_create(&options, &verifier);
    if (error != ELM_OK) {
        /* The first argument is not a public key in PEM. */
        fprintf(stderr, "error: %s\n", elm_last_error_message());
        free(file);
        free(root_key);
        return 2;
    }

    requirement.struct_size = sizeof requirement;
    requirement.product_id = PRODUCT_ID;
    requirement.kind = ELM_KIND_STANDARD;
    requirement.feature_id = FEATURE_ID;
    error = elm_verifier_verify(verifier, (const uint8_t*)file, file_length, &requirement, &license);
    if (error == ELM_OK) {
        printf("valid: license %s, product %s, feature %s\n", elm_license_id(license), elm_license_product(license), FEATURE_ID);
    } else {
        printf("invalid: %d: %s\n", (int)error, elm_last_error_message());
    }

    elm_license_free(license);
    elm_verifier_destroy(verifier);
    free(file);
    free(root_key);
    return error == ELM_OK ? 0 : 1;
}
