using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace FocusApp;

// ======================= Chapter packs =======================

/// <summary>Where an answer is in the book: a lesson part and a few words from the answering sentence.</summary>
public class PackRef
{
    [JsonPropertyName("part")] public int Part { get; set; }
    [JsonPropertyName("find")] public string Find { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>A question: multiple choice ("mc"), memory card ("card") or putting things in order ("order").</summary>
public class PackQuestion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "mc";
    [JsonPropertyName("concept")] public string Concept { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
    [JsonPropertyName("answer")] public int Answer { get; set; }
    [JsonPropertyName("items")] public List<string> Items { get; set; } = new();
    [JsonPropertyName("front")] public string Front { get; set; } = "";
    [JsonPropertyName("back")] public string Back { get; set; } = "";
    [JsonPropertyName("why")] public string Why { get; set; } = "";
    [JsonPropertyName("refs")] public List<PackRef> Refs { get; set; } = new();
}

public class PackPart
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>The heading (or first words) where this part starts in the book.</summary>
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("watch")] public List<string> Watch { get; set; } = new();
}

public class PackLesson
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("bigQuestions")] public List<string> BigQuestions { get; set; } = new();
    [JsonPropertyName("parts")] public List<PackPart> Parts { get; set; } = new();
    [JsonPropertyName("warmup")] public List<PackQuestion> Warmup { get; set; } = new();
    [JsonPropertyName("practice")] public List<PackQuestion> Practice { get; set; } = new();
    [JsonPropertyName("cards")] public List<PackQuestion> Cards { get; set; } = new();
    [JsonPropertyName("quiz")] public List<PackQuestion> Quiz { get; set; } = new();
    [JsonPropertyName("endsChapter")] public bool EndsChapter { get; set; }

    [JsonIgnore] public IEnumerable<PackQuestion> AllQuestions => Warmup.Concat(Practice).Concat(Cards).Concat(Quiz);
}

/// <summary>A chapter pack (*.focuspack): one chapter's lessons and questions. The reading itself comes from your book file.</summary>
public class ChapterPack
{
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("course")] public string Course { get; set; } = "";
    [JsonPropertyName("book")] public string Book { get; set; } = "";
    [JsonPropertyName("chapter")] public int Chapter { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("startText")] public string StartText { get; set; } = "";
    [JsonPropertyName("endBefore")] public string? EndBefore { get; set; }
    [JsonPropertyName("lessons")] public List<PackLesson> Lessons { get; set; } = new();

    public static ChapterPack Read(string path)
    {
        var pack = JsonSerializer.Deserialize<ChapterPack>(File.ReadAllText(path)) ?? throw new InvalidDataException("The file is empty.");
        if (pack.Chapter < 1 || pack.Lessons.Count == 0) throw new InvalidDataException("This doesn't look like a chapter pack (no chapter number or lessons).");
        return pack;
    }
}

// ======================= The book (from your Word file) =======================

public class BookRun
{
    public string Text { get; set; } = "";
    public bool Italic { get; set; }
    public bool Bold { get; set; }
    public bool Super { get; set; }
}

/// <summary>One paragraph of the book, with its formatting.</summary>
public class BookPara
{
    public int Heading { get; set; }          // 0 = body text, 1-3 = heading level
    public bool Sidebar { get; set; }         // shaded box in the book
    public bool InTable { get; set; }
    /// <summary>A picture or map from the book (a file in the course's media folder), if this paragraph holds one.</summary>
    public string? Image { get; set; }
    public List<BookRun> Runs { get; set; } = new();
    [JsonIgnore] public string Text => string.Concat(Runs.Select(r => r.Text));
}

