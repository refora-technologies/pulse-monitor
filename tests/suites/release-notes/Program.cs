// Tests how release notes are read for the update dialog.
//
// This exists because the previous reader printed anything it did not recognise, so our own
// 1.2.0 notes showed users literal "---", literal backticks and two literal ``` fences in the
// dialog that asks them to trust an update. Nothing failed, nothing was logged, it just looked
// broken, and it was only found by reading the code.
//
// The last section runs against the real published notes, so a future release written with
// markup the dialog cannot render fails here rather than in front of users.

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static int _pass, _fail;
    static Type _win = null!, _block = null!, _span = null!;
    static MethodInfo _parseNotes = null!, _parseInline = null!;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    /// Parsed blocks as "Kind:Text" pairs, which is all the assertions need.
    static (string Kind, string Text)[] Blocks(string notes, string version = "v9.9.9")
    {
        var list = (IEnumerable)_parseNotes.Invoke(null, new object[] { notes, version })!;
        return list.Cast<object>()
            .Select(b => (
                _block.GetProperty("Kind")!.GetValue(b)!.ToString()!,
                (string)_block.GetProperty("Text")!.GetValue(b)!))
            .ToArray();
    }

    static (string Text, bool Bold, bool Code)[] Spans(string line)
    {
        var list = (IEnumerable)_parseInline.Invoke(null, new object[] { line })!;
        return list.Cast<object>()
            .Select(s => (
                (string)_span.GetProperty("Text")!.GetValue(s)!,
                (bool)_span.GetProperty("Bold")!.GetValue(s)!,
                (bool)_span.GetProperty("Code")!.GetValue(s)!))
            .ToArray();
    }

    static int Main(string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }

        var asm = Assembly.LoadFrom(dll);
        _win   = asm.GetType("Pulse.Views.WhatsNewWindow")!;
        _block = asm.GetType("Pulse.Views.WhatsNewWindow+NoteBlock")!;
        _span  = asm.GetType("Pulse.Views.WhatsNewWindow+NoteSpan")!;

        _parseNotes  = _win.GetMethod("ParseNotes",  BindingFlags.Public | BindingFlags.Static)!;
        _parseInline = _win.GetMethod("ParseInline", BindingFlags.Public | BindingFlags.Static)!;

        Console.WriteLine("Release note rendering\n");

        Markup();
        Inline();
        Degrading();
        RealNotes(args.Length > 0 ? args[0] : null);

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── the markup that shipped broken ──────────────────────────────────────────────
    static void Markup()
    {
        Console.WriteLine("Markup must never reach the screen as itself:");

        var rule = Blocks("before\n\n---\n\nafter");
        Check("a horizontal rule becomes a separator",
              rule.Any(b => b.Kind == "Separator"),
              string.Join(" / ", rule.Select(b => b.Kind)));
        Check("and the dashes are not printed",
              rule.All(b => b.Text != "---"),
              "no block still carries the literal text");

        var fenced = Blocks("text\n```\nabc123\n```\nmore");
        Check("a fenced block becomes one code block",
              fenced.Count(b => b.Kind == "Code") == 1,
              string.Join(" / ", fenced.Select(b => b.Kind)));
        Check("the fence markers are gone",
              fenced.All(b => !b.Text.Contains("```")),
              "no block still carries the fence");
        Check("the code inside survives",
              fenced.Any(b => b.Kind == "Code" && b.Text == "abc123"),
              fenced.FirstOrDefault(b => b.Kind == "Code").Text);

        var multi = Blocks("```\nline one\nline two\n```");
        Check("a multi line block stays one block with its lines intact",
              multi.Count(b => b.Kind == "Code") == 1
              && multi.First(b => b.Kind == "Code").Text == "line one\nline two");

        // Rules use three different markers and must not be mistaken for bullets.
        foreach (var marker in new[] { "---", "***", "___", "- - -", "-----" })
            Check($"\"{marker}\" reads as a rule",
                  Blocks(marker).Any(b => b.Kind == "Separator"),
                  string.Join(",", Blocks(marker).Select(b => b.Kind)));
    }

    // ── inline runs ─────────────────────────────────────────────────────────────────
    static void Inline()
    {
        Console.WriteLine("\nInline markers must style text rather than appear in it:");

        var code = Spans("SHA-256 of `PulseSetup.exe` today");
        Check("backticks mark code without being shown",
              code.Any(s => s.Code && s.Text == "PulseSetup.exe")
              && code.All(s => !s.Text.Contains('`')),
              string.Join(" | ", code.Select(s => $"{s.Text}{(s.Code ? " [code]" : "")}")));

        var bold = Spans("a **strong** word");
        Check("asterisks mark bold without being shown",
              bold.Any(s => s.Bold && s.Text == "strong")
              && bold.All(s => !s.Text.Contains('*')));

        Check("the whole line survives, nothing dropped",
              string.Concat(Spans("a **strong** word").Select(s => s.Text)) == "a strong word",
              string.Concat(Spans("a **strong** word").Select(s => s.Text)));

        Check("bold and code together",
              Spans("**bold** and `code`").Count(s => s.Bold) == 1
              && Spans("**bold** and `code`").Count(s => s.Code) == 1);
    }

    // ── it must still degrade, not throw ────────────────────────────────────────────
    static void Degrading()
    {
        Console.WriteLine("\nMalformed or unexpected notes must degrade quietly:");

        Check("empty notes produce nothing", Blocks("").Length == 0, "");
        Check("whitespace only produces nothing", Blocks("   \n  ").Length == 0, "");

        var unclosed = Blocks("intro\n```\ndangling");
        Check("an unclosed fence still yields its content rather than swallowing the rest",
              unclosed.Any(b => b.Kind == "Code" && b.Text.Contains("dangling")),
              string.Join(" / ", unclosed.Select(b => b.Kind)));

        Check("a stray asterisk is left alone",
              string.Concat(Spans("2 * 3 = 6").Select(s => s.Text)) == "2 * 3 = 6");
        Check("a stray backtick is left alone",
              string.Concat(Spans("a ` b").Select(s => s.Text)) == "a ` b");
        Check("unmatched bold is left alone",
              string.Concat(Spans("**not closed").Select(s => s.Text)) == "**not closed");

        Check("headings and bullets still work",
              Blocks("## Big\n### Small\n- point").Select(b => b.Kind).SequenceEqual(
                  new[] { "Heading", "Subheading", "Bullet" }),
              string.Join(",", Blocks("## Big\n### Small\n- point").Select(b => b.Kind)));

        Check("the duplicated title line is dropped",
              !Blocks("## What's new in v9.9.9\n- thing").Any(b => b.Kind == "Heading"),
              "the dialog header already says it");

        Check("an unrelated heading is kept",
              Blocks("## Fixed\n- thing").Any(b => b.Kind == "Heading"));
    }

    // ── the notes we actually publish ───────────────────────────────────────────────
    static void RealNotes(string? path)
    {
        Console.WriteLine("\nThe notes we actually publish must render cleanly:");

        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("    (skipped: pass a file of the published release body to check it)");
            return;
        }

        var blocks = Blocks(File.ReadAllText(path));

        // A paragraph still carrying markup is exactly the bug this suite exists for.
        var leaking = blocks
            .Where(b => b.Kind == "Paragraph"
                        && (b.Text.Contains("```") || b.Text.Trim() == "---"
                            || b.Text.StartsWith("|") || b.Text.Contains("](")))
            .ToArray();

        Check("no line renders as raw markup", leaking.Length == 0,
              leaking.Length == 0 ? $"{blocks.Length} blocks parsed"
                                  : string.Join(" | ", leaking.Select(b => b.Text)));
    }
}
