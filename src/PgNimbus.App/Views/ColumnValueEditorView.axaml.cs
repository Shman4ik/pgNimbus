using Avalonia.Controls;

namespace PgNimbus.App.Views;

/// <summary>
/// The type-aware value input for one column, bound to a
/// <see cref="ViewModels.NewRowField"/>. See the XAML for which hosts share it.
/// </summary>
public partial class ColumnValueEditorView : UserControl
{
    public ColumnValueEditorView()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to whichever input this column's type shows.</summary>
    public void FocusInput()
    {
        Control? input = TextValueInput.IsVisible ? TextValueInput
            : BoolInput.IsVisible ? BoolInput
            : EnumInput.IsVisible ? EnumInput
            : DateInput.IsVisible ? DateInput
            : StampDateInput;
        input.Focus();
    }
}
