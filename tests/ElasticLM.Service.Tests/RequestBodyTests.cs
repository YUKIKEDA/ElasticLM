using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The reader of request bodies that tells a sent member, a null member, and a missing member apart (design §14.5).
/// </summary>
public sealed class RequestBodyTests
{
    private const string Malformed = "malformed";

    /// <summary>
    /// A body within the limits is read; a body that is not one JSON object, nests deeper than 8, or sends a member twice is
    /// ELM_MALFORMED with the caller's message.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bodies written in the test.
    ///
    /// Steps:
    /// - Parse an object nested to depth 8 and one to depth 9; an array; a string; "not json"; an object with "a" twice.
    ///
    /// Expected:
    /// - The first is read; each other one throws BodyException with ELM_MALFORMED and the message given.
    /// </remarks>
    [Fact]
    public void Parse_OutsideTheLimits_IsMalformed()
    {
        // The object is depth 1; each array inside "a" adds one.
        using (RequestBody body = Parse("{\"a\":" + new string('[', 7) + new string(']', 7) + "}"))
        {
            Assert.True(body.Has("a"));
        }

        foreach (string json in new[] { "{\"a\":" + new string('[', 8) + new string(']', 8) + "}", "[]", "\"a\"", "not json", "{\"a\":1,\"a\":2}" })
        {
            BodyException e = Assert.Throws<BodyException>(() => Parse(json).Dispose());
            Assert.Equal((ErrorCode.Malformed, Malformed), (e.Code, e.Message));
        }
    }

    /// <summary>
    /// A member sent, sent as null, and left out are three different answers; text and numbers are checked for their type.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The body {"t":"x","n":null,"i":5,"f":1.5,"big":1e30,"s":"\ud800","o":{}}.
    ///
    /// Steps:
    /// - Read each member as text and as a number; ask for "missing".
    ///
    /// Expected:
    /// - "x"; null for "n" both ways; 5; ELM_MALFORMED for 1.5, 1e30, the lone surrogate, the object, the number read as
    ///   text, and the text read as a number; "missing" is not there; RequiredText refuses "n" and "missing".
    /// </remarks>
    [Fact]
    public void Readers_SentNullAndMissing_AreToldApart()
    {
        using RequestBody body = Parse("""{"t":"x","n":null,"i":5,"f":1.5,"big":1e30,"s":"\ud800","o":{}}""");
        JsonElement Get(string name) => body.TryGet(name, out JsonElement value) ? value : throw new InvalidOperationException(name);
        void Bad(Action read) => Assert.Equal(ErrorCode.Malformed, Assert.Throws<BodyException>(read).Code);

        Assert.Equal("x", body.Text(Get("t")));
        Assert.Null(body.Text(Get("n")));
        Assert.Null(body.Integer(Get("n")));
        Assert.Equal(5, body.Integer(Get("i")));
        Bad(() => body.Integer(Get("f")));
        Bad(() => body.Integer(Get("big")));
        Bad(() => body.Text(Get("s")));
        Bad(() => body.Text(Get("o")));
        Bad(() => body.Text(Get("i")));
        Bad(() => body.Integer(Get("t")));
        Assert.False(body.Has("missing"));
        Bad(() => body.RequiredText("n"));
        Bad(() => body.RequiredText("missing"));
    }

    private static RequestBody Parse(string json) => RequestBody.Parse(Encoding.UTF8.GetBytes(json), Malformed);
}
