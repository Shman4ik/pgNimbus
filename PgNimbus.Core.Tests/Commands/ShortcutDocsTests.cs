using PgNimbus.Core.Commands;

namespace PgNimbus.Core.Tests.Commands;

/// <summary>
/// Golden-file check for the published shortcut reference: the checked-in page
/// must be exactly what <see cref="ShortcutDocs.ToMarkdown"/> produces today.
/// Set <c>PGNIMBUS_UPDATE_DOCS=1</c> to rewrite it after changing the catalog.
/// </summary>
public class ShortcutDocsTests
{
    [Test]
    public async Task GeneratedPageMatchesTheCheckedInFile()
    {
        var expected = ShortcutDocs.ToMarkdown();
        var path = Path.Combine(RepositoryRoot(), ShortcutDocs.RelativePath);

        if (Environment.GetEnvironmentVariable("PGNIMBUS_UPDATE_DOCS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, expected);
            return;
        }

        await Assert.That(File.Exists(path)).IsTrue();

        // Compare line-wise so a diff points at the row that drifted, and so a
        // checkout with CRLF line endings doesn't fail the whole file.
        var actualLines = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        await Assert.That(actualLines).IsEqualTo(expected.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task PageCoversEveryDocumentedShortcut()
    {
        var markdown = ShortcutDocs.ToMarkdown();

        foreach (var descriptor in CommandCatalog.On(CommandSurface.CheatSheet))
        {
            await Assert.That(markdown).Contains(descriptor.DisplayName);
        }
    }

    // The tests run from bin/<config>/net10.0; walk up to the directory that
    // holds the solution rather than hardcoding a relative hop count.
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