public static class BookReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Reads a .docx file into paragraphs, keeping italics, bold, superscript, headings, shaded sidebars and tables.</summary>
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Reads a .docx file into paragraphs; pictures (PNG, JPEG, GIF, BMP) are saved into <paramref name="mediaDir"/>.</summary>
    public static List<BookPara> ReadDocx(string path, string? mediaDir = null)
    {
        using var zip = ZipFile.OpenRead(path);
        var rels = new Dictionary<string, string>();
        if (zip.GetEntry("word/_rels/document.xml.rels") is ZipArchiveEntry relEntry)
        {
            using var rs = relEntry.Open();
            foreach (var r in XDocument.Load(rs).Descendants(Rel + "Relationship"))
                rels[(string?)r.Attribute("Id") ?? ""] = (string?)r.Attribute("Target") ?? "";
        }
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("This isn't a Word (.docx) file.");
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        // Styles: which style ids are headings, and which carry shading.
        var headingOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shadedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.GetEntry("word/styles.xml") is ZipArchiveEntry stylesEntry)
        {
            using var ss = stylesEntry.Open();
            foreach (var st in XDocument.Load(ss).Descendants(W + "style"))
            {
                string id = (string?)st.Attribute(W + "styleId") ?? "";
                string name = (string?)st.Element(W + "name")?.Attribute(W + "val") ?? id;
                int level = HeadingLevel(name);
                var outline = st.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
                if (level == 0 && outline != null && int.TryParse(outline.Value, out int ol) && ol < 3) level = ol + 1;
                if (level > 0) headingOf[id] = level;
                if (IsShaded(st.Element(W + "pPr")?.Element(W + "shd"))) shadedStyles.Add(id);
            }
        }

        var paras = new List<BookPara>();
        var body = doc.Root?.Element(W + "body");
        if (body == null) return paras;
        foreach (var p in body.Descendants(W + "p"))
        {
            var pPr = p.Element(W + "pPr");
            string style = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
            var para = new BookPara
            {
                Heading = headingOf.TryGetValue(style, out int h) ? h : HeadingLevel(style),
                Sidebar = IsShaded(pPr?.Element(W + "shd")) || shadedStyles.Contains(style) || p.Ancestors(W + "tc").Any(tc => IsShaded(tc.Element(W + "tcPr")?.Element(W + "shd"))),
                InTable = p.Ancestors(W + "tbl").Any()
            };
            foreach (var r in p.Descendants(W + "r"))
            {
                var rPr = r.Element(W + "rPr");
                var text = new StringBuilder();
                foreach (var e in r.Elements())
                {
                    if (e.Name == W + "t") text.Append(e.Value);
                    else if (e.Name == W + "tab") text.Append('\t');
                    else if (e.Name == W + "br" || e.Name == W + "cr") text.Append('\n');
                    else if (e.Name == W + "noBreakHyphen") text.Append('‑');
                }
                if (text.Length == 0) continue;
                var run = new BookRun
                {
                    Text = text.ToString(),
                    Italic = On(rPr?.Element(W + "i")),
                    Bold = On(rPr?.Element(W + "b")),
                    Super = (string?)rPr?.Element(W + "vertAlign")?.Attribute(W + "val") == "superscript"
                };
                var last = para.Runs.LastOrDefault();
                if (last != null && last.Italic == run.Italic && last.Bold == run.Bold && last.Super == run.Super) last.Text += run.Text;
                else para.Runs.Add(run);
            }
            if (mediaDir != null)
                foreach (var blip in p.Descendants(A + "blip"))
                {
                    string rid = (string?)blip.Attribute(R + "embed") ?? "";
                    if (!rels.TryGetValue(rid, out var target)) continue;
                    string ext = Path.GetExtension(target).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")) continue;
                    var img = zip.GetEntry("word/" + target.TrimStart('/').Replace("word/", "")) ?? zip.GetEntry(target.TrimStart('/'));
                    if (img == null) continue;
                    Directory.CreateDirectory(mediaDir);
                    string name = Path.GetFileName(target);
                    img.ExtractToFile(Path.Combine(mediaDir, name), overwrite: true);
                    paras.Add(new BookPara { Image = name, Sidebar = para.Sidebar });
                }
            if (para.Text.Trim().Length > 0) paras.Add(para);
        }
        return paras;
    }

    private static int HeadingLevel(string name)
    {
        var n = name.Replace(" ", "").ToLowerInvariant();
        if (n == "title") return 1;
        if (n.StartsWith("heading") && int.TryParse(n[7..], out int l)) return Math.Clamp(l, 1, 3);
        return 0;
    }

    private static bool On(XElement? e) => e != null && (string?)e.Attribute(W + "val") is not ("0" or "false");

    private static bool IsShaded(XElement? shd)
    {
        string fill = ((string?)shd?.Attribute(W + "fill") ?? "").ToUpperInvariant();
        return fill.Length > 0 && fill != "AUTO" && fill != "FFFFFF";
    }

    /// <summary>Text compared loosely: lower case, plain quotes and dashes, single spaces.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c0 in s.ToLowerInvariant())
        {
            char c = c0 switch
            {
                '‘' or '’' or '‛' or '′' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '–' or '—' or '‑' or '‒' => '-',
                '\u00AD' => '\0',
                _ => c0
            };
            if (c == '\0') continue;
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The first paragraph at or after <paramref name="from"/> (and before <paramref name="to"/>) containing the text; headings first when asked.</summary>
    public static int Find(List<BookPara> book, string text, int from = 0, int to = int.MaxValue, bool preferHeading = false)
    {
        string want = Normalize(text);
        if (want.Length == 0) return -1;
        to = Math.Min(to, book.Count);
        if (preferHeading)
            for (int i = Math.Max(0, from); i < to; i++)
                if (book[i].Heading > 0 && Normalize(book[i].Text).Contains(want)) return i;
        for (int i = Math.Max(0, from); i < to; i++)
            if (Normalize(book[i].Text).Contains(want)) return i;
        return -1;
    }
}

// ======================= Progress (one log per device) =======================

