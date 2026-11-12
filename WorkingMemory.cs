using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>working-memory.json (synced): your Working Memory notes, the same on every computer.</summary>
public class WorkingMemoryFile
{
    public string Text { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Working Memory, at the bottom of the task window: a quiet Markdown box for fragments of thought. Markdown shows formatted
/// as you type, like Obsidian: # headings, **bold**, *italic*, ~~struck~~, `code`, [[links]] and [links](…), - bullets,
/// 1. numbers, > quotes and - [ ] checkboxes (click the box, or Ctrl+Enter, to tick it). The Markdown symbols stay visible
/// but faint. Ctrl+B and Ctrl+I wrap the selection. What you type stays until you delete it, and syncs between computers.
/// </summary>
public class WorkingMemoryBox : Border
{
    private readonly RichTextBox _box = new()
    {
        AcceptsReturn = true, AcceptsTab = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(2), FontSize = 13.5
    };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _formatting;
    private DateTime _loadedUtc;

    public WorkingMemoryBox()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12, 8, 10, 8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(BorderBrushProperty, "Line");
        _box.SetResourceReference(Control.ForegroundProperty, "Text");
        _box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text");
        _box.Document.PagePadding = new Thickness(0);
        _box.Document.FontFamily = new FontFamily("Segoe UI");

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var title = new TextBlock { Text = "Working Memory", FontSize = 12, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(title);
        var stack = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        stack.Children.Add(head);
        stack.Children.Add(_box);
        Child = stack;

        Load();
        _box.TextChanged += (_, _) =>
        {
            if (_formatting) return;
            FormatAroundCaret();
            _saveTimer.Stop(); _saveTimer.Start();
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _syncTimer.Tick += (_, _) => { if (!_box.IsKeyboardFocusWithin) ReloadIfNewer(); };
        _syncTimer.Start();
        Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); } _syncTimer.Stop(); };
        _box.PreviewKeyDown += OnKey;
        // Paste as plain text (Markdown), never as formatted text from other programs.
        DataObject.AddPastingHandler(_box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string text) e.DataObject = new DataObject(DataFormats.UnicodeText, text);
            else e.CancelCommand();
        });
        _box.LostKeyboardFocus += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); } };
    }

    // ---------- Text in and out ----------

    private string Text => string.Join("\n", _box.Document.Blocks.OfType<Paragraph>().Select(ParaText));

    private static string ParaText(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private void SetText(string text)
    {
        _formatting = true;
        try
        {
            _box.Document.Blocks.Clear();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var p = new Paragraph();
                _box.Document.Blocks.Add(p);
                Format(p, line);
            }
        }
        finally { _formatting = false; }
    }

    private void Load()
    {
        if (Storage.TryLoad<WorkingMemoryFile>("working-memory.json", out var f)) { SetText(f.Text); _loadedUtc = f.UpdatedUtc; }
        else SetText("");
    }

    private void ReloadIfNewer()
    {
        try
        {
            if (Storage.TryLoad<WorkingMemoryFile>("working-memory.json", out var f) && f.UpdatedUtc > _loadedUtc && f.Text != Text)
            { SetText(f.Text); _loadedUtc = f.UpdatedUtc; }
        }
        catch (Exception ex) { Log.Error("Working memory reload", ex); }
    }

    private void Save()
    {
        try
        {
            _loadedUtc = DateTime.UtcNow;
            Storage.Save("working-memory.json", new WorkingMemoryFile { Text = Text, UpdatedUtc = _loadedUtc });
        }
        catch (Exception ex) { Log.Error("Working memory save", ex); }
    }

    // ---------- Keys: lists continue, Ctrl+B / Ctrl+I, Ctrl+Enter ticks a checkbox ----------

    private static readonly Regex ListStart = new(@"^(\s*)(- \[[ xX]\] |[-*+] |(\d+)\. )");

    private void OnKey(object sender, KeyEventArgs e)
    {
        var p = _box.CaretPosition.Paragraph;
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.B || e.Key == Key.I)) { Wrap(e.Key == Key.B ? "**" : "*"); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter && p != null) { ToggleCheck(p); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && p != null)
        {
            var m = ListStart.Match(ParaText(p));
            if (!m.Success) return;
            if (ParaText(p).Trim() == m.Value.Trim())
            {
                // An empty list item: Enter ends the list.
                _formatting = true; Format(p, ""); _formatting = false;
                _box.CaretPosition = p.ContentEnd;
                e.Handled = true;
                return;
            }
            string marker = m.Groups[3].Success ? $"{m.Groups[1].Value}{int.Parse(m.Groups[3].Value) + 1}. " : m.Groups[1].Value + (m.Groups[2].Value.StartsWith("- [") ? "- [ ] " : m.Groups[2].Value);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _box.CaretPosition.InsertTextInRun(marker);
                if (_box.CaretPosition.Paragraph is Paragraph np) { _formatting = true; Format(np, ParaText(np), marker.Length); _formatting = false; }
            }), DispatcherPriority.Input);
        }
    }

    private void Wrap(string mark)
    {
        var sel = _box.Selection;
        string text = sel.Text;
        sel.Text = mark + text + mark;
        FormatAroundCaret();
    }

    private void ToggleCheck(Paragraph p)
    {
        string text = ParaText(p);
        var m = Regex.Match(text, @"^(\s*- \[)([ xX])(\] )");
        string next = m.Success ? text[..m.Groups[2].Index] + (m.Groups[2].Value == " " ? "x" : " ") + text[(m.Groups[2].Index + 1)..]
                                : Regex.Replace(text, @"^(\s*)([-*+] )?", "$1- [ ] ");
        _formatting = true; Format(p, next, Math.Min(next.Length, CaretOffset(p) + (m.Success ? 0 : next.Length - text.Length))); _formatting = false;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    // ---------- Formatting (Obsidian-style: formatted as you type, symbols faint) ----------

    private int CaretOffset(Paragraph p)
    {
        try { return new TextRange(p.ContentStart, _box.CaretPosition).Text.Length; } catch { return 0; }
    }

    private void FormatAroundCaret()
    {
        var p = _box.CaretPosition.Paragraph;
        if (p == null) return;
        _formatting = true;
        try
        {
            if (p.PreviousBlock is Paragraph prev) Format(prev, ParaText(prev)); // Enter splits a line: both halves get their look
            Format(p, ParaText(p), CaretOffset(p));
        }
        finally { _formatting = false; }
    }

    private static readonly Regex InlineMd = new(@"(\*\*[^*\n]+\*\*)|(~~[^~\n]+~~)|(`[^`\n]+`)|(\[\[[^\]\n]+\]\])|(\[[^\]\n]+\]\([^)\n]+\))|(\*[^*\n]+\*)|(_[^_\n]+_)");

    /// <summary>Rebuilds one line's look from its text, keeping the caret where it was.</summary>
    private void Format(Paragraph p, string text, int caret = -1)
    {
        p.Inlines.Clear();
        p.Margin = new Thickness(0, 0, 0, 2);
        p.FontSize = 13.5; p.FontWeight = FontWeights.Normal; p.FontStyle = FontStyles.Normal; p.TextDecorations = null;
        p.ClearValue(TextElement.ForegroundProperty);
        string rest = text;
        var heading = Regex.Match(text, @"^(#{1,3}) ");
        var quote = Regex.Match(text, @"^> ");
        var check = Regex.Match(text, @"^(\s*)- \[([ xX])\] ");
        var list = ListStart.Match(text);
        bool done = false;
        if (heading.Success)
        {
            int level = heading.Groups[1].Length;
            p.FontSize = level == 1 ? 18 : level == 2 ? 16 : 14.5; p.FontWeight = FontWeights.SemiBold;
            p.Margin = new Thickness(0, 4, 0, 2);
            Add(p, heading.Value, "mark"); rest = text[heading.Length..];
        }
        else if (quote.Success) { Add(p, quote.Value, "mark"); rest = text[quote.Length..]; p.FontStyle = FontStyles.Italic; p.SetResourceReference(TextElement.ForegroundProperty, "Muted"); }
        else if (check.Success)
        {
            done = check.Groups[2].Value != " ";
            if (check.Groups[1].Length > 0) Add(p, check.Groups[1].Value, "");
            Add(p, "- [", "mark");
            Add(p, check.Groups[2].Value, "box");
            Add(p, "] ", "mark");
            rest = text[check.Length..];
        }
        else if (list.Success) { Add(p, list.Value, "bullet"); rest = text[list.Length..]; }

        int at = 0;
        foreach (Match m in InlineMd.Matches(rest))
        {
            if (m.Index > at) Add(p, rest[at..m.Index], done ? "done" : "");
            string v = m.Value;
            if (m.Groups[1].Success) { Add(p, "**", "mark"); Add(p, v[2..^2], "bold"); Add(p, "**", "mark"); }
            else if (m.Groups[2].Success) { Add(p, "~~", "mark"); Add(p, v[2..^2], "done"); Add(p, "~~", "mark"); }
            else if (m.Groups[3].Success) { Add(p, "`", "mark"); Add(p, v[1..^1], "code"); Add(p, "`", "mark"); }
            else if (m.Groups[4].Success) { Add(p, "[[", "mark"); Add(p, v[2..^2], "link"); Add(p, "]]", "mark"); }
            else if (m.Groups[5].Success) { int mid = v.IndexOf("](", StringComparison.Ordinal); Add(p, "[", "mark"); Add(p, v[1..mid], "link"); Add(p, v[mid..], "mark"); }
            else { Add(p, v[..1], "mark"); Add(p, v[1..^1], "italic"); Add(p, v[^1..], "mark"); }
            at = m.Index + m.Length;
        }
        if (at < rest.Length) Add(p, rest[at..], done ? "done" : "");
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run(""));

        if (caret >= 0)
        {
            // Put the caret back at the same character.
            int left = caret;
            foreach (var r in p.Inlines.OfType<Run>())
            {
                if (left <= r.Text.Length) { _box.CaretPosition = r.ContentStart.GetPositionAtOffset(left) ?? r.ContentEnd; return; }
                left -= r.Text.Length;
            }
            _box.CaretPosition = p.ContentEnd;
        }
    }

    private void Add(Paragraph p, string text, string kind)
    {
        if (text.Length == 0) return;
        var r = new Run(text);
        switch (kind)
        {
            case "mark": r.SetResourceReference(TextElement.ForegroundProperty, "Muted"); r.FontWeight = FontWeights.Normal; r.FontStyle = FontStyles.Normal; break;
            case "bullet": r.SetResourceReference(TextElement.ForegroundProperty, "FocusBrush"); break;
            case "bold": r.FontWeight = FontWeights.Bold; break;
            case "italic": r.FontStyle = FontStyles.Italic; break;
            case "done": r.TextDecorations = TextDecorations.Strikethrough; r.SetResourceReference(TextElement.ForegroundProperty, "Muted"); break;
            case "code": r.FontFamily = new FontFamily("Cascadia Mono, Consolas"); r.SetResourceReference(TextElement.BackgroundProperty, "Panel2"); break;
            case "link": r.SetResourceReference(TextElement.ForegroundProperty, "FocusBrush"); r.TextDecorations = TextDecorations.Underline; break;
            case "box":
                r.SetResourceReference(TextElement.ForegroundProperty, "FocusBrush"); r.FontWeight = FontWeights.Bold; r.Cursor = Cursors.Hand;
                r.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleCheck(p); };
                break;
        }
        p.Inlines.Add(r);
    }
}
