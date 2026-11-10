using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FocusApp;

/// <summary>
/// Books practice (multiple-choice questions) and Book flashcards (the flip cards), each with Learn and Review at the top
/// right. Learn: what's due today in the day's book. Review: any book, then any lesson you've done (or all of them).
/// </summary>
public partial class MainWindow
{
    private void StartBooksPractice(bool review = false, string? bookId = null, int lesson = -2) => StartBookPractice(cards: false, review, bookId, lesson, quiet: false);

    private void StartBookFlashcards(bool review = false, string? bookId = null, int lesson = -2, bool quiet = false) => StartBookPractice(cards: true, review, bookId, lesson, quiet);

    /// <param name="lesson">A lesson's index, -1 for all lessons done, -2 for the latest one done.</param>
    private void StartBookPractice(bool cards, bool review, string? bookId, int lesson, bool quiet)
    {
        if (_lessonWindow != null) { _lessonWindow.Activate(); return; }
        try
        {
            Course.UseToday();
            if (Course.Config.Courses.Count == 0) { if (!quiet) Info("Add a book on the Training page first.", "Training"); return; }
            bool Fits(PackQuestion q) => cards ? q.Kind == "card" : q.Kind == "mc";
            string what = cards ? "Book flashcards" : "Books practice";
            List<(Course.LessonRef Lesson, PackQuestion Q)> round;

            if (!review)
            {
                int size = cards ? Math.Clamp(Course.Config.FlashcardsCount, 3, 60) : Math.Clamp(Course.Config.BooksPracticeCount, 3, 40); // Training settings, Practice
                round = Course.DueReview(200).Where(x => Fits(x.Q)).Take(size).ToList();
                if (round.Count == 0 && cards) round = Course.HardestCardsToday().Take(size).ToList();
                if (round.Count == 0)
                {
                    if (quiet) return;
                    review = true; // nothing due: show the latest lesson in Review instead
                    _toast.ShowMessage($"Nothing due in {ShortTitle(Course.Current)} today", "Here's your latest lesson to review.");
                }
            }
            else round = new();

            if (review)
            {
                Course.UseBookQuietly(bookId ?? Course.Current.Id);
                var done = Course.DoneLessons();
                var lessons = Course.Lessons().Where(l => done.Contains(l.Index)).ToList();
                if (lessons.Count == 0) { Course.UseToday(); if (!quiet) Info($"No lessons done yet in {ShortTitle(Course.Current)}.", what); return; }
                if (lesson == -2 || (lesson >= 0 && lessons.All(l => l.Index != lesson))) lesson = lessons[^1].Index;
                var from = lesson == -1 ? lessons : lessons.Where(l => l.Index == lesson).ToList();
                round = from.SelectMany(l => l.Lesson.AllQuestions.Where(Fits).Select(q => (l, q))).ToList();
                round = (lesson == -1 ? round.OrderBy(_ => Guid.NewGuid()).Take(cards ? 30 : 12) : round.OrderBy(_ => Guid.NewGuid())).ToList();
                if (round.Count == 0) { if (!quiet) Info(cards ? "That lesson has no flashcards." : "That lesson has no multiple-choice questions.", what); }
            }
            if (round.Count == 0) { Course.UseToday(); return; }

            string title = $"{what} · {ShortTitle(Course.Current)}";
            var w = cards
                ? LessonWindow.CardReview(round, TimeSpan.FromMinutes(Math.Clamp(Course.Config.CardReviewMinutes, 1, 60)), _ => { })
                : new LessonWindow(title, round, null, _ => { });
            w.AddToTopBar(ModeBar(cards, review, Course.Current.Id, lesson));
            w.Closed += (_, _) => { Course.Pinned = null; Course.UseToday(); }; // back to the day's book
            OpenTrainingWindow(w, blocking: false);
        }
        catch (Exception ex) { Log.Error("Book practice", ex); Course.UseToday(); }
    }

    /// <summary>Learn and Review, and in Review the book and lesson to choose from.</summary>
    private FrameworkElement ModeBar(bool cards, bool review, string bookId, int lesson)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        void Restart(bool r, string? book, int les) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _lessonWindow?.Close();
            StartBookPractice(cards, r, book, les, quiet: false);
        }));
        Button Mode(string text, bool on, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 6, 0) };
            if (on) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => { if (!on) click(); };
            return b;
        }
        bar.Children.Add(Mode("Learn", !review, () => Restart(false, null, -2)));
        bar.Children.Add(Mode("Review", review, () => Restart(true, Course.Current.Id, -2)));
        if (!review) return bar;

        var books = new ComboBox { MinWidth = 170, Margin = new Thickness(10, 0, 6, 0) };
        foreach (var c in Course.Config.Courses)
        {
            var item = new ComboBoxItem { Content = ShortTitle(c), Tag = c.Id };
            books.Items.Add(item);
            if (c.Id == bookId) books.SelectedItem = item;
        }
        books.SelectionChanged += (_, _) => { if (books.SelectedItem is ComboBoxItem { Tag: string id } && id != bookId) Restart(true, id, -2); };
        bar.Children.Add(books);

        var lessons = new ComboBox { MinWidth = 230, MaxDropDownHeight = 420 };
        var all = new ComboBoxItem { Content = "All lessons", Tag = -1 };
        lessons.Items.Add(all);
        var done = Course.DoneLessons();
        foreach (var l in Course.Lessons().Where(l => done.Contains(l.Index)))
            lessons.Items.Add(new ComboBoxItem { Content = $"{l.Pack.Chapter}.{l.NumberInChapter}  {l.Lesson.Title}", Tag = l.Index });
        lessons.SelectedItem = lessons.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == lesson) ?? all;
        lessons.SelectionChanged += (_, _) => { if (lessons.SelectedItem is ComboBoxItem { Tag: int les } && les != lesson) Restart(true, bookId, les); };
        bar.Children.Add(lessons);
        return bar;
    }
}
