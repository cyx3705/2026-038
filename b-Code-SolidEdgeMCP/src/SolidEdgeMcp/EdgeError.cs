namespace SolidEdgeMcp;

/// <summary>An actionable error safe to show to the MCP client.</summary>
public sealed class EdgeError(string message) : Exception(message);
