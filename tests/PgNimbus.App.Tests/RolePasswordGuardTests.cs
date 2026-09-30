using PgNimbus.App.ViewModels.Security;
using PgNimbus.Core.Security;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The role editor sends a password only as a client-side SCRAM verifier
/// (security audit 2026-09, finding 7). Two cases the verifier cannot cover are
/// said in the dialog rather than sent (review of the audit fixes): a server
/// before PG10, which would store the verifier as the password itself, and a
/// non-ASCII password in a build without NFKC tables.
/// </summary>
public class RolePasswordGuardTests
{
    [Test]
    public async Task A_password_on_a_server_before_10_is_refused_not_sent()
    {
        var host = Fixtures.SecurityViewModel();
        host.ServerVersion = new Version(9, 6);
        var editor = RoleEditorViewModel.ForCreate(new SecurityEditor(Fixtures.DataSource), host);

        editor.Name = "app";
        editor.Password = "s3cret";
        editor.PasswordConfirm = "s3cret";

        await Assert.That(editor.ValidationMessage).Contains("PostgreSQL 10 or later");
        await Assert.That(editor.ApplyCommand.CanExecute(null)).IsFalse();

        // Without a password the same role can still be created there.
        editor.Password = "";
        editor.PasswordConfirm = "";
        await Assert.That(editor.ValidationMessage).DoesNotContain("PostgreSQL 10");
    }

    [Test]
    public async Task A_password_on_a_modern_server_is_accepted()
    {
        var host = Fixtures.SecurityViewModel();
        var editor = RoleEditorViewModel.ForCreate(new SecurityEditor(Fixtures.DataSource), host);

        editor.Name = "app";
        editor.Password = "s3cret";
        editor.PasswordConfirm = "s3cret";

        await Assert.That(editor.ValidationMessage).IsEqualTo("");
    }

    [Test]
    [Arguments("plain-ascii", false, false)]
    [Arguments("pässwörd", false, true)]
    [Arguments("pässwörd", true, false)]
    public async Task A_non_ascii_password_is_warned_about_only_without_normalisation(string password, bool normalizationAvailable, bool warned)
    {
        var warning = RoleEditorViewModel.NonAsciiPasswordWarning(password, normalizationAvailable);

        await Assert.That(warning is not null).IsEqualTo(warned);
    }
}
