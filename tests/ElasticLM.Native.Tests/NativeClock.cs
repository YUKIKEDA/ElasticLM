namespace ElasticLM.Native.Tests;

/// <summary>
/// The tests that replace <see cref="Exports.Time"/>, the clock of the verifiers and activations the library makes. They
/// share it, so they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeClock
{
    public const string Name = "Exports.Time";
}
