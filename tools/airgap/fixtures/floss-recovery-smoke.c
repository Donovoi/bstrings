/*
 * Benign bstrings release fixture. It decodes a fixed marker on the stack so
 * the offline bundle smoke test can prove that FLOSS recovery really ran.
 * The program performs no file, registry, process, or network operations.
 */

#include <stddef.h>
#include <stdio.h>

#if defined(_MSC_VER)
#pragma optimize("", off)
#define NOINLINE __declspec(noinline)
#else
#define NOINLINE __attribute__((noinline, optimize("O0")))
#endif

static NOINLINE int decode_marker(
    char *output,
    size_t output_length,
    const char *input,
    size_t input_length,
    unsigned char key)
{
    size_t index;

    if (output_length != input_length || output_length == 0) {
        return 1;
    }
    for (index = 0; index < output_length; ++index) {
        output[index] = (char)(input[index] ^ key);
    }
    output[output_length - 1] = '\0';
    return 0;
}

static NOINLINE int recover_marker(void)
{
    char encoded[] = "GMNRR^SDBNWDSX^NJ";
    char decoded[sizeof(encoded)] = { 0 };

    if (decode_marker(decoded, sizeof(decoded), encoded, sizeof(encoded), 0x01) != 0) {
        return 1;
    }
    return puts(decoded) < 0 ? 1 : 0;
}

int main(void)
{
    return recover_marker();
}
