using System.Globalization;
using System.Windows;

namespace PtySpeak.Views;

/// <summary>
/// Cycle 43a — code-behind for the Grep diagnostics dialog.
/// <para>
/// The dialog collects four inputs (pattern, case-sensitive flag,
/// regex flag, context lines) and exposes them as read-only
/// properties after the user clicks OK. The orchestrator in
/// <c>Terminal.App/Program.fs</c> opens this dialog modally,
/// inspects the properties when <c>DialogResult == true</c>, and
/// hands them off to the F# <c>Terminal.Core.DiagnosticGrep</c>
/// module along with the assembled lightweight bundle.
/// </para>
/// <para>
/// Validation: context-lines must parse as an integer in
/// <c>[0, 20]</c>. Invalid input keeps the dialog open with an
/// inline error message; the validation TextBlock has
/// <c>LiveSetting="Assertive"</c> so NVDA reads the error
/// immediately when it becomes visible.
/// </para>
/// </summary>
public partial class GrepDialog : Window
{
    private const int MinContextLines = 0;
    private const int MaxContextLines = 20;

    public GrepDialog()
    {
        InitializeComponent();
    }

    /// <summary>The pattern the user entered in the textbox.</summary>
    public string Pattern { get; private set; } = string.Empty;

    /// <summary>Whether the case-sensitive checkbox was ticked.</summary>
    public bool CaseSensitive { get; private set; }

    /// <summary>Whether the regex checkbox was ticked.</summary>
    public bool TreatAsRegex { get; private set; }

    /// <summary>
    /// Number of context lines requested. Guaranteed to be in
    /// <c>[0, 20]</c> when the dialog returns <c>true</c>.
    /// </summary>
    public int ContextLines { get; private set; } = 5;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate context-lines first; keep the dialog open
        // with an inline error if invalid. Pattern is allowed to
        // be empty — the grep module returns a clean "empty
        // pattern" banner rather than throwing, and the
        // orchestrator surfaces a "no matches" announce. That
        // way a user who accidentally hits Enter on an empty
        // pattern gets feedback (an empty clipboard slot rather
        // than a 60KB log dump) without an in-dialog error.
        var contextText = ContextLinesTextBox.Text ?? string.Empty;
        if (!int.TryParse(
                contextText.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var context)
            || context < MinContextLines
            || context > MaxContextLines)
        {
            ValidationMessage.Text =
                $"Context lines must be a whole number between {MinContextLines} and {MaxContextLines}.";
            ValidationMessage.Visibility = Visibility.Visible;
            ContextLinesTextBox.Focus();
            ContextLinesTextBox.SelectAll();
            return;
        }

        Pattern = PatternTextBox.Text ?? string.Empty;
        CaseSensitive = CaseSensitiveCheckBox.IsChecked == true;
        TreatAsRegex = RegexCheckBox.IsChecked == true;
        ContextLines = context;

        DialogResult = true;
        Close();
    }
}
