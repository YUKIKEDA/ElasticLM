using System.Text.Json.Serialization.Metadata;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Lists the keys of a settings file and their environment variables from the file's source-generated JSON type (design §20.7,
/// V1c2), for the tests that compare them with the settings pages of <c>elm-server</c> and <c>elm-service</c>.
/// </summary>
internal static class SettingsReference
{
    /// <summary>
    /// Returns the dotted path of every key of a settings file that holds a value, such as <c>rateLimit.perIp</c>, from its
    /// JSON property names. A key whose value is an object is a section: its keys are listed instead of it.
    /// </summary>
    /// <param name="root">The source-generated type of the settings file.</param>
    /// <returns>The paths, in the order of the type.</returns>
    public static IReadOnlyList<string> KeyPaths(JsonTypeInfo root)
    {
        var paths = new List<string>();
        Walk(root, prefix: string.Empty, paths);
        return paths;
    }

    /// <summary>
    /// Returns the environment variable of a key by the rule of design §13.8 and §14.8: <c>ELM_</c> and the path in upper case
    /// with <c>.</c> as <c>_</c>.
    /// </summary>
    /// <param name="path">The key's dotted path.</param>
    /// <returns>The variable's name.</returns>
    public static string Variable(string path) => "ELM_" + path.ToUpperInvariant().Replace('.', '_');

    private static void Walk(JsonTypeInfo type, string prefix, List<string> paths)
    {
        foreach (JsonPropertyInfo property in type.Properties)
        {
            string path = prefix + property.Name;
            JsonTypeInfo value = type.Options.GetTypeInfo(property.PropertyType);
            if (value.Kind == JsonTypeInfoKind.Object)
            {
                Walk(value, path + ".", paths);
            }
            else
            {
                paths.Add(path);
            }
        }
    }
}
