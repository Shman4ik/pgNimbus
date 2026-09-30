using Avalonia.Controls;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// Row-level security: which tables have it, which policies apply, and who
/// bypasses them. Read-only — the one action hands a CREATE POLICY statement to
/// the editor rather than applying anything here.
/// </summary>
public partial class RlsTabView : UserControl
{
    public RlsTabView()
    {
        InitializeComponent();
    }
}
