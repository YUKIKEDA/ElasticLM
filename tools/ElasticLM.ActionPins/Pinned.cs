namespace ElasticLM.ActionPins;

/// <summary>
/// The parts of a pinned uses value.
/// </summary>
/// <param name="Repo">The owner and repository.</param>
/// <param name="Folder">The folder in the repository, with its leading slash, or empty.</param>
/// <param name="Sha">The commit.</param>
/// <param name="Version">The tag that the comment names.</param>
internal readonly record struct Pinned(string Repo, string Folder, string Sha, string Version);
