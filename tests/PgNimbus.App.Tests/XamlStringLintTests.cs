using System.Text.RegularExpressions;

namespace PgNimbus.App.Tests;

/// <summary>
/// No view writes interface text into its XAML: every attribute that shows text
/// takes <c>{l:Tr Key}</c> (or a binding) instead of a literal. The pseudo-locale
/// test catches what this cannot see (text set from code); this one covers every
/// view, including states no screenshot scenario reaches.
/// <para>
/// <see cref="Pending"/> lists the views not moved to <c>Strings.resx</c> yet. It
/// only shrinks: a view leaves it in the PR that converts it.
/// </para>
/// </summary>
public partial class XamlStringLintTests
{
    /// <summary>Views (file names) still holding literal text, emptied slice by slice.</summary>
    private static readonly HashSet<string> Pending =
    [
        "ActivityWindow.axaml", "AddRowDialog.axaml", "AlterTableDialog.axaml", "BackupWindow.axaml",
        "BrowseFilterBar.axaml", "CellValueView.axaml", "ColumnValueEditorView.axaml", "ConfirmDialog.axaml",
        "ConnectionDialog.axaml", "CrashWindow.axaml", "DatabaseOverviewWindow.axaml", "DatabaseSwitcherView.axaml",
        "FilterEditorView.axaml", "HostKeyDialog.axaml", "ImportDialog.axaml", "ImportPlanDialog.axaml",
        "MainWindow.axaml", "NotifyMonitorWindow.axaml", "PendingChangesDialog.axaml", "PgToolGuideView.axaml",
        "QueryEditorPanel.axaml", "QueryLabWindow.axaml", "RestoreWindow.axaml",
        "ResultsGridPanel.axaml", "RowDetailPanel.axaml", "SaveQueryDialog.axaml", "SavedQueriesPanel.axaml",
        "SchemaTreePanel.axaml", "ShortcutsView.axaml", "SlowQueriesWindow.axaml", "StagedConflictDialog.axaml",
        "App.axaml", "Theme.axaml", "BulkGrantDialog.axaml", "DefaultPrivilegesTabView.axaml", "DropRoleDialog.axaml",
        "PermissionsTabView.axaml", "RlsTabView.axaml", "RoleDialog.axaml", "RolesTabView.axaml", "SecurityWindow.axaml",
    ];

    /// <summary>Literal values that are names, never translated.</summary>
    private static readonly HashSet<string> Names = ["pgNimbus"];

    [GeneratedRegex(@"\s(Text|Content|Header|ToolTip\.Tip|PlaceholderText|Watermark|Title|OnContent|OffContent|cmd:CommandTip\.Text|AutomationProperties\.Name)=""([^""{][^""]*)""")]
    private static partial Regex TextAttribute();

    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex Word();

    [Test]
    public async Task No_converted_view_writes_text_into_its_XAML()
    {
        var found = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "PgNimbus.App"), "*.axaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Pending.Contains(name))
            {
                continue;
            }

            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in TextAttribute().Matches(lines[i]))
                {
                    var value = match.Groups[2].Value;
                    if (Word().IsMatch(value) && !Names.Contains(value))
                    {
                        found.Add($"{name}:{i + 1}: {match.Groups[1].Value}=\"{value}\"");
                    }
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(0);
        await Assert.That(found).IsEmpty().Because(string.Join(Environment.NewLine, found));
    }

    [Test]
    public async Task Every_pending_view_still_exists()
    {
        var all = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "PgNimbus.App"), "*.axaml", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToHashSet();
        await Assert.That(Pending.Where(p => !all.Contains(p))).IsEmpty();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgNimbus.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