/// <summary>One thing that happened: an answer, a finished lesson, a hidden or flagged question.</summary>
public class TrainingEvent
{
    public string Device { get; set; } = "";
    /// <summary>The book this belongs to (empty in logs from before several books: the first book, desilva-nt).</summary>
    public string CourseId { get; set; } = "";
    public long Seq { get; set; }
    public DateTime At { get; set; }
    public string Day { get; set; } = "";          // the local date it counts toward (yyyy-MM-dd)
    public string Kind { get; set; } = "";         // answer, lessonDone, hide, unhide, flag
    public string Question { get; set; } = "";
    public string Concept { get; set; } = "";
    public bool Correct { get; set; }
    public bool Sure { get; set; }
    public int Lesson { get; set; } = -1;          // index in the course's lesson order
    public string Note { get; set; } = "";
    // Highlights: the chapter, the paragraph (in the book), the exact words and the color.
    public int Chapter { get; set; }
    public int Para { get; set; } = -1;
    public string Quote { get; set; } = "";
    public string Color { get; set; } = "";
    /// <summary>A memory card's grade: 0 = ✕ (again in this lesson), 1 = ✓ (know it fairly well), 2 = ✓✓ (know it well); -1 for older answers and multiple choice.</summary>
    public int Grade { get; set; } = -1;
}

/// <summary>One book in your library.</summary>
public class CourseInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string BookFileName { get; set; } = "";
    public int Chapters { get; set; } = 24;
    /// <summary>The last chapter of each part of the book; a unit exam follows each one.</summary>
    public List<int> UnitEnds { get; set; } = new();
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Training settings (for every book) and your library, shared between your devices.</summary>
public class CourseConfig
{
    public List<CourseInfo> Courses { get; set; } = new();
    /// <summary>The book whose lessons come up each day.</summary>
    public string ActiveId { get; set; } = "";
    // From before the library (one book only); moved into Courses when first loaded.
    public string Id { get; set; } = "desilva-nt";
    public string Title { get; set; } = "deSilva, An Introduction to the New Testament";
    public string BookFileName { get; set; } = "";
    public int Chapters { get; set; } = 24;
    /// <summary>The three daily times; the last one must be finished.</summary>
    public List<string> Times { get; set; } = new() { "07:00", "13:00", "19:30" };
    public List<string> BlockListIds { get; set; } = new();
    public int ReviewCap { get; set; } = 8;
    public bool On { get; set; } = true;
    /// <summary>Read in pages (like a book) instead of scrolling.</summary>
    public bool PageView { get; set; }
    public List<int> UnitEnds { get; set; } = new();
    /// <summary>All-books practice: its times, and whether it pops up by itself.</summary>
    public List<string> AllBooksTimes { get; set; } = new() { "16:00" };
    public bool AllBooksAuto { get; set; } = true;

    // ----- How the reading looks (Training settings, or Aa while reading) -----
    /// <summary>The reading's font (any font installed on the PC).</summary>
    public string ReadingFont { get; set; } = "Georgia";
    public double ReadingSize { get; set; } = 17;
    /// <summary>The reading's background: theme (follows Focus), paper, sepia, white, sage, gray, slate or night.</summary>
    public string ReadingBg { get; set; } = "theme";

    // ----- Evening card review: a short round of the day's hardest memory cards -----
    public bool CardReviewOn { get; set; } = true;
    /// <summary>When it pops up (set on the practice timelines).</summary>
    public List<string> CardReviewTimes { get; set; } = new() { "20:30" };
    public int CardReviewMinutes { get; set; } = 5;
}

public enum ConceptStage { New, Learning, Learned, Retired }

/// <summary>
/// The book course: config, the book's paragraphs, imported packs and every device's progress log.
/// Files live in training\ in the synced Focus folder. Each device appends only to its own
/// progress-&lt;device&gt;.json, so no two devices ever write the same file.
/// </summary>
public static class Course
{
    public static string Device { get; set; } = Environment.MachineName;
    public static CourseConfig Config { get; private set; } = new();
    public static List<BookPara> Book { get; private set; } = new();
    public static SortedDictionary<int, ChapterPack> Packs { get; } = new();
    public static List<TrainingEvent> Events { get; private set; } = new();
    public static event Action? Changed;

    private static string Dir => Path.Combine(Storage.SharedFolder, "training");
    private static string CourseDir => Path.Combine(Dir, Safe(Current.Id));

    /// <summary>The open book (the one whose lessons come up each day).</summary>
    public static CourseInfo Current { get; private set; } = new() { Id = "desilva-nt", Title = "deSilva, An Introduction to the New Testament" };

