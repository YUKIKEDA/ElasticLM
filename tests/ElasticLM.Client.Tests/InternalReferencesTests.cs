using ElasticLM.Licensing.Tests;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Checks that the XML documentation of <c>ElasticLM.Client</c> holds no identifier that only this repository explains (design
/// §20.7, V1c6).
/// </summary>
public sealed class InternalReferencesTests
{
    /// <summary>
    /// The XML documentation file of ElasticLM.Client, which ships in the package, holds no internal identifier.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - ElasticLM.Client.xml in the test's output folder, as the compiler wrote it for the referenced project. It holds the
    ///   types and members that are not public too.
    ///
    /// Steps:
    /// - Take the text of every member element, with every run of white space as one space.
    /// - Find the internal identifiers of each text.
    ///
    /// Expected:
    /// - The file exists and has members.
    /// - No member's text holds an internal identifier; the message names the member, the pattern, and the matched text of each.
    /// </remarks>
    [Fact]
    public void XmlDocumentation_EveryMemberOfClient_HoldsNoInternalIdentifier()
    {
        IReadOnlyList<(string Name, string Text)> members = InternalReferences.DocumentedMembers("ElasticLM.Client");

        Assert.NotEmpty(members);
        IReadOnlyList<string> found = InternalReferences.FindInMembers(members);
        Assert.True(
            found.Count == 0,
            found.Count + " internal identifiers in ElasticLM.Client.xml:" + Environment.NewLine + string.Join(Environment.NewLine, found)
        );
    }
}
