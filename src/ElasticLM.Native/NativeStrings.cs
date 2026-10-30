using System.Runtime.InteropServices;
using System.Text;

namespace ElasticLM.Native;

/// <summary>
/// UTF-8 strings across the C boundary (design §17.4): strings from C are read strictly and up to a bound; strings to C live
/// in native memory the library owns.
/// </summary>
internal static unsafe class NativeStrings
{
    /// <summary>The longest PEM read from C, in bytes before the NUL.</summary>
    public const int MaxPemBytes = 16 * 1024;

    /// <summary>The longest other string read from C, in bytes before the NUL.</summary>
    public const int MaxTextBytes = 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads a required NUL-terminated UTF-8 string from C.
    /// </summary>
    /// <exception cref="NativeException">The pointer is <see langword="null"/>, no NUL comes within the bound, or the bytes
    /// are not valid UTF-8 (<c>ELM_INVALID_ARGUMENT</c>).</exception>
    public static string Read(byte* text, string name, int maxBytes = MaxTextBytes) =>
        ReadOptional(text, name, maxBytes) ?? throw NativeException.Invalid($"{name} is required.");

    /// <summary>
    /// Reads an optional NUL-terminated UTF-8 string from C; <see langword="null"/> stays <see langword="null"/>.
    /// </summary>
    /// <exception cref="NativeException">No NUL comes within the bound, or the bytes are not valid UTF-8.</exception>
    public static string? ReadOptional(byte* text, string name, int maxBytes = MaxTextBytes)
    {
        if (text is null)
        {
            return null;
        }

        int length = 0;
        while (text[length] != 0)
        {
            if (++length > maxBytes)
            {
                throw NativeException.Invalid($"{name} is longer than {maxBytes} bytes or not terminated by NUL.");
            }
        }

        try
        {
            return Strict.GetString(text, length);
        }
        catch (DecoderFallbackException)
        {
            throw NativeException.Invalid($"{name} is not valid UTF-8.");
        }
    }

    /// <summary>
    /// Copies a string into native memory as UTF-8 with a NUL after it, and returns the pointer and the length in bytes
    /// before the NUL. The caller frees it with <see cref="Free"/>.
    /// </summary>
    public static (nint Pointer, nuint Length) Allocate(string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        byte* memory = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(text, new Span<byte>(memory, length));
        memory[length] = 0;
        return ((nint)memory, (nuint)length);
    }

    /// <summary>Frees memory from <see cref="Allocate"/>; zero is ignored.</summary>
    public static void Free(nint pointer)
    {
        if (pointer != 0)
        {
            NativeMemory.Free((void*)pointer);
        }
    }
}