    private static string CourseOf(TrainingEvent e) => e.CourseId.Length == 0 ? "desilva-nt" : e.CourseId;
    /// <summary>The open book's events only, so lessons, review and notes never mix books.</summary>
    private static IEnumerable<TrainingEvent> CE => Events.Where(e => CourseOf(e) == Current.Id);
    public static string MediaDir => Path.Combine(CourseDir, "media");
    private static string MyLog => Path.Combine(Dir, $"progress-{Safe(Device)}.json");
    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string cfg = Path.Combine(Dir, "course.json");
            Config = File.Exists(cfg) ? JsonSerializer.Deserialize<CourseConfig>(File.ReadAllText(cfg)) ?? new() : new();
            if (Config.Courses.Count == 0 && Directory.Exists(Path.Combine(Dir, Config.Id)))
            {
                // The first book, from before the library.
                Config.Courses.Add(new CourseInfo { Id = Config.Id, Title = Config.Title, BookFileName = Config.BookFileName, Chapters = Config.Chapters, UnitEnds = Config.UnitEnds });
                Config.ActiveId = Config.Id;
                SaveConfig();
            }
            Current = Config.Courses.FirstOrDefault(c => c.Id == Config.ActiveId) ?? Config.Courses.FirstOrDefault() ?? Current;
            LoadCurrent();
            ReloadEvents();
        }
        catch (Exception ex) { Log.Error("Course.Load", ex); }
    }

    public static bool HasBook => Config.Courses.Any(c => c.Id == Current.Id);

    /// <summary>Opens a book from the library; its lessons become the daily ones.</summary>
    public static void Open(string id)
    {
        if (Config.Courses.FirstOrDefault(c => c.Id == id) is not CourseInfo c) return;
        Current = c;
        Config.ActiveId = id;
        SaveConfig();
        LoadCurrent();
    }

    /// <summary>Adds a book to the library and opens it.</summary>
    public static CourseInfo AddCourse(string title)
    {
        string baseId = string.Concat(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        if (baseId.Length == 0) baseId = "book";
        if (baseId.Length > 40) baseId = baseId[..40].Trim('-');
        string id = baseId;
        for (int n = 2; Config.Courses.Any(c => c.Id == id); n++) id = $"{baseId}-{n}";
        var info = new CourseInfo { Id = id, Title = title.Trim() };
        Config.Courses.Add(info);
        Open(id);
        return info;
    }

    /// <summary>Takes a book out of the library (its files and your progress stay in the training folder).</summary>
    public static void RemoveCourse(string id)
    {
        Config.Courses.RemoveAll(c => c.Id == id);
        if (Config.ActiveId == id) Config.ActiveId = Config.Courses.FirstOrDefault()?.Id ?? "";
        SaveConfig();
        if (Config.Courses.FirstOrDefault(c => c.Id == Config.ActiveId) is CourseInfo next) Open(next.Id);
    }

    private static void LoadCurrent()
    {
        try
        {
            Directory.CreateDirectory(CourseDir);
            string book = Path.Combine(CourseDir, "book.json");
            Book = File.Exists(book) ? JsonSerializer.Deserialize<List<BookPara>>(File.ReadAllText(book)) ?? new() : new();
            Packs.Clear();
            foreach (var f in Directory.GetFiles(CourseDir, "ch*.focuspack"))
            {
                try { var p = ChapterPack.Read(f); Packs[p.Chapter] = p; }
                catch (Exception ex) { Log.Error("Course.LoadPack " + f, ex); }
            }
        }
        catch (Exception ex) { Log.Error("Course.LoadCurrent", ex); }
        Changed?.Invoke();
    }

    /// <summary>Reads every device's log (others may have added to theirs through OneDrive).</summary>
    public static void ReloadEvents(bool raiseChanged = false)
    {
        var all = new List<TrainingEvent>();
        if (Directory.Exists(Dir))
            foreach (var f in Directory.GetFiles(Dir, "progress-*.json"))
            {
                if (f.Contains("conflict", StringComparison.OrdinalIgnoreCase)) continue;
                try { all.AddRange(JsonSerializer.Deserialize<List<TrainingEvent>>(File.ReadAllText(f)) ?? new()); }
                catch (Exception ex) { Log.Error("Course.ReadLog " + f, ex); }
            }
        int before = Events.Count;
        Events = all.OrderBy(e => e.At).ThenBy(e => e.Device).ThenBy(e => e.Seq).ToList();
        if (Events.Count != before && raiseChanged) Changed?.Invoke(); // another device added something
    }

    public static void SaveConfig()
    {
        Directory.CreateDirectory(Dir);
        WriteAtomic(Path.Combine(Dir, "course.json"), JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Add(TrainingEvent e)
    {
        e.Device = Device;
        if (e.CourseId.Length == 0) e.CourseId = Current.Id;
        e.At = DateTime.UtcNow;
        if (e.Day.Length == 0) e.Day = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mine = Events.Where(x => x.Device == Device).ToList();
        e.Seq = mine.Count == 0 ? 1 : mine.Max(x => x.Seq) + 1;
        mine.Add(e);
        Events.Add(e);
        try { Directory.CreateDirectory(Dir); WriteAtomic(MyLog, JsonSerializer.Serialize(mine, Json)); }
        catch (Exception ex) { Log.Error("Course.Add", ex); }
        Changed?.Invoke();
    }

    // ---------- Setting up ----------

    public static int ImportBook(string docxPath)
    {
        var paras = BookReader.ReadDocx(docxPath, MediaDir);
        if (paras.Count < 20) throw new InvalidDataException("Focus found almost no text in that file.");
        Directory.CreateDirectory(CourseDir);
        WriteAtomic(Path.Combine(CourseDir, "book.json"), JsonSerializer.Serialize(paras, Json));
        Book = paras;
        Current.BookFileName = Path.GetFileName(docxPath);
        SaveConfig();
        return paras.Count;
    }

    /// <summary>Checks a pack against the book; returns the problems found (empty means it's fine).</summary>
    public static List<string> Check(ChapterPack pack, out string summary)
    {
        var problems = new List<string>();
        int lessons = pack.Lessons.Count, questions = pack.Lessons.Sum(l => l.AllQuestions.Count());
        summary = $"Chapter {pack.Chapter}: {pack.Title}. {lessons} lesson{(lessons == 1 ? "" : "s")}, {questions} questions.";
        if (Book.Count == 0) { problems.Add("Choose your book's Word file first, so Focus can find each lesson's reading."); return problems; }
        var (start, end) = ChapterRange(pack);
        if (start < 0) { problems.Add($"Couldn't find where chapter {pack.Chapter} starts (\"{pack.StartText}\")."); return problems; }
        foreach (var lesson in pack.Lessons)
        {
            for (int pi = 0; pi < lesson.Parts.Count; pi++)
                if (FindPart(lesson, pi, start, end) < 0) problems.Add($"Part \"{lesson.Parts[pi].Title}\" wasn't found in chapter {pack.Chapter}.");
            foreach (var q in lesson.AllQuestions)
            {
                if (q.Kind == "mc" && (q.Options.Count < 2 || q.Answer < 0 || q.Answer >= q.Options.Count)) problems.Add($"Question {q.Id} has no valid answer.");
                if (q.Kind == "card" && (q.Front.Length == 0 || q.Back.Length == 0)) problems.Add($"Card {q.Id} is missing a side.");
                foreach (var r in q.Refs)
                    if (BookReader.Find(Book, r.Find, start, end) < 0) problems.Add($"{q.Id}: the text \"{r.Find}\" wasn't found in the chapter.");
            }
        }
        return problems;
    }

    public static void AddPack(string sourcePath, ChapterPack pack)
    {
        Directory.CreateDirectory(CourseDir);
        File.Copy(sourcePath, Path.Combine(CourseDir, $"ch{pack.Chapter:00}.focuspack"), overwrite: true);
        Packs[pack.Chapter] = pack;
        Changed?.Invoke();
    }

    public static (int Start, int End) ChapterRange(ChapterPack pack)
    {
        int start = BookReader.Find(Book, pack.StartText, 0, int.MaxValue, preferHeading: true);
        if (start < 0) return (-1, -1);
        int end = string.IsNullOrWhiteSpace(pack.EndBefore) ? -1 : BookReader.Find(Book, pack.EndBefore!, start + 1, int.MaxValue, preferHeading: true);
        return (start, end < 0 ? Book.Count : end);
    }

    // ---------- Lessons in order ----------

    public record LessonRef(int Index, ChapterPack Pack, PackLesson Lesson, int NumberInChapter);

    /// <summary>Every imported lesson, chapter by chapter, stopping at the first chapter without a pack.</summary>
    public static List<LessonRef> Lessons()
    {
        var list = new List<LessonRef>();
        int expected = Packs.Count == 0 ? 1 : Packs.Keys.Min();
        foreach (var (chapter, pack) in Packs)
        {
            if (chapter != expected) break;
            for (int i = 0; i < pack.Lessons.Count; i++) list.Add(new LessonRef(list.Count, pack, pack.Lessons[i], i + 1));
            expected++;
        }
        return list;
    }

    public static HashSet<int> DoneLessons() => CE.Where(e => e.Kind == "lessonDone" && e.Lesson >= 0).Select(e => e.Lesson).ToHashSet();

    public static string Today => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>True once any device has finished a lesson today.</summary>
    public static bool DoneToday() => CE.Any(e => e.Kind == "lessonDone" && e.Day == Today);

    /// <summary>The next lesson to do (the first one not finished), or null if none is imported.</summary>
    public static LessonRef? NextLesson()
    {
        var done = DoneLessons();
        return Lessons().FirstOrDefault(l => !done.Contains(l.Index));
    }

    // ---------- Finishing a lesson later (progress saved step by step, on every device) ----------

    /// <summary>The steps of a lesson already finished ("reading", "practice:id", "card:id"…), from any device and any day.</summary>
    public static HashSet<string> StepsDone(int lessonIndex) =>
        CE.Where(e => e.Kind == "lessonStep" && e.Lesson == lessonIndex).Select(e => e.Question).ToHashSet();

    public static void MarkStep(int lessonIndex, string key) => Add(new TrainingEvent { Kind = "lessonStep", Lesson = lessonIndex, Question = key });

    /// <summary>True when the next lesson was started (here or on another device) but isn't finished.</summary>
    public static bool InProgress(LessonRef? lesson) => lesson != null && !DoneLessons().Contains(lesson.Index) && StepsDone(lesson.Index).Count > 0;

    // ---------- Evening card review ----------

    public static bool CardReviewDoneToday() => CE.Any(e => e.Kind == "cardReviewDone" && e.Day == Today);

    /// <summary>
    /// Today's lesson cards, hardest first: cards marked ✕ today (most often first), then ✓ (fairly well).
    /// Cards whose last grade today was ✓✓ (know it well) and that were never missed today are left out.
    /// </summary>
    public static List<(LessonRef Lesson, PackQuestion Q)> HardestCardsToday(int max = 40)
    {
        var lessonIdx = CE.Where(e => e.Day == Today && e.Lesson >= 0 && (e.Kind == "lessonDone" || e.Kind == "lessonStep")).Select(e => e.Lesson).ToHashSet();
        if (lessonIdx.Count == 0) return new();
        var hidden = Hidden();
        var todayAnswers = CE.Where(e => e.Kind == "answer" && e.Day == Today).GroupBy(e => e.Question).ToDictionary(g => g.Key, g => g.ToList());
        var list = new List<(LessonRef, PackQuestion, int Score)>();
        foreach (var l in Lessons().Where(l => lessonIdx.Contains(l.Index)))
            foreach (var q in l.Lesson.Cards)
            {
                if (hidden.Contains(q.Id) || !todayAnswers.TryGetValue(q.Id, out var answers)) continue;
                int misses = answers.Count(a => a.Grade == 0 || (a.Grade < 0 && !a.Correct));
                var last = answers[^1];
                int lastGrade = last.Grade >= 0 ? last.Grade : last.Correct ? 1 : 0;
                if (lastGrade == 2 && misses == 0) continue;
                list.Add((l, q, misses * 2 + (lastGrade == 0 ? 2 : lastGrade == 1 ? 1 : 0)));
            }
        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, max)).Select(x => (x.Item1, x.Item2)).ToList();
    }

    /// <summary>The paragraphs a lesson reads: from its first part to the next lesson's first part (or the chapter's end).</summary>
    public static (int Start, int End) LessonRange(LessonRef lesson)
    {
        var (cs, ce) = ChapterRange(lesson.Pack);
        if (cs < 0) return (-1, -1);
        int idx = lesson.NumberInChapter - 1;
        int start = idx == 0 ? cs : FirstPart(lesson.Lesson, cs, ce) ?? cs;
        int end = ce;
        if (idx + 1 < lesson.Pack.Lessons.Count && FirstPart(lesson.Pack.Lessons[idx + 1], start + 1, ce) is int next) end = next;
        return (start, end);
    }

    private static int? FirstPart(PackLesson l, int from, int to)
    {
        if (l.Parts.Count == 0) return null;
        int i = FindPart(l, 0, from, to);
        return i < 0 ? null : i;
    }

    private static readonly HashSet<string> Stop = new() { "the", "and", "for", "from", "with", "into", "of", "a", "an", "to", "in", "on" };

    /// <summary>
    /// Where a lesson part starts: its heading as written, else a heading with all the title's main words
    /// (so "Lists, councils and criteria" matches "Lists, Councils, and Criteria"), else the earliest
    /// passage one of the part's questions points to.
    /// </summary>
    public static int FindPart(PackLesson l, int part, int from, int to)
    {
        if (part < 0 || part >= l.Parts.Count) return -1;
        var p = l.Parts[part];
        int exact = BookReader.Find(Book, p.Start.Length > 0 ? p.Start : p.Title, from, to, preferHeading: true);
        if (exact >= 0 && Book[exact].Heading > 0) return exact;
        var words = BookReader.Normalize(p.Title.Length > 0 ? p.Title : p.Start)
            .Split(new[] { ' ', ',', '.', ':', ';', '"', '\'', '(', ')', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).ToList();
        if (words.Count > 0)
            for (int k = Math.Max(0, from); k < Math.Min(to, Book.Count); k++)
                if (Book[k].Heading > 0 && Book[k].Text.Length < 200)
                {
                    string t = BookReader.Normalize(Book[k].Text);
                    if (words.All(w => t.Contains(w))) return k;
                }
        if (exact >= 0) return exact;
        var refs = l.AllQuestions.SelectMany(q => q.Refs).Where(r => r.Part == part)
                    .Select(r => BookReader.Find(Book, r.Find, from, to)).Where(x => x >= 0).ToList();
        return refs.Count > 0 ? refs.Min() : -1;
    }

    /// <summary>Where an answer is: the paragraph index for a reference, searched within the lesson's chapter.</summary>
    public static int Locate(LessonRef lesson, PackRef r)
    {
        var (cs, ce) = ChapterRange(lesson.Pack);
        if (cs < 0) return -1;
        int from = cs;
        if (r.Part >= 0 && r.Part < lesson.Lesson.Parts.Count)
        {
            int p = FindPart(lesson.Lesson, r.Part, cs, ce);
            if (p >= 0) from = p;
        }
        int i = BookReader.Find(Book, r.Find, from, ce);
        return i >= 0 ? i : BookReader.Find(Book, r.Find, cs, ce);
    }

    // ---------- Hidden, flagged, and review ----------

    public static HashSet<string> Hidden()
    {
        var set = new HashSet<string>();
        foreach (var e in CE)
            if (e.Kind == "hide") set.Add(e.Question);
            else if (e.Kind == "unhide") set.Remove(e.Question);
        return set;
    }

    public static IEnumerable<string> Flagged() => CE.Where(e => e.Kind == "flag").Select(e => e.Question).Distinct();

    /// <summary>A question's review state, worked out from every answer on every device, in time order.</summary>
    public record QState(DateTime Due, int Streak, bool Misconception, int Seen);

    public static QState StateOf(string questionId)
    {
        DateTime due = DateTime.MinValue;
        int streak = 0, seen = 0;
        bool miscon = false;
        int[] gaps = { 1, 3, 7, 14, 30, 60, 120 };
        foreach (var e in CE.Where(e => e.Kind == "answer" && e.Question == questionId))
        {
            seen++;
            var day = DateTime.TryParse(e.Day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : e.At.ToLocalTime().Date;
            if (e.Grade == 0) { streak = 0; miscon = false; due = day.AddDays(1); }
            else if (e.Grade == 2)
            {
                // ✓✓ Know it well: a much longer gap (two weeks the first time, then months).
                streak = Math.Max(streak + 2, 3); miscon = false;
                due = day.AddDays(gaps[Math.Min(streak - 1, gaps.Length - 1)] * 2);
            }
            else if (e.Grade == 1 || (e.Correct && e.Sure)) { streak++; miscon = false; due = day.AddDays(gaps[Math.Min(streak - 1, gaps.Length - 1)]); }
            else { streak = 0; miscon = !e.Correct && e.Sure; due = day.AddDays(1); }
        }
        return new QState(due, streak, miscon, seen);
    }

    /// <summary>A concept's stage: Learned once answered right (and sure) on three separate days; Retired after a long run.</summary>
    public static ConceptStage StageOf(string concept)
    {
        var right = CE.Where(e => e.Kind == "answer" && e.Concept == concept);
        if (!right.Any()) return ConceptStage.New;
        int days = right.Where(e => e.Correct && e.Sure).Select(e => e.Day).Distinct().Count();
        return days >= 6 ? ConceptStage.Retired : days >= 3 ? ConceptStage.Learned : ConceptStage.Learning;
    }

    /// <summary>Review for today: due questions from lessons already done (this course only), misconceptions first.</summary>
    public static List<(LessonRef Lesson, PackQuestion Q)> DueReview(int cap)
    {
        var hidden = Hidden();
        var done = DoneLessons();
        var today = DateTime.Today;
        var due = new List<(LessonRef, PackQuestion, QState)>();
        foreach (var l in Lessons().Where(l => done.Contains(l.Index)))
            foreach (var q in l.Lesson.Practice.Concat(l.Lesson.Cards).Concat(l.Lesson.Quiz.Where(x => x.Kind == "mc")))
            {
                if (hidden.Contains(q.Id)) continue;
                var s = StateOf(q.Id);
                if (s.Seen > 0 && s.Due <= today) due.Add((l, q, s));
            }
        return due.OrderByDescending(x => x.Item3.Misconception).ThenBy(x => x.Item3.Due).ThenBy(x => x.Item3.Streak)
                  .Take(Math.Max(0, cap)).Select(x => (x.Item1, x.Item2)).ToList();
    }

    /// <summary>Days in a row with a finished lesson, ending today or yesterday.</summary>
    public static int Streak()
    {
        var days = CE.Where(e => e.Kind == "lessonDone").Select(e => e.Day).ToHashSet();
        var d = DateTime.Today;
        if (!days.Contains(Fmt(d))) d = d.AddDays(-1);
        int n = 0;
        while (days.Contains(Fmt(d))) { n++; d = d.AddDays(-1); }
        return n;
    }

    private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Concepts missed most, for the course page.</summary>
    public static List<(string Concept, int Missed)> WeakSpots(int count) =>
        CE.Where(e => e.Kind == "answer" && !e.Correct && e.Concept.Length > 0)
              .GroupBy(e => e.Concept).Select(g => (g.Key, g.Count()))
              .Where(x => StageOf(x.Key) < ConceptStage.Learned)
              .OrderByDescending(x => x.Item2).Take(count).ToList();

    // ---------- Highlights and notes ----------

    public record Highlight(string Id, int Chapter, int Para, string Quote, string Color, string Note, DateTime At);

    public static List<Highlight> Highlights()
    {
        var map = new Dictionary<string, Highlight>();
        foreach (var e in CE)
        {
            if (e.Kind == "highlight") map[e.Question] = new Highlight(e.Question, e.Chapter, e.Para, e.Quote, e.Color, e.Note, e.At);
            else if (e.Kind == "highlightNote" && map.TryGetValue(e.Question, out var h)) map[e.Question] = h with { Note = e.Note };
            else if (e.Kind == "unhighlight") map.Remove(e.Question);
        }
        return map.Values.OrderBy(h => h.Para).ToList();
    }

    public static string AddHighlight(int chapter, int para, string quote, string color, string note)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        Add(new TrainingEvent { Kind = "highlight", Question = id, Chapter = chapter, Para = para, Quote = quote, Color = color, Note = note });
        return id;
    }

    /// <summary>Every highlight and note as Markdown: chapter, section heading, the quote, then your note.</summary>
    public static string ExportMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + Current.Title).AppendLine();
        sb.AppendLine($"Highlights and notes, exported {DateTime.Now:d MMMM yyyy}.").AppendLine();
        foreach (var chapter in Highlights().GroupBy(h => h.Chapter).OrderBy(g => g.Key))
        {
            string title = Packs.TryGetValue(chapter.Key, out var pk) ? pk.Title : "";
            sb.AppendLine($"## Chapter {chapter.Key}{(title.Length > 0 ? ": " + title : "")}").AppendLine();
            string lastHeading = "";
            foreach (var h in chapter.OrderBy(h => h.Para))
            {
                string heading = "";
                for (int i = Math.Min(h.Para, Book.Count - 1); i >= 0; i--) if (Book[i].Heading >= 2) { heading = Book[i].Text.Trim(); break; }
                if (heading.Length > 0 && heading != lastHeading) { sb.AppendLine("### " + heading).AppendLine(); lastHeading = heading; }
                sb.AppendLine("> " + h.Quote.Replace("\n", " ").Trim()).AppendLine();
                if (h.Note.Trim().Length > 0) sb.AppendLine(h.Note.Trim()).AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string ExportPath => Path.Combine(Dir, $"{Safe(Current.Id)}-notes.md");

    // ---------- Unit exams and the final exam ----------

    public record Exam(string Key, string Title, List<(LessonRef Lesson, PackQuestion Q)> Questions);

    public static HashSet<string> ExamsPassed() => CE.Where(e => e.Kind == "examDone" && e.Correct).Select(e => e.Note).ToHashSet();
    public static HashSet<string> ExamsTaken() => CE.Where(e => e.Kind == "examDone").Select(e => e.Note).ToHashSet();

    /// <summary>The book is finished once its final exam is passed; its questions then join All-books practice.</summary>
    public static bool Finished => ExamsPassed().Contains("final");

    /// <summary>An exam that's due: a unit exam once every lesson of that part is done, or the final once the whole book is.</summary>
    public static Exam? ExamDue()
    {
        var lessons = Lessons();
        var done = DoneLessons();
        int prev = 0;
        for (int u = 0; u < Current.UnitEnds.Count; u++)
        {
            int last = Current.UnitEnds[u];
            var unit = lessons.Where(l => l.Pack.Chapter > prev && l.Pack.Chapter <= last).ToList();
            bool complete = Enumerable.Range(prev + 1, Math.Max(0, last - prev)).All(c => Packs.ContainsKey(c)) && unit.Count > 0 && unit.All(l => done.Contains(l.Index));
            string key = $"unit{u + 1}";
            if (complete && !ExamsPassed().Contains(key)) return new Exam(key, $"Unit exam {u + 1}: chapters {prev + 1}–{last}", ExamQuestions(unit, 20));
            prev = last;
        }
        bool all = Enumerable.Range(1, Current.Chapters).All(c => Packs.ContainsKey(c)) && lessons.Count > 0 && lessons.All(l => done.Contains(l.Index));
        if (all && !ExamsPassed().Contains("final")) return new Exam("final", "Final exam: the whole book", ExamQuestions(lessons, 40));
        return null;
    }

    /// <summary>Questions you haven't answered yet first, then the ones seen longest ago; multiple choice only.</summary>
    private static List<(LessonRef, PackQuestion)> ExamQuestions(List<LessonRef> lessons, int count)
    {
        var hidden = Hidden();
        var rnd = new Random();
        return lessons.SelectMany(l => l.Lesson.Practice.Concat(l.Lesson.Quiz).Where(q => q.Kind == "mc" && !hidden.Contains(q.Id)).Select(q => (l, q)))
            .Select(x => (x.l, x.q, s: StateOf(x.q.Id)))
            .OrderBy(x => x.s.Seen > 0).ThenBy(x => x.s.Due).ThenBy(_ => rnd.Next())
            .Take(count).Select(x => (x.l, x.q)).ToList();
    }

    // ---------- All-books practice ----------

    /// <summary>A short round from finished books: missed questions and ones not seen for a while come up more often.</summary>
    public static List<(LessonRef Lesson, PackQuestion Q)> AllBooksRound(int count)
    {
        if (!Finished) return new();
        var hidden = Hidden();
        var rnd = new Random();
        return Lessons().SelectMany(l => l.Lesson.AllQuestions.Where(q => q.Kind == "mc" && !hidden.Contains(q.Id)).Select(q => (l, q)))
            .Select(x =>
            {
                var st = StateOf(x.q.Id);
                double weight = 1 + (st.Misconception ? 3 : 0) + (st.Streak == 0 ? 1.5 : 0) + Math.Min(3, (DateTime.Today - st.Due).TotalDays / 30.0);
                return (x.l, x.q, key: -Math.Log(Math.Max(1e-9, rnd.NextDouble())) / weight);
            })
            .OrderBy(x => x.key).Take(count).Select(x => (x.l, x.q)).ToList();
    }
}
