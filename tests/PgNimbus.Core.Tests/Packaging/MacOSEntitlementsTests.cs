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
        var path = Path.Combine(RepositoryRoot(), "packaging", "macos", "Entitlements.plist");
        await Assert.That(File.Exists(path)).IsTrue();

        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);

        await Assert.That(doc.DocumentElement?.Name).IsEqualTo("plist");
    }

    [Test]
    public async Task It_asks_for_library_validation_to_be_off_and_nothing_else()
    {
        var path = Path.Combine(RepositoryRoot(), "packaging", "macos", "Entitlements.plist");
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);

        var keys = doc.SelectNodes("/plist/dict/key")!.Cast<XmlNode>().Select(n => n.InnerText).ToArray();

        await Assert.That(keys).IsEquivalentTo(["com.apple.security.cs.disable-library-validation"]);
    }

    /// <summary>
    /// A NativeAOT app built with .NET 10 carries <c>minos 12.0</c>. The bundle said 11.0,
    /// so a macOS 11 Mac got a dyld failure instead of Launch Services' "requires
    /// macOS 12". The real gate is release.yml, which compares this key with the
    /// binaries' minos; this keeps a hand edit from lowering it under that floor.
    /// </summary>
    [Test]
    public async Task The_bundle_does_not_promise_a_macOS_older_than_12()
    {
        var path = Path.Combine(RepositoryRoot(), "packaging", "macos", "Info.plist.template");
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);

        var key = doc.SelectSingleNode("/plist/dict/key[text()='LSMinimumSystemVersion']")!;
        var value = key.NextSibling;
        while (value is not null && value.NodeType != XmlNodeType.Element)
        {
            value = value.NextSibling;
        }

        await Assert.That(Version.Parse(value!.InnerText)).IsGreaterThanOrEqualTo(new Version(12, 0));
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
