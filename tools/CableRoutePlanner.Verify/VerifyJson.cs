using System.Text.Json;

namespace CableRoutePlanner.Verify;

internal static class VerifyJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
