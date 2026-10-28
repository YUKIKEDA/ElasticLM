namespace ElasticLM.Client;

/// <summary>
/// Writes a server license file where <c>elm-server</c> reads it (design §13.7, M4c3): to a temporary file beside it, then in
/// its place, so a reader never sees half a file. <c>elm-server</c> and <c>elm server</c> both use it.
/// </summary>
internal static class LicenseFiles
{
    /// <summary>Writes the file.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void Write(string path, byte[] file)
    {
        string full = Path.GetFullPath(path);
        string temporary = full + ".tmp";
        File.WriteAllBytes(temporary, file);
        File.Move(temporary, full, overwrite: true);
    }
}
