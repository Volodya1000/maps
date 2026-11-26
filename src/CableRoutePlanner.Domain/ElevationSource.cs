namespace CableRoutePlanner.Domain;

/// <summary>Provenance and native resolution of the offline elevation model.</summary>
public sealed record ElevationSource(
    string Name, string Version, string VerticalDatum, double ResolutionMetres, string Attribution, Uri LicenseUri)
{
    public string Name { get; } = RequireText(Name, nameof(Name));
    public string Version { get; } = RequireText(Version, nameof(Version));
    public string VerticalDatum { get; } = RequireText(VerticalDatum, nameof(VerticalDatum));
    public double ResolutionMetres { get; } = double.IsFinite(ResolutionMetres) && ResolutionMetres > 0
        ? ResolutionMetres
        : throw new ArgumentOutOfRangeException(nameof(ResolutionMetres), ResolutionMetres, "Разрешение должно быть конечным и положительным.");
    public string Attribution { get; } = RequireText(Attribution, nameof(Attribution));
    public Uri LicenseUri { get; } = LicenseUri is { IsAbsoluteUri: true }
        ? LicenseUri
        : throw new ArgumentException($"Требуется абсолютный URI лицензии. Получено: {LicenseUri}.", nameof(LicenseUri));

    private static string RequireText(string value, string parameterName) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Требуется непустой текст. Получено: '{value}'.", parameterName);
}
