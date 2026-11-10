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

/// <summary>A dated event for the timeline review. Year: negative for BCE (−167 = 167 BCE). Within: years either way that still count as right.</summary>
public class PackEvent
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("year")] public int Year { get; set; }
    [JsonPropertyName("within")] public int Within { get; set; } = 10;
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("find")] public string Find { get; set; } = "";

    public static string Label(int year) => year < 0 ? $"{-year} BCE" : $"{Math.Max(1, year)} CE";
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
    [JsonPropertyName("timeline")] public List<PackEvent> Timeline { get; set; } = new();

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
    /// <summary>A text box's own fill color (#RRGGBB), when the book gave it one.</summary>
    public string? Fill { get; set; }
    /// <summary>A picture's caption (small and muted under it).</summary>
    public bool Caption { get; set; }
    /// <summary>A list item's bullet or number ("•", "3.", "b)"), and how deep it is (0 = first level).</summary>
    public string? ListMark { get; set; }
    public int ListLevel { get; set; }
    /// <summary>Tables: which table (paragraphs of one table share it), and the cell's row and column.</summary>
    public int TableId { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    /// <summary>A chart from the book, drawn from the numbers Word stored with it.</summary>
    public BookChart? Chart { get; set; }
    public List<BookRun> Runs { get; set; } = new();
    [JsonIgnore] public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>A Word chart's numbers: its kind (bar, column, line or pie), title, categories and series.</summary>
public class BookChart
{
    public string Kind { get; set; } = "column";
    public string Title { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public List<string> SeriesNames { get; set; } = new();
    public List<List<double>> SeriesValues { get; set; } = new();
}

public static class BookReader
{
    private static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
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

        // Styles: which style ids are headings, which carry shading, and which are captions.
        var headingOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shadedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var captionStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Caption" };
        var styleSize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int normalSize = 22;
        if (zip.GetEntry("word/styles.xml") is ZipArchiveEntry stylesEntry)
        {
            using var ss = stylesEntry.Open();
            var sdoc = XDocument.Load(ss);
            if (int.TryParse((string?)sdoc.Root?.Element(W + "docDefaults")?.Descendants(W + "sz").FirstOrDefault()?.Attribute(W + "val"), out int dsz)) normalSize = dsz;
            foreach (var st in sdoc.Descendants(W + "style"))
            {
                string id = (string?)st.Attribute(W + "styleId") ?? "";
                string name = (string?)st.Element(W + "name")?.Attribute(W + "val") ?? id;
                int level = HeadingLevel(name);
                var outline = st.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
                if (level == 0 && outline != null && int.TryParse(outline.Value, out int ol) && ol < 3) level = ol + 1;
                if (level > 0) headingOf[id] = level;
                if (IsShaded(st.Element(W + "pPr")?.Element(W + "shd"))) shadedStyles.Add(id);
                if (name.Equals("caption", StringComparison.OrdinalIgnoreCase)) captionStyles.Add(id);
                if (int.TryParse((string?)st.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val"), out int ssz)) styleSize[id] = ssz;
                if (id.Equals("Normal", StringComparison.OrdinalIgnoreCase) && styleSize.TryGetValue(id, out int nsz)) normalSize = nsz;
            }
        }

        // Numbering: each list's bullet or number format, by list and level.
        var numFormat = new Dictionary<(string Num, int Level), (string Fmt, string Text)>();
        if (zip.GetEntry("word/numbering.xml") is ZipArchiveEntry numEntry)
        {
            using var ns = numEntry.Open();
            var ndoc = XDocument.Load(ns);
            var abstracts = ndoc.Descendants(W + "abstractNum").ToDictionary(x => (string?)x.Attribute(W + "abstractNumId") ?? "", x => x);
            foreach (var num in ndoc.Descendants(W + "num"))
            {
                string numId = (string?)num.Attribute(W + "numId") ?? "";
                string absId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val") ?? "";
                if (!abstracts.TryGetValue(absId, out var abs)) continue;
                foreach (var lvl in abs.Elements(W + "lvl"))
                    if (int.TryParse((string?)lvl.Attribute(W + "ilvl"), out int il))
                        numFormat[(numId, il)] = ((string?)lvl.Element(W + "numFmt")?.Attribute(W + "val") ?? "bullet", (string?)lvl.Element(W + "lvlText")?.Attribute(W + "val") ?? "");
            }
        }
        var counters = new Dictionary<(string, int), int>();

        var paras = new List<BookPara>();
        var body = doc.Root?.Element(W + "body");
        if (body == null) return paras;
        var tableIds = new Dictionary<XElement, int>();
        // Word keeps a second copy of every text box (for older versions) under mc:Fallback: read only the first.
        foreach (var p in body.Descendants(W + "p").Where(p => !p.Ancestors(MC + "Fallback").Any()))
        {
            var pPr = p.Element(W + "pPr");
            string style = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
            var box = p.Ancestors(W + "txbxContent").FirstOrDefault();
            var tbl = p.Ancestors(W + "tbl").FirstOrDefault();
            var para = new BookPara
            {
                Heading = headingOf.TryGetValue(style, out int h) ? h : HeadingLevel(style),
                Sidebar = box != null || IsShaded(pPr?.Element(W + "shd")) || shadedStyles.Contains(style),
                InTable = tbl != null,
                Caption = captionStyles.Contains(style)
            };
            if (box != null)
            {
                // The text box's own fill color, if it has one.
                var fill = box.Ancestors().FirstOrDefault(x => x.Name.LocalName is "wsp" or "shape")?.Descendants(A + "solidFill").FirstOrDefault()?.Element(A + "srgbClr")?.Attribute("val");
                if (fill != null && fill.Value.Length == 6 && fill.Value.ToUpperInvariant() != "FFFFFF") para.Fill = "#" + fill.Value;
            }
            if (tbl != null)
            {
                if (!tableIds.TryGetValue(tbl, out int tid)) tableIds[tbl] = tid = tableIds.Count + 1;
                para.TableId = tid;
                var tr = p.Ancestors(W + "tr").First();
                var tc = p.Ancestors(W + "tc").First();
                para.Row = tbl.Elements(W + "tr").ToList().IndexOf(tr);
                para.Col = tr.Elements(W + "tc").ToList().IndexOf(tc);
                if (IsShaded(tc.Element(W + "tcPr")?.Element(W + "shd"))) para.Fill ??= "shade";
            }
            var numPr = pPr?.Element(W + "numPr");
            string? numId = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val");
            if (numId != null && numId != "0" && para.Heading == 0)
            {
                int il = int.TryParse((string?)numPr!.Element(W + "ilvl")?.Attribute(W + "val"), out int lv) ? lv : 0;
                var (fmt, lvlText) = numFormat.TryGetValue((numId, il), out var f) ? f : ("bullet", "");
                foreach (var k in counters.Keys.Where(k => k.Item1 == numId && k.Item2 > il).ToList()) counters.Remove(k);
                int n = counters[(numId, il)] = (counters.TryGetValue((numId, il), out int c0) ? c0 : 0) + 1;
                string mark = fmt switch
                {
                    "bullet" or "none" => il % 2 == 0 ? "•" : "◦",
                    "lowerLetter" => ((char)('a' + (n - 1) % 26)).ToString(),
                    "upperLetter" => ((char)('A' + (n - 1) % 26)).ToString(),
                    "lowerRoman" => Roman(n).ToLowerInvariant(),
                    "upperRoman" => Roman(n),
                    _ => n.ToString(CultureInfo.InvariantCulture)
                };
                if (fmt is not ("bullet" or "none")) mark += lvlText.EndsWith(")") ? ")" : ".";
                para.ListMark = mark; para.ListLevel = il;
            }
            int sizeSum = 0, sizeCount = 0;
            // Only this paragraph's own runs: a text box's runs belong to the text box's own paragraphs.
            foreach (var r in p.Descendants(W + "r").Where(r => r.Ancestors(W + "p").First() == p && !r.Ancestors(MC + "Fallback").Any()))
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
                int sz = int.TryParse((string?)rPr?.Element(W + "sz")?.Attribute(W + "val"), out int rs) ? rs : styleSize.TryGetValue(style, out int st2) ? st2 : normalSize;
                sizeSum += sz * text.Length; sizeCount += text.Length;
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
            // Small text right under a picture or chart reads as its caption.
            if (!para.Caption && para.Heading == 0 && sizeCount > 0 && sizeSum / sizeCount <= normalSize - 3 && sizeCount < 400 &&
                paras.Count > 0 && (paras[^1].Image != null || paras[^1].Chart != null)) para.Caption = true;
            if (mediaDir != null)
                foreach (var blip in p.Descendants().Where(x => (x.Name == A + "blip" || x.Name.LocalName == "imagedata") && x.Ancestors(W + "p").First() == p && !x.Ancestors(MC + "Fallback").Any()))
                {
                    string rid = (string?)blip.Attribute(R + "embed") ?? (string?)blip.Attribute(R + "id") ?? "";
                    if (!rels.TryGetValue(rid, out var target)) continue;
                    if (SaveImage(zip, target, mediaDir) is string name) paras.Add(new BookPara { Image = name, Sidebar = para.Sidebar, Fill = para.Fill });
                }
            foreach (var chartRef in p.Descendants(C + "chart").Where(x => x.Ancestors(W + "p").First() == p && !x.Ancestors(MC + "Fallback").Any()))
            {
                string rid = (string?)chartRef.Attribute(R + "id") ?? "";
                if (rels.TryGetValue(rid, out var target) && ReadChart(zip, target) is BookChart chart) paras.Add(new BookPara { Chart = chart, Sidebar = para.Sidebar });
            }
            if (para.Text.Trim().Length > 0) paras.Add(para);
        }
        return paras;
    }

    private static string Roman(int n)
    {
        var sb = new StringBuilder();
        foreach (var (v, r) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            while (n >= v) { sb.Append(r); n -= v; }
        return sb.ToString();
    }

    private static ZipArchiveEntry? Part(ZipArchive zip, string target) =>
        zip.GetEntry("word/" + target.TrimStart('/').Replace("word/", "")) ?? zip.GetEntry(target.TrimStart('/'));

    /// <summary>Saves a picture into the media folder; Windows drawings (EMF, WMF) become PNG pictures.</summary>
    private static string? SaveImage(ZipArchive zip, string target, string mediaDir)
    {
        string ext = Path.GetExtension(target).ToLowerInvariant();
        var img = Part(zip, target);
        if (img == null) return null;
        Directory.CreateDirectory(mediaDir);
        string name = Path.GetFileName(target);
        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
        {
            img.ExtractToFile(Path.Combine(mediaDir, name), overwrite: true);
            return name;
        }
        if (ext is not (".emf" or ".wmf")) return null;
        try
        {
            using var ms = new MemoryStream();
            using (var src = img.Open()) src.CopyTo(ms);
            ms.Position = 0;
            using var meta = new System.Drawing.Imaging.Metafile(ms);
            // Twice its size, so it stays sharp.
            int w = Math.Clamp(meta.Width * 2, 50, 2400), hgt = Math.Clamp(meta.Height * 2, 50, 2400);
            using var bmp = new System.Drawing.Bitmap(w, hgt);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawImage(meta, 0, 0, w, hgt);
            }
            name = Path.GetFileNameWithoutExtension(target) + ".png";
            bmp.Save(Path.Combine(mediaDir, name), System.Drawing.Imaging.ImageFormat.Png);
            return name;
        }
        catch (Exception ex) { Log.Error("Book picture " + target, ex); return null; }
    }

    /// <summary>A chart's numbers from its part in the Word file (bar, column, line or pie).</summary>
    private static BookChart? ReadChart(ZipArchive zip, string target)
    {
        try
        {
            var part = Part(zip, target);
            if (part == null) return null;
            XDocument cd;
            using (var cs = part.Open()) cd = XDocument.Load(cs);
            var plot = cd.Descendants(C + "plotArea").FirstOrDefault();
            if (plot == null) return null;
            var kindEl = plot.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Chart"));
            if (kindEl == null) return null;
            string local = kindEl.Name.LocalName;
            var chart = new BookChart
            {
                Kind = local.StartsWith("pie") || local.StartsWith("doughnut") ? "pie" : local.StartsWith("line") || local.StartsWith("area") ? "line"
                     : (string?)kindEl.Element(C + "barDir")?.Attribute("val") == "bar" ? "bar" : "column",
                Title = string.Concat(cd.Root?.Element(C + "chart")?.Element(C + "title")?.Descendants(A + "t").Select(t => t.Value) ?? Array.Empty<string>())
            };
            static List<string> Points(XElement? e) => e?.Descendants(C + "pt").OrderBy(pt => (int?)pt.Attribute("idx") ?? 0).Select(pt => (string?)pt.Element(C + "v") ?? "").ToList() ?? new();
            foreach (var ser in kindEl.Elements(C + "ser"))
            {
                if (chart.Categories.Count == 0) chart.Categories = Points(ser.Element(C + "cat"));
                chart.SeriesNames.Add(Points(ser.Element(C + "tx")).FirstOrDefault() ?? "");
                chart.SeriesValues.Add(Points(ser.Element(C + "val")).Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0).ToList());
            }
            return chart.SeriesValues.Count == 0 ? null : chart;
        }
        catch (Exception ex) { Log.Error("Book chart " + target, ex); return null; }
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
    /// <summary>The weekly plan: "Track A", "Track B" or "Elective", and its days (1 = Monday ... 5 = Friday).</summary>
    public string Role { get; set; } = "";
    public List<int> Days { get; set; } = new();
    public int Chapters { get; set; } = 24;
    /// <summary>The last chapter of each part of the book; a unit exam follows each one.</summary>
    public List<int> UnitEnds { get; set; } = new();
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The cover photo's file name in training\covers (synced with your other computers), or empty.</summary>
    public string Cover { get; set; } = "";
}

/// <summary>Training settings (for every book) and your library, shared between your devices.</summary>
public class CourseConfig
{
    public List<CourseInfo> Courses { get; set; } = new();
    /// <summary>The book whose lessons come up each day.</summary>
    public string ActiveId { get; set; } = "";
    /// <summary>A missed lesson: "offer" it as an optional catch-up the next weekday, or "carry" on without one.</summary>
    public string MissedPolicy { get; set; } = "offer";
    /// <summary>Catch-ups you let go ("courseId|yyyy-MM-dd").</summary>
    public List<string> SkippedCatchUps { get; set; } = new();
    // From before the library (one book only); moved into Courses when first loaded.
    public string Id { get; set; } = "desilva-nt";
    public string Title { get; set; } = "deSilva, An Introduction to the New Testament";
    public string BookFileName { get; set; } = "";
    public int Chapters { get; set; } = 24;
    /// <summary>The three daily times; the last one must be finished.</summary>
    public List<string> Times { get; set; } = new() { "07:00", "13:00", "19:30" };
    public List<string> BlockListIds { get; set; } = new();
    public int ReviewCap { get; set; } = 8;
    /// <summary>About how long a day's review should take (minutes). New lessons' memory cards join the review only while
    /// it fits; the rest wait their turn (see Course.ReleaseCards).</summary>
    public int ReviewMinutes { get; set; } = 10;
    /// <summary>When the waiting room started: memory cards first answered before this are already in the review.</summary>
    public DateTime? IntakeSinceUtc { get; set; }
    public bool On { get; set; } = true;
    /// <summary>Read in pages (like a book) instead of scrolling.</summary>
    public bool PageView { get; set; }
    public List<int> UnitEnds { get; set; } = new();
    /// <summary>All-books practice: its times, and whether it pops up by itself.</summary>
    public List<string> AllBooksTimes { get; set; } = new() { "16:00" };
    public bool AllBooksAuto { get; set; } = true;
    /// <summary>Show the day's practice rounds as small markers in the Day plan and the Dashboard's day view.</summary>
    public bool ShowPracticeInDay { get; set; }
    /// <summary>List today's practice rounds, with their times, on the Training page.</summary>
    public bool ShowPracticeOnTraining { get; set; } = true;

    // ----- How the reading looks (Training settings, or Aa while reading) -----
    /// <summary>The reading's font (any font installed on the PC).</summary>
    public string ReadingFont { get; set; } = "Georgia";
    public double ReadingSize { get; set; } = 17;
    /// <summary>The reading's background: theme (follows Focus), paper, sepia, white, sage, gray, slate or night.</summary>
    public string ReadingBg { get; set; } = "theme";
    /// <summary>scroll, page (one column of pages) or two (two columns, like an open book).</summary>
    public string ReadingLayout { get; set; } = "";
    public double ReadingSpacing { get; set; } = 1.65;
    /// <summary>The text column's width in pixels (Narrow 620, Normal 760, Wide 940).</summary>
    public int ReadingWidth { get; set; } = 760;
    public bool ReadingJustify { get; set; }
    /// <summary>Whether the reading settings panel is open beside the text.</summary>
    public bool ReadingPanelOpen { get; set; } = true;

    // ----- Evening card review: a short round of the day's hardest memory cards -----
    public bool CardReviewOn { get; set; } = true;
    /// <summary>When it pops up (set on the practice timelines).</summary>
    public List<string> CardReviewTimes { get; set; } = new() { "20:30" };
    public int CardReviewMinutes { get; set; } = 5;

    /// <summary>Weeks you changed on the Training page: a day's lessons (book ids, in order), by date (yyyy-MM-dd).
    /// Days not listed follow the weekly plan.</summary>
    public Dictionary<string, DayPlan> WeekPlan { get; set; } = new();
    /// <summary>Timeline practice: rounds a day on the practice timeline, and how many events a round.</summary>
    public List<string> TimelineTimes { get; set; } = new() { "16:30" };
    public bool TimelineOn { get; set; } = true;
    public int TimelineCount { get; set; } = 8;
    /// <summary>Round sizes (Training settings, Practice): questions in a Books practice round, cards in a Book flashcards round.</summary>
    public int BooksPracticeCount { get; set; } = 12;
    public int FlashcardsCount { get; set; } = 20;
}

/// <summary>One changed day: its lessons, or back to the weekly plan; the newest change wins between computers.</summary>
public class DayPlan
{
    public List<string> Books { get; set; } = new();
    public bool Default { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
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
    // ---------- The weekly plan: which book each day ----------

    public static readonly string[] Roles = { "Track A", "Track B", "Elective" };
    public static string RoleOf(CourseInfo c) => Roles.Contains(c.Role) ? c.Role : "Track A";
    private static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The book planned for a day: none at weekends; with no days planned yet, your active book takes every weekday.</summary>
    public static CourseInfo? BookFor(DateTime day) => BooksFor(day).FirstOrDefault();

    /// <summary>A day's lessons in order (a book twice means two of its lessons), from the week you changed or the weekly plan.</summary>
    public static List<CourseInfo> BooksFor(DateTime day)
    {
        if (Config.WeekPlan.TryGetValue(Ymd(day), out var plan) && !plan.Default)
            return plan.Books.Select(id => Config.Courses.FirstOrDefault(c => c.Id == id)).Where(c => c != null).Select(c => c!).ToList();
        return PlanBook(day) is CourseInfo c1 ? new List<CourseInfo> { c1 } : new List<CourseInfo>();
    }

    /// <summary>True when that day's week was changed (it differs from the weekly plan).</summary>
    public static bool WeekChanged(DateTime monday) =>
        Enumerable.Range(0, 7).Any(i => Config.WeekPlan.TryGetValue(Ymd(monday.AddDays(i)), out var p) && !p.Default);

    /// <summary>Changes one day's lessons (this week or ahead only).</summary>
    public static void SetDay(DateTime day, List<string> books, bool save = true)
    {
        if (day.Date < DateTime.Today) return;
        Config.WeekPlan[Ymd(day)] = new DayPlan { Books = books, At = DateTime.UtcNow };
        // Forget changed days more than two weeks past.
        foreach (var k in Config.WeekPlan.Keys.Where(k => string.CompareOrdinal(k, Ymd(DateTime.Today.AddDays(-14))) < 0).ToList()) Config.WeekPlan.Remove(k);
        if (save) SaveConfig();
    }

    /// <summary>Back to the weekly plan for a week (today and the days after it).</summary>
    public static void ResetWeek(DateTime monday)
    {
        for (int i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            if (d >= DateTime.Today && Config.WeekPlan.ContainsKey(Ymd(d))) Config.WeekPlan[Ymd(d)] = new DayPlan { Default = true, At = DateTime.UtcNow };
        }
        SaveConfig();
    }

    /// <summary>Lessons done in a book on a day (on any device).</summary>
    public static int DoneCountOn(string courseId, DateTime day) =>
        Events.Count(e => CourseOf(e) == courseId && e.Kind == "lessonDone" && e.Day == Ymd(day));

    /// <summary>A day's planned lessons, each marked done once that many of its book's lessons were finished that day.</summary>
    public static List<(CourseInfo Book, bool Done)> DayLessons(DateTime day)
    {
        var seen = new Dictionary<string, int>();
        var list = new List<(CourseInfo, bool)>();
        foreach (var b in BooksFor(day))
        {
            int n = seen[b.Id] = (seen.TryGetValue(b.Id, out int k) ? k : 0) + 1;
            list.Add((b, DoneCountOn(b.Id, day) >= n));
        }
        return list;
    }

    /// <summary>The book of today's next lesson not yet done, or null when today's lessons are all done (or none are planned).</summary>
    public static CourseInfo? NextDueToday() => DayLessons(DateTime.Today).Where(x => !x.Done).Select(x => x.Book).FirstOrDefault();

    /// <summary>How many of today's planned lessons are done.</summary>
    public static int DoneTodayCount() => DayLessons(DateTime.Today).Count(x => x.Done);

    public record Outline(List<LessonRef> Lessons, HashSet<int> Done, int NextChapter, int? InProgress);
    private static readonly Dictionary<string, (DateTime At, int Events, Outline O)> _outlines = new();

    /// <summary>A book's lessons, which are done and the chapter to import next (each book opened quietly; kept a minute).</summary>
    public static Outline OutlineOf(string id)
    {
        if (_outlines.TryGetValue(id, out var c) && DateTime.Now - c.At < TimeSpan.FromMinutes(1) && c.Events == Events.Count) return c.O;
        var was = Current;
        Outline o = new(new(), new(), 1, null);
        try
        {
            if (Config.Courses.FirstOrDefault(x => x.Id == id) is CourseInfo book)
            {
                if (book.Id != Current.Id) { Current = book; LoadCurrent(raise: false); }
                var next = NextLesson();
                o = new(Lessons(), DoneLessons(), Packs.Count == 0 ? 1 : Packs.Keys.Max() + 1, InProgress(next) ? (int?)next!.Index : null);
            }
        }
        catch (Exception ex) { Log.Error("Outline " + id, ex); }
        finally { if (Current.Id != was.Id) { Current = was; LoadCurrent(raise: false); } }
        _outlines[id] = (DateTime.Now, Events.Count, o);
        return o;
    }

    private static (DateTime At, int Events, int Pages)? _pages;

    /// <summary>About how many book pages this week's finished lessons covered (about 2,400 letters a page).</summary>
    public static int PagesReadThisWeek()
    {
        if (_pages is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(1) && c.Events == Events.Count) return c.Pages;
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        string from = Ymd(monday);
        long letters = 0;
        var was = Current;
        try
        {
            foreach (var g in Events.Where(e => e.Kind == "lessonDone" && e.Lesson >= 0 && string.CompareOrdinal(e.Day, from) >= 0).GroupBy(CourseOf))
            {
                if (Config.Courses.FirstOrDefault(x => x.Id == g.Key) is not CourseInfo book) continue;
                if (book.Id != Current.Id) { Current = book; LoadCurrent(raise: false); }
                var lessons = Lessons();
                foreach (int idx in g.Select(e => e.Lesson).Distinct())
                {
                    if (idx >= lessons.Count) continue;
                    var (st, en) = LessonRange(lessons[idx]);
                    for (int i = Math.Max(0, st); i < Math.Min(Book.Count, en); i++) letters += Book[i].Text.Length;
                }
            }
        }
        catch (Exception ex) { Log.Error("Pages read", ex); }
        finally { if (Current.Id != was.Id) { Current = was; LoadCurrent(raise: false); } }
        int pages = (int)Math.Round(letters / 2400.0);
        _pages = (DateTime.Now, Events.Count, pages);
        return pages;
    }

    /// <summary>The weekly plan's book for a day: none at weekends; with no days planned yet, your active book takes every weekday.</summary>
    private static CourseInfo? PlanBook(DateTime day)
    {
        int dow = (int)day.DayOfWeek;
        if (dow == 0 || dow == 6) return null;
        if (Config.Courses.All(c => c.Days.Count == 0)) return Config.Courses.FirstOrDefault(c => c.Id == Current.Id) ?? Config.Courses.FirstOrDefault();
        return Config.Courses.Where(c => c.Days.Contains(dow)).OrderBy(c => Array.IndexOf(Roles, RoleOf(c))).FirstOrDefault();
    }

    public static string DaysText(CourseInfo c)
    {
        var days = Config.Courses.All(x => x.Days.Count == 0) && c.Id == Current.Id ? new List<int> { 1, 2, 3, 4, 5 } : c.Days.OrderBy(d => d).ToList();
        return days.Count == 0 ? "No days yet" : string.Join(" · ", days.Where(d => d >= 1 && d <= 5).Select(d => new[] { "", "Mon", "Tue", "Wed", "Thu", "Fri" }.ElementAt(d)));
    }

    public static bool DoneOn(string courseId, DateTime day) =>
        Events.Any(e => CourseOf(e) == courseId && e.Kind == "lessonDone" && e.Day == Ymd(day));

    public static int LessonsDone(string courseId) =>
        Events.Where(e => CourseOf(e) == courseId && e.Kind == "lessonDone" && e.Lesson >= 0).Select(e => e.Lesson).Distinct().Count();

    /// <summary>The last weekday's planned lesson, if it wasn't done and its book hasn't had a lesson since (optional catch-up).</summary>
    public static (CourseInfo Book, DateTime Day)? MissedLesson()
    {
        if (Config.MissedPolicy != "offer") return null;
        // The last day before today that had lessons planned (up to three days back); one catch-up at a time.
        for (var day = DateTime.Today.AddDays(-1); (DateTime.Today - day).TotalDays <= 3; day = day.AddDays(-1))
        {
            var lessons = DayLessons(day);
            if (lessons.Count == 0) continue;
            var book = lessons.FirstOrDefault(x => !x.Done).Book;
            if (book == null) return null;
            if (Config.SkippedCatchUps.Contains(book.Id + "|" + Ymd(day))) return null;
            // Already caught up: a lesson of that book on a day since, beyond what that day planned.
            for (var d = day.AddDays(1); d <= DateTime.Today; d = d.AddDays(1))
                if (DoneCountOn(book.Id, d) > BooksFor(d).Count(c => c.Id == book.Id)) return null;
            return (book, day);
        }
        return null;
    }

    public static void SkipCatchUp(CourseInfo book, DateTime day)
    {
        Config.SkippedCatchUps.Add(book.Id + "|" + Ymd(day));
        if (Config.SkippedCatchUps.Count > 60) Config.SkippedCatchUps.RemoveAt(0);
        SaveConfig();
    }

    /// <summary>Makes today's planned book the open one (lessons, prompts and "done today" follow it).</summary>
    /// <summary>The book of the lesson or practice window that's open: the open book doesn't change under it, and its
    /// answers always count toward it.</summary>
    public static string? Pinned { get; set; }

    public static void UseToday()
    {
        if (Pinned != null) { UseBookQuietly(Pinned); return; }
        // Switches without saving the book's settings file (saving it here let an older computer overwrite newer settings).
        EnsureWeeklyPlan();
        if ((NextDueToday() ?? BookFor(DateTime.Today)) is CourseInfo c && c.Id != Current.Id) { Current = c; Config.ActiveId = c.Id; LoadCurrent(raise: false); }
    }

    /// <summary>
    /// With two or more books and no days planned yet, the book you've done most in keeps every weekday (Track A) and the
    /// others wait for days (Track B), so a newly added book never takes over the one you're studying.
    /// </summary>
    public static void EnsureWeeklyPlan()
    {
        if (Config.Courses.Count < 2 || Config.Courses.Any(c => c.Days.Count > 0)) return;
        var main = Config.Courses.OrderByDescending(c => LessonsDone(c.Id)).ThenBy(c => Config.Courses.IndexOf(c)).First();
        main.Days = new List<int> { 1, 2, 3, 4, 5 };
        if (main.Role.Length == 0) main.Role = "Track A";
        foreach (var c in Config.Courses.Where(c => c != main && c.Role.Length == 0)) c.Role = "Track B";
        SaveConfig();
    }

    /// <summary>Switches the open book without redrawing anything (for practice in another book).</summary>
    public static void UseBookQuietly(string id)
    {
        if (id == Current.Id || Config.Courses.FirstOrDefault(c => c.Id == id) is not CourseInfo c) return;
        Current = c; Config.ActiveId = c.Id; LoadCurrent(raise: false);
    }

    private static (DateTime At, List<(CourseInfo Book, int Due)> Counts)? _dueCache;

    /// <summary>Review due in each book (each opened briefly to count, then back to the one you had).</summary>
    public static List<(CourseInfo Book, int Due)> DueByBook()
    {
        if (_dueCache is { } cache && DateTime.Now - cache.At < TimeSpan.FromMinutes(1) && cache.Counts.Count == Config.Courses.Count) return cache.Counts;
        var result = new List<(CourseInfo, int)>();
        var was = Current;
        foreach (var c in Config.Courses.ToList())
        {
            try
            {
                if (c.Id != Current.Id) { Current = c; LoadCurrent(raise: false); }
                result.Add((c, DueReview(999).Count));
            }
            catch (Exception ex) { Log.Error("DueByBook " + c.Id, ex); }
        }
        if (Current.Id != was.Id) { Current = was; LoadCurrent(raise: false); }
        _dueCache = (DateTime.Now, result);
        return result;
    }

    public static void Open(string id)
    {
        if (Pinned != null && Pinned != id) return; // a lesson is open: its book stays open
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

    /// <summary>Renames a book (its lessons and progress stay with it).</summary>
    public static void RenameCourse(string id, string title)
    {
        if (title.Trim().Length == 0 || Config.Courses.FirstOrDefault(c => c.Id == id) is not CourseInfo c) return;
        c.Title = title.Trim();
        if (Current.Id == id) Current.Title = c.Title;
        SaveConfig();
    }

    /// <summary>The cover photo's full path, or null if the book has none (or the file hasn't synced yet).</summary>
    public static string? CoverPath(CourseInfo c)
    {
        if (c.Cover.Length == 0) return null;
        string path = Path.Combine(Dir, "covers", c.Cover);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Sets a book's cover photo (copied into the training folder), or removes it when <paramref name="source"/> is null.</summary>
    public static void SetCover(string id, string? source)
    {
        if (Config.Courses.FirstOrDefault(c => c.Id == id) is not CourseInfo c) return;
        string dir = Path.Combine(Dir, "covers");
        string old = c.Cover;
        if (source != null)
        {
            Directory.CreateDirectory(dir);
            // A new name each time, so every computer (and the picture cache) shows the new cover.
            string name = $"{Safe(id)}-{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(source).ToLowerInvariant()}";
            File.Copy(source, Path.Combine(dir, name), overwrite: true);
            c.Cover = name;
        }
        else c.Cover = "";
        if (Current.Id == id) Current.Cover = c.Cover;
        SaveConfig();
        try { if (old.Length > 0 && old != c.Cover) File.Delete(Path.Combine(dir, old)); } catch (Exception ex) { Log.Error("Old cover", ex); }
    }

    /// <summary>Takes a book out of the library (its files and your progress stay in the training folder).</summary>
    public static void RemoveCourse(string id)
    {
        Config.Courses.RemoveAll(c => c.Id == id);
        if (Config.ActiveId == id) Config.ActiveId = Config.Courses.FirstOrDefault()?.Id ?? "";
        SaveConfig();
        if (Config.Courses.FirstOrDefault(c => c.Id == Config.ActiveId) is CourseInfo next) Open(next.Id);
    }

    /// <param name="raise">Tell the app the book changed (which redraws the Training page). Background switches
    /// (counting review, picking today's book) pass false: raising it there redrew the page, which switched books
    /// again, endlessly, freezing Focus once a second book was added.</param>
    private static void LoadCurrent(bool raise = true)
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
        if (raise) Changed?.Invoke();
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
        // Changed weeks: two computers editing the same week keep the newest change for each day.
        try
        {
            string cfgPath = Path.Combine(Dir, "course.json");
            if (File.Exists(cfgPath) && JsonSerializer.Deserialize<CourseConfig>(File.ReadAllText(cfgPath)) is CourseConfig disk)
            {
                string oldest = Ymd(DateTime.Today.AddDays(-14));
                foreach (var (day, plan) in disk.WeekPlan)
                    if (string.CompareOrdinal(day, oldest) >= 0 && (!Config.WeekPlan.TryGetValue(day, out var mine) || plan.At > mine.At)) Config.WeekPlan[day] = plan;
            }
        }
        catch (Exception ex) { Log.Error("Course.SaveConfig merge", ex); }
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
        if (e.CourseId.Length == 0) e.CourseId = Pinned ?? Current.Id;
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

    /// <summary>True when every lesson planned for today is done (or none was planned).</summary>
    public static bool AllDoneToday() => NextDueToday() == null;

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

    // ---------- Thumbs up / down and Prioritize ----------

    /// <summary>+1 thumbs up (see it more), −1 thumbs down (see it less), 0 none. The latest vote wins.</summary>
    public static int VoteOf(string questionId) => CE.LastOrDefault(e => e.Kind == "vote" && e.Question == questionId)?.Grade ?? 0;
    public static void Vote(string questionId, int vote) => Add(new TrainingEvent { Kind = "vote", Question = questionId, Grade = vote });
    public static bool IsPrioritized(string questionId) => (CE.LastOrDefault(e => e.Kind == "priority" && e.Question == questionId)?.Grade ?? 0) > 0;
    public static void SetPriority(string questionId, bool on) => Add(new TrainingEvent { Kind = "priority", Question = questionId, Grade = on ? 1 : 0 });

    // ---------- Timeline review ----------

    /// <summary>The dated events for a lesson's timeline: this lesson's plus every finished lesson's (latest misses first), at most <paramref name="max"/>.</summary>
    public static List<(LessonRef Lesson, PackEvent E)> TimelineFor(LessonRef lesson, int max = 8)
    {
        var done = DoneLessons();
        var mine = lesson.Lesson.Timeline.Select(e => (lesson, e)).ToList();
        var earlier = Lessons().Where(l => l.Index != lesson.Index && done.Contains(l.Index)).SelectMany(l => l.Lesson.Timeline.Select(e => (l, e)))
            .OrderByDescending(x => TimelineMisses(x.e.Text)).ThenBy(_ => Guid.NewGuid()).ToList();
        return mine.Concat(earlier).Take(Math.Max(mine.Count, max)).ToList();
    }

    /// <summary>Timeline events from every lesson you've done in this book, the ones you misplace most first.</summary>
    public static List<(LessonRef Lesson, PackEvent E)> TimelineAll(int max = 8)
    {
        var done = DoneLessons();
        return Lessons().Where(l => done.Contains(l.Index)).SelectMany(l => l.Lesson.Timeline.Select(e => (l, e)))
            .OrderByDescending(x => TimelineMisses(x.e.Text)).ThenBy(_ => Guid.NewGuid()).Take(max).ToList();
    }

    // ---------- Timeline practice (spaced repetition by event) ----------

    /// <summary>Which book each event in a timeline round comes from (rounds can mix books).</summary>
    public static Dictionary<PackEvent, string> EventBook { get; } = new();

    public record TimelineState(DateTime Due, int Streak, int Seen, bool Graduated);

    /// <summary>
    /// An event's place in timeline practice. Exact placements space it out (2 days, then ×2.5) and after four in a row it
    /// graduates; close enough spaces it out more slowly; a miss brings it back sooner the further off it was.
    /// </summary>
    public static TimelineState TimelineStateOf(string bookId, PackEvent ev)
    {
        DateTime due = DateTime.MinValue; double gap = 0; int streak = 0, seen = 0;
        foreach (var e in Events.Where(e => e.Kind == "timeline" && e.Question == ev.Text && CourseOf(e) == bookId).OrderBy(e => e.At))
        {
            seen++;
            var at = e.At.ToLocalTime();
            int off = e.Grade >= 0 ? e.Grade : e.Correct ? 0 : 999;
            if (e.Correct)
            {
                bool exact = off == 0;
                streak = exact ? streak + 1 : 0;
                gap = gap <= 0 ? (exact ? 2 : 1) : gap * (exact ? 2.5 : 1.4);
                due = at.AddDays(gap);
            }
            else
            {
                streak = 0; gap = 0;
                double far = off / (double)Math.Max(5, ev.Within);
                due = far > 8 ? at.AddMinutes(10) : far > 3 ? at.AddHours(3) : at.AddDays(1);
            }
        }
        return new(due, streak, seen, streak >= 4);
    }

    /// <summary>Every event in the lessons you've done, in the books given (each book opened quietly, then back).</summary>
    public static List<(LessonRef Lesson, PackEvent E, string Book, int Order)> LearnedEvents(IEnumerable<string> bookIds)
    {
        var list = new List<(LessonRef, PackEvent, string, int)>();
        if (EventBook.Count > 5000) EventBook.Clear();
        _tlDue = null;
        var was = Current;
        foreach (var id in bookIds.Distinct().ToList())
        {
            if (Config.Courses.FirstOrDefault(c => c.Id == id) is not CourseInfo c) continue;
            try
            {
                if (c.Id != Current.Id) { Current = c; LoadCurrent(raise: false); }
                var done = DoneLessons();
                int order = 0;
                foreach (var l in Lessons().Where(l => done.Contains(l.Index)))
                {
                    order++;
                    foreach (var ev in l.Lesson.Timeline) { EventBook[ev] = c.Id; list.Add((l, ev, c.Id, order)); }
                }
            }
            catch (Exception ex) { Log.Error("Timeline events " + id, ex); }
        }
        if (Current.Id != was.Id) { Current = was; LoadCurrent(raise: false); }
        return list;
    }

    /// <summary>
    /// A Learn round: what's due, then the events of your last two lessons you haven't placed yet (from each of the day's
    /// books). Graduated events stay out.
    /// </summary>
    public static List<(LessonRef Lesson, PackEvent E)> TimelineLearn(IEnumerable<string> bookIds, int max)
    {
        var all = LearnedEvents(bookIds);
        var states = all.Select(x => (x, st: TimelineStateOf(x.Book, x.E))).Where(x => !x.st.Graduated).ToList();
        var due = states.Where(x => x.st.Seen > 0 && x.st.Due <= DateTime.Now).OrderBy(x => x.st.Due).Select(x => x.x);
        var fresh = states.Where(x => x.st.Seen == 0 && all.Where(y => y.Book == x.x.Book).Select(y => y.Order).DefaultIfEmpty(0).Max() - x.x.Order < 2).Select(x => x.x);
        return due.Concat(fresh).Distinct().Take(max).Select(x => (x.Lesson, x.E)).ToList();
    }

    /// <summary>Eras to review by (only ones holding an event you've learned are offered).</summary>
    public static readonly (string Name, int From, int To)[] Eras =
    {
        ("Israel before the exile", -2200, -598), ("Babylonian exile", -597, -539), ("Persian period", -538, -333),
        ("Greek rule (Ptolemies and Seleucids)", -332, -168), ("Maccabean revolt and Hasmonean rule", -167, -64),
        ("Roman rule and Herod the Great", -63, -5), ("Life of Jesus", -6, 30), ("The apostles and Paul", 30, 70),
        ("Jewish wars and the late first century", 66, 135), ("The early church", 100, 325), ("Councils and the later church", 325, 600),
        ("Middle Ages", 600, 1500), ("Reformation", 1500, 1650), ("Modern era", 1650, 2100)
    };

    /// <summary>A Review round: events you've learned between two years (any order, at most <paramref name="max"/>).</summary>
    public static List<(LessonRef Lesson, PackEvent E)> TimelineReview(IEnumerable<string> bookIds, int from, int to, int max) =>
        LearnedEvents(bookIds).Where(x => x.E.Year >= from && x.E.Year <= to).OrderBy(_ => Guid.NewGuid()).Take(max).Select(x => (x.Lesson, x.E)).ToList();

    private static (DateTime At, int Count)? _tlDue;
    /// <summary>Timeline events due in every book (counted at most once a minute).</summary>
    public static int TimelineDueCount()
    {
        if (_tlDue is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(1)) return c.Count;
        int n = LearnedEvents(Config.Courses.Select(x => x.Id)).Count(x => TimelineStateOf(x.Book, x.E) is { Seen: > 0, Graduated: false } st && st.Due <= DateTime.Now);
        _tlDue = (DateTime.Now, n);
        return n;
    }

    public static bool TimelineDoneToday() => Events.Any(e => e.Kind == "timelineDone" && e.Day == Today);

    public static int TimelineMisses(string text) =>
        CE.Count(e => e.Kind == "timeline" && e.Question == text && !e.Correct) - CE.Count(e => e.Kind == "timeline" && e.Question == text && e.Correct);

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
                if (lastGrade == 2 && misses == 0 && !IsPrioritized(q.Id) && VoteOf(q.Id) <= 0) continue;
                list.Add((l, q, misses * 2 + (lastGrade == 0 ? 2 : lastGrade == 1 ? 1 : 0) + (IsPrioritized(q.Id) ? 100 : 0) + VoteOf(q.Id) * 100));
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

    /// <summary>A question's review state, worked out from every answer on every device, in time order.
    /// Graduated: done with daily review (a practice or quiz question right in the lesson and again a few days later; or a
    /// memory card that has come through its 120-day review).</summary>
    public record QState(DateTime Due, int Streak, bool Misconception, int Seen, bool Graduated = false);

    /// <summary>
    /// Memory cards are the long-term facts (people, dates, places, ideas): they come back for months. Practice and quiz
    /// questions check understanding of the reading: they come back once, three days later, and graduate if you get them right.
    /// </summary>
    public static QState StateOf(string questionId) => StateOf(questionId, IsCard(questionId));

    public static QState StateOf(string questionId, bool longTerm)
    {
        DateTime due = DateTime.MinValue;
        int streak = 0, seen = 0;
        bool miscon = false;
        int[] gaps = longTerm ? new[] { 1, 3, 7, 14, 30, 60, 120 } : new[] { 3 };
        DateTime last = DateTime.MinValue;
        foreach (var e in CE.Where(e => e.Kind == "answer" && e.Question == questionId))
        {
            seen++;
            var day = DateTime.TryParse(e.Day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : e.At.ToLocalTime().Date;
            // Remembered for longer than planned (answered late, and right): the next gap builds on how long it actually lasted.
            int lasted = last == DateTime.MinValue ? 0 : Math.Max(0, (day - last).Days);
            bool late = due != DateTime.MinValue && day > due;
            last = day;
            if (e.Grade == 0) { streak = 0; miscon = false; due = day.AddDays(1); }
            else if (e.Grade == 2)
            {
                // ✓✓ Know it well: a much longer gap (two weeks the first time, then months).
                streak = Math.Max(streak + 2, 3); miscon = false;
                int g = gaps[Math.Min(streak - 1, gaps.Length - 1)] * 2;
                if (late) g = Math.Max(g, Math.Min(lasted * 2, 365));
                due = day.AddDays(g);
            }
            else if (e.Grade == 1 || (e.Correct && e.Sure))
            {
                streak++; miscon = false;
                int g = gaps[Math.Min(streak - 1, gaps.Length - 1)];
                if (late) g = Math.Max(g, Math.Min(lasted * 2, 365));
                due = day.AddDays(g);
            }
            else { streak = 0; miscon = !e.Correct && e.Sure; due = day.AddDays(1); }
        }
        // Thumbs up brings it back sooner (half the gap), thumbs down later (double); it always stays in the schedule.
        int vote = VoteOf(questionId);
        if (vote != 0 && last != DateTime.MinValue && due > last)
        {
            int gap = (due - last).Days;
            due = last.AddDays(vote > 0 ? Math.Max(1, gap / 2) : gap * 2);
        }
        bool graduated = streak > gaps.Length; // through every gap in a row
        return new QState(due, streak, miscon, seen, graduated);
    }

    private static HashSet<string>? _cardIds;
    private static string _cardIdsFor = "";
    /// <summary>Whether a question is a memory card (in any lesson of the course).</summary>
    public static bool IsCard(string questionId)
    {
        string key = Current.Id + "|" + string.Join(",", Packs.Select(p => p.Key + ":" + p.Value.GetHashCode()));
        if (_cardIds == null || _cardIdsFor != key)
        {
            _cardIds = Lessons().SelectMany(l => l.Lesson.Cards.Select(c => c.Id)).ToHashSet();
            _cardIdsFor = key;
        }
        return _cardIds.Contains(questionId);
    }

    // ---------- The waiting room for new memory cards ----------

    /// <summary>A memory card is in the review once it's been let in (or if it was first answered before the waiting room began).</summary>
    public static bool Released(string cardId)
    {
        if (CE.Any(e => e.Kind == "release" && e.Question == cardId)) return true;
        var first = CE.FirstOrDefault(e => e.Kind == "answer" && e.Question == cardId);
        return first != null && Config.IntakeSinceUtc is DateTime since && first.At < since;
    }

    private static double SecondsFor(bool card) => card ? 12 : 25; // a memory card, or a multiple-choice question

    /// <summary>
    /// Lets waiting memory cards into the review while today's review still fits in your daily review time (oldest
    /// lesson first). On a normal day every new card goes straight in; after a busy stretch, they join a few at a time.
    /// </summary>
    public static void ReleaseCards()
    {
        if (Config.IntakeSinceUtc == null) { Config.IntakeSinceUtc = DateTime.UtcNow; SaveConfig(); return; }
        var hidden = Hidden();
        var done = DoneLessons();
        var retired = RetiredConcepts();
        var today = DateTime.Today;
        double load = 0;
        var waiting = new List<string>();
        foreach (var l in Lessons().Where(l => done.Contains(l.Index)).OrderBy(l => l.Index))
            foreach (var (q, card) in l.Lesson.Practice.Select(pq => (pq, false)).Concat(l.Lesson.Quiz.Where(x => x.Kind == "mc").Select(pq => (pq, false)))
                                                     .Concat(l.Lesson.Cards.Select(pq => (pq, true))))
            {
                if (hidden.Contains(q.Id) || (q.Concept.Length > 0 && retired.Contains(q.Concept))) continue;
                var st = StateOf(q.Id, card);
                if (st.Seen == 0 || st.Graduated) continue;
                if (card && !Released(q.Id)) { waiting.Add(q.Id); continue; }
                if (st.Due <= today) load += SecondsFor(card);
            }
        double room = Math.Max(1, Config.ReviewMinutes) * 60 - load;
        foreach (var id in waiting)
        {
            if (room < SecondsFor(true)) break;
            Add(new TrainingEvent { Kind = "release", Question = id });
            room -= SecondsFor(true);
        }
    }

    /// <summary>How many memory cards are waiting to join the review (not counting ones whose concept you know for good).</summary>
    public static int WaitingCards()
    {
        var done = DoneLessons();
        var hidden = Hidden();
        var retired = RetiredConcepts();
        return Lessons().Where(l => done.Contains(l.Index)).SelectMany(l => l.Lesson.Cards)
            .Count(q => !hidden.Contains(q.Id) && !(q.Concept.Length > 0 && retired.Contains(q.Concept)) &&
                        StateOf(q.Id, true).Seen > 0 && !Released(q.Id));
    }

    /// <summary>
    /// Concepts you know for good: right on six separate days, spread over at least 90 days (so the long gaps that make
    /// facts stick have happened). Their questions leave daily review.
    /// </summary>
    public static HashSet<string> RetiredConcepts()
    {
        var result = new HashSet<string>();
        foreach (var g in CE.Where(e => e.Kind == "answer" && e.Concept.Length > 0 && e.Correct && e.Sure).GroupBy(e => e.Concept))
        {
            var days = g.Select(e => DateTime.TryParse(e.Day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : e.At.ToLocalTime().Date)
                        .Distinct().OrderBy(d => d).ToList();
            if (days.Count >= 6 && (days[^1] - days[0]).TotalDays >= 90) result.Add(g.Key);
        }
        return result;
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
        ReleaseCards(); // new memory cards join while today's review fits in your daily review time
        var retired = RetiredConcepts();
        var due = new List<(LessonRef, PackQuestion, QState)>();
        foreach (var l in Lessons().Where(l => done.Contains(l.Index)))
            foreach (var (q, card) in l.Lesson.Practice.Select(pq => (pq, false)).Concat(l.Lesson.Cards.Select(pq => (pq, true)))
                                                     .Concat(l.Lesson.Quiz.Where(x => x.Kind == "mc").Select(pq => (pq, false))))
            {
                if (hidden.Contains(q.Id)) continue;
                if (q.Concept.Length > 0 && retired.Contains(q.Concept)) continue;   // known for good
                if (card && !Released(q.Id)) continue;                                // still in the waiting room
                var s = StateOf(q.Id, card);
                if (s.Graduated) continue;                                            // done with daily review
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
