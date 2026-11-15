namespace CELMapViewer.Agent;

/// <summary>
/// A user-facing error from map analysis or an agent tool (bad room name, unknown tier, read-only mode, ...).
/// Shared by both editions: the PCG and reference-map code throw it too.
/// </summary>
public sealed class ToolException(string message) : Exception(message);
