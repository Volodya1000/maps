using System.Xml.Linq;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class AndroidManifestTests
{
    [Fact]
    public void ManifestDeclaresOnlyForegroundLocationPermissions()
    {
        string path = FindManifest();
        XNamespace android = "http://schemas.android.com/apk/res/android";
        XDocument manifest = XDocument.Load(path);
        string[] permissions = manifest.Descendants("uses-permission")
            .Select(element => (string?)element.Attribute(android + "name"))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        Assert.Contains("android.permission.ACCESS_FINE_LOCATION", permissions);
        Assert.Contains("android.permission.ACCESS_COARSE_LOCATION", permissions);
        XElement? internet = manifest.Descendants("uses-permission")
            .SingleOrDefault(element => (string?)element.Attribute(android + "name")
                == "android.permission.INTERNET");
        XNamespace tools = "http://schemas.android.com/tools";
        Assert.True(internet is null || (string?)internet.Attribute(tools + "node") == "remove");
        Assert.DoesNotContain("android.permission.ACCESS_BACKGROUND_LOCATION", permissions);
    }

    private static string FindManifest()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "CableRoutePlanner.Android",
                "Properties",
                "AndroidManifest.xml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("AndroidManifest.xml not found.");
    }
}
