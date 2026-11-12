using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;

namespace ElasticLM.Client;

// design §13.7 (V1d2, decision 0.119).

/// <summary>
/// The socket file of an on-premises server's local administration API: the Unix domain socket <c>admin.sock</c> in the
/// server's data folder. <c>elm-server</c> listens on it while it runs; <c>elm server</c> connects to it on the same machine.
/// </summary>
internal static class AdminSocket
{
    public const string FileName = "admin.sock";

    // unix(7) gives sun_path 108 bytes on Linux, and sys/un.h 104 on macOS; .NET writes 108 for Windows
    // (UnixDomainSocketEndPoint.Windows.cs, dotnet/runtime release/10.0). One byte is the closing NUL.

    /// <summary>The oldest Windows versions that have Unix domain sockets, as the message for a system without them names
    /// them.</summary>
    public const string OldestWindows = "Windows 10 version 1803 or Windows Server 2019";

    /// <summary>Gets the longest path of a socket file that this system takes, in UTF-8 bytes.</summary>
    public static int MaxPathBytes => OperatingSystem.IsMacOS() ? 103 : 107;

    /// <summary>
    /// Returns the absolute path of the socket file of a server data folder.
    /// </summary>
    public static string PathIn(string directory) => Path.Combine(Path.GetFullPath(directory), FileName);

    /// <summary>
    /// Makes the endpoint of a socket file. .NET decides whether the path is within the system's limit, and whether the
    /// system has Unix domain sockets at all.
    /// </summary>
    /// <param name="path">The absolute path of the socket file.</param>
    /// <param name="endPoint">The endpoint.</param>
    /// <param name="problem">When there is no endpoint, one sentence that says why: the path's length and the limit, or that
    /// the system has no Unix domain sockets.</param>
    /// <returns><see langword="false"/> when the path is longer than the system takes, or the system has no Unix domain
    /// sockets.</returns>
    public static bool TryCreateEndPoint(
        string path,
        [NotNullWhen(true)] out UnixDomainSocketEndPoint? endPoint,
        [NotNullWhen(false)] out string? problem
    )
    {
        try
        {
            endPoint = new UnixDomainSocketEndPoint(path);
            problem = null;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            endPoint = null;
            problem =
                $"The path of the administration socket '{path}' is {Encoding.UTF8.GetByteCount(path)} bytes in UTF-8, and this system allows "
                + $"{MaxPathBytes}. Use a data folder with a shorter path.";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // The constructor checks the length first and then throws this when Socket.OSSupportsUnixDomainSockets is false
            // (UnixDomainSocketEndPoint.cs lines 59 to 61, dotnet/runtime release/10.0): a Windows before AF_UNIX.
            endPoint = null;
            problem =
                "This system has no Unix domain sockets, which the server's administration API needs. On Windows, "
                + $"{OldestWindows}, or later, is required.";
            return false;
        }
    }
}
