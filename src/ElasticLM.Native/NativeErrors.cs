using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary>
/// A refusal with a design §19 number, thrown inside an exported function and turned into its return value.
/// </summary>
internal sealed class NativeException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;

    public static NativeException Invalid(string message) => new(ErrorCode.InvalidArgument, message);
}

/// <summary>
/// The message of the last function that returns a number, for each thread (design §17.4). It is kept in a pinned array
/// on the pinned object heap, so its address does not move while C reads it, and it is collected with the thread's
/// statics.
/// </summary>
internal static unsafe class NativeErrors
{
    [ThreadStatic]
    private static byte[]? _message;

    /// <summary>Gets the message as a NUL-terminated UTF-8 string, valid until the next function that returns a number.</summary>
    public static byte* Pointer
    {
        get
        {
            _message ??= GC.AllocateArray<byte>(1, pinned: true);
            return (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_message));
        }
    }

    /// <summary>Empties the message; every function that returns a number starts with it.</summary>
    public static void Clear()
    {
        _message ??= GC.AllocateArray<byte>(1, pinned: true);
        _message[0] = 0;
    }

    /// <summary>Keeps a message and returns its number.</summary>
    public static int Fail(ErrorCode code, string message)
    {
        int length = Encoding.UTF8.GetByteCount(message);
        if (_message is null || _message.Length < length + 1)
        {
            _message = GC.AllocateArray<byte>(length + 1, pinned: true);
        }

        Encoding.UTF8.GetBytes(message, _message);
        _message[length] = 0;
        return (int)code;
    }

    /// <summary>
    /// Turns an exception into a number (design §17.3): a refusal keeps its number, an argument exception is
    /// <c>ELM_INVALID_ARGUMENT</c>, and anything else is <c>ELM_INTERNAL_ERROR</c>.
    /// </summary>
    public static int Fail(Exception exception) =>
        exception switch
        {
            NativeException refusal => Fail(refusal.Code, refusal.Message),
            ArgumentException argument => Fail(ErrorCode.InvalidArgument, argument.Message),
            _ => Fail(ErrorCode.InternalError, "An internal error occurred: " + exception.Message),
        };
}
