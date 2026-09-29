using System.Xml;

namespace PgNimbus.Core.Tests.Packaging;

/// <summary>
/// The macOS entitlements file is only read by <c>codesign</c> on the release
/// runner, so a mistake in it shows up as a red tag run, not a red build. 1.0.0
/// hit exactly that: a comment that said <c>(--options runtime)</c> is not XML
/// (a comment may not contain <c>--</c>), and <c>codesign</c> answered "Failed to
/// parse entitlements: AMFIUnserializeXML: syntax error near line 6".
/// </summary>
public class MacOSEntitlementsTests
{
    [Test]
    public async Task The_entitlements_file_is_well_formed_XML()
    {
        var path = Path.Combine(RepositoryRoot(), "installer", "macos", "Entitlements.plist");
        await Assert.That(File.Exists(path)).IsTrue();

        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);

        await Assert.That(doc.DocumentElement?.Name).IsEqualTo("plist");
    }

    [Test]
    public async Task It_asks_for_library_validation_to_be_off_and_nothing_else()
    {
        var path = Path.Combine(RepositoryRoot(), "installer", "macos", "Entitlements.plist");
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);

        var keys = doc.SelectNodes("/plist/dict/key")!.Cast<XmlNode>().Select(n => n.InnerText).ToArray();

        await Assert.That(keys).IsEquivalentTo(["com.apple.security.cs.disable-library-validation"]);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
