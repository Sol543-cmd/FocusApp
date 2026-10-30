using System;
using System.Windows;
using System.Windows.Input;

namespace FocusApp;

/// <summary>The small "add a task" box that the quick-add key opens over whatever you're doing.</summary>
public partial class QuickAddWindow : Window
{
    private readonly Action<string> _add;
    private bool _closing;

    public QuickAddWindow(Action<string> add)
    {
        InitializeComponent();
        _add = add;
        Loaded += (_, _) =>
        {
            Activate();
            InputBox.Focus();
        };
        Deactivated += (_, _) => CloseOnce();
        Closing += (_, _) => _closing = true;
    }

    /// <summary>Closes the box (safe to call more than once, for example when clicking away while closing).</summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseOnce();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string text = InputBox.Text.Trim();
            if (text.Length == 0)
            {
                CloseOnce();
                return;
            }
            _add(text);
            AddedText.Text = "Added. Type another, or press Esc.";
            InputBox.Text = "";
        }
    }

    /// <summary>The line at the top (the personal quick-add box says "Add a personal task").</summary>
    public void SetHeading(string text) => HeadingText.Text = text;
}
