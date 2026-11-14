using ElasticLM.Client;

namespace ElasticLM.Server.Tests;

/// <summary>
/// Reads the result of a lease call in a test. A refusal puts its code and message into the failure, so that a run that fails
/// once says why (Issue #414).
/// </summary>
internal static class LeaseResults
{
    /// <summary>Returns the lease of an acquired result, and fails the test with the refusal's code and message otherwise.</summary>
    /// <param name="result">The result of an acquisition or a borrow.</param>
    /// <returns>The lease.</returns>
    public static Lease Acquired(LeaseResult result)
    {
        if (result is LeaseResult.Failed failed)
        {
            Assert.Fail($"The lease was refused: {failed.Error.Code}: {failed.Error.Message}");
        }

        return Assert.IsType<LeaseResult.Acquired>(result).Lease;
    }
}
