namespace CableRoutePlanner.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CableRoutePlanner-tests-" + Guid.NewGuid());

    public TemporaryDirectory() => Directory.CreateDirectory(DirectoryPath);

    // Do not suppress cleanup failures: a leaked SQLite handle is a defect worth seeing.
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
