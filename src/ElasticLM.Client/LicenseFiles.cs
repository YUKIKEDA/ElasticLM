namespace ElasticLM.Client;

// design §13.7, M4c3.

/// <summary>
/// Writes a server license file where <c>elm-server</c> reads it: to a temporary file beside it, then in
/// its place, so a reader never sees half a file. <c>elm-server</c> and <c>elm server</c> both use it.
/// </summary>
internal static class LicenseFiles
{
    /// <summary>Writes the file.</summary>
    /// <param name="path">The path of the server license file.</param>
    /// <param name="file">The license file.</param>
    /// <param name="time">The clock that counts the wait for a reader of the old file on Windows.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void Write(string path, byte[] file, TimeProvider time)
    {
        string full = Path.GetFullPath(path);
        string temporary = full + ".tmp";
        File.WriteAllBytes(temporary, file);
        FileReplacement.Move(temporary, full, time);
    }
}
