namespace DataTray.Core.Connections;

/// <summary>One connection secret in transit for the master-password re-encryption pass: which connection
/// and field it belongs to, and its current (plaintext/decrypted) value.</summary>
public sealed record ConnectionSecret(string ConnectionId, string FieldKey, string? Value);
