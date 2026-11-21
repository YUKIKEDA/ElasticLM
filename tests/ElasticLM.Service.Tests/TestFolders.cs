namespace ElasticLM.Service.Tests;

/// <summary>
/// The folder under which tests make the folders that a server or a machine-scope key uses: a machine-scope folder is refused
/// when a folder above it lets other users replace what is in it (design §10.2, V1d24). On Linux and macOS it is
/// <c>.elm-test</c> in the user's home folder, of mode 0755, since <c>/tmp</c> is writable by every user. On Windows it is
/// <c>elm-test</c> in the user's local application data, since a temporary folder can carry rules for other accounts (on a
/// developer's machine, <c>%TEMP%</c> lets the accounts of a sandbox modify it).
/// </summary>
internal static class TestFolders
{
    /// <summary>Gets the folder.</summary>
    public static string Root { get; } = Make();

    private static string Make()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "elm-test");
            Directory.CreateDirectory(local);
            return local;
        }

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".elm-test");
        Directory.CreateDirectory(root);
        const UnixFileMode Mode0755 =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(root, Mode0755);
        return root;
    }
}
