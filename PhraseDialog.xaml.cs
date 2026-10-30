using System;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>Asks you to type a phrase before a break in locked or extreme mode.</summary>
public partial class PhraseDialog : Window
{
    public const string Phrase = Defaults.BreakPhrase;

    public PhraseDialog()
    {
        InitializeComponent();
        PhraseText.Text = Phrase;
        Loaded += (_, _) => PhraseBox.Focus();
    }

    private bool Matches() =>
        string.Equals(PhraseBox.Text.Trim().TrimEnd('.', '!'), Phrase, StringComparison.OrdinalIgnoreCase);

    private void PhraseBox_TextChanged(object sender, TextChangedEventArgs e) => OkButton.IsEnabled = Matches();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Matches()) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
