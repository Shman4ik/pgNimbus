using System.Xml;

namespace PgNimbus.Core.Tests.Packaging;

/// <summary>
/// The Store shows "supported languages" from the package manifest's
/// <c>Resources</c>, not from the app. 1.4.0 shipped five translations with a
/// manifest that still said <c>en-us</c> alone, so the Store page listed English
/// only. Every translation file must have its language here, and nothing else
/// may be listed.
/// </summary>
public class MsixManifestTests
{
    [Test]
    public async Task The_manifest_declares_every_language_the_app_carries()
    {
        var root = RepositoryRoot();
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(Path.Combine(root, "packaging", "msix", "Package.appxmanifest"));

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        var declared = doc.SelectNodes("/m:Package/m:Resources/m:Resource/@Language", ns)!
            .Cast<XmlNode>()
            .Select(n => n.Value!.ToLowerInvariant())
            .ToArray();

        var translations = Directory
            .GetFiles(Path.Combine(root, "src", "PgNimbus.Core", "Localization"), "Strings.*.resx")
            .Select(f => Path.GetFileNameWithoutExtension(f)["Strings.".Length..].ToLowerInvariant());
        string[] expected = ["en-us", .. translations.Order(StringComparer.Ordinal)];

        await Assert.That(declared[0]).IsEqualTo("en-us");
        await Assert.That(declared).IsEquivalentTo(expected);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgNimbus.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
