using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Services;

using WpfColor = System.Windows.Media.Color;

namespace Pulse.Views;

/// <summary>
/// Shows the GitHub release notes for a pending update and asks the user to confirm
/// before anything is downloaded. The notes are already fetched during the update
/// check (UpdateInfo.Notes), so this costs no extra network work.
/// </summary>
public partial class WhatsNewWindow : Window
{
    /// True when the user chose to download rather than dismissing the dialog.
    public bool Accepted { get; private set; }

    public WhatsNewWindow(UpdateInfo info)
    {
        InitializeComponent();

        VersionText.Text = info.DisplayVersion;

        SizeText.Text = info.InstallerSize > 0
            ? $"Download size {info.InstallerSize / 1024.0 / 1024.0:F0} MB"
            : "";

        RenderNotes(info.Notes, info.DisplayVersion);

        // Evaluated once after layout as well as on scroll: if the notes happen to fit, no
        // scroll event ever fires and the hint would never be told to stay hidden.
        Loaded += (_, _) => NotesScroller_ScrollChanged(NotesScroller, null!);

        // Dragging anywhere on the dialog moves it, since there is no title bar.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };

        // Escape dismisses, matching normal dialog behaviour.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>
    /// One piece of a release note, once the Markdown has been read.
    ///
    /// Parsing is kept apart from rendering so the awkward part can be tested without standing
    /// up a window. It needed testing: the previous reader silently printed anything it did not
    /// recognise, so our own 1.2.0 notes showed users literal "---" and literal ``` fences in
    /// the dialog that asks them to trust an update.
    /// </summary>
    public enum NoteBlockKind { Blank, Heading, Subheading, Bullet, Paragraph, Separator, Code }

    public readonly record struct NoteBlock(NoteBlockKind Kind, string Text);

    /// A run of text within a line, and how it should be drawn.
    public readonly record struct NoteSpan(string Text, bool Bold, bool Code);

    /// <summary>
    /// Reads the Markdown that GitHub release notes actually use: "##" and "###" headings,
    /// "-" bullets, "---" rules, fenced code blocks, and inline "**bold**" and `code`.
    ///
    /// Anything still unrecognised is returned as a plain paragraph, which is the right
    /// fallback for prose. The point of handling rules and fences explicitly is that they are
    /// markup rather than prose, so printing them verbatim is never what was meant.
    /// </summary>
    public static List<NoteBlock> ParseNotes(string notes, string version)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(notes)) return blocks;

        bool skippedTitle = false;
        bool insideFence  = false;
        var fenced = new List<string>();

        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();

            // Fence markers are never content. An unclosed fence is flushed at the end rather
            // than swallowing the remainder of the notes.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (insideFence)
                {
                    blocks.Add(new NoteBlock(NoteBlockKind.Code, string.Join("\n", fenced)));
                    fenced.Clear();
                }
                insideFence = !insideFence;
                continue;
            }

            if (insideFence)
            {
                fenced.Add(line);
                continue;
            }

            if (trimmed.Length == 0)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Blank, ""));
                continue;
            }

            // Checked before bullets: "* * *" is a rule and would otherwise read as a bullet.
            if (IsRule(trimmed))
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Separator, ""));
                continue;
            }

            // Our own notes open with "## What's new in vX.Y.Z", which the dialog header
            // already says. Drop that one line rather than showing it twice, but only when it
            // really is that title so arbitrary notes are left intact.
            if (!skippedTitle && line.StartsWith("## ")
                && (line.Contains("what's new", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(version, StringComparison.OrdinalIgnoreCase)))
            {
                skippedTitle = true;
                continue;
            }

            if (line.StartsWith("### "))
                blocks.Add(new NoteBlock(NoteBlockKind.Subheading, line[4..].Trim()));
            else if (line.StartsWith("## "))
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, line[3..].Trim()));
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, line[2..].Trim()));
            else
                blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, line));
        }

        if (fenced.Count > 0)
            blocks.Add(new NoteBlock(NoteBlockKind.Code, string.Join("\n", fenced)));

        return blocks;
    }

    /// A horizontal rule: three or more of the same marker, nothing else but spaces.
    private static bool IsRule(string trimmed)
    {
        foreach (var marker in new[] { '-', '*', '_' })
        {
            int count = 0;
            bool onlyThis = true;

            foreach (var c in trimmed)
            {
                if (c == marker) count++;
                else if (c != ' ') { onlyThis = false; break; }
            }

            if (onlyThis && count >= 3) return true;
        }

        return false;
    }

    /// <summary>
    /// Splits a line into bold, code and plain runs. Unmatched markers are returned as plain
    /// text, so a stray asterisk or backtick reads as itself rather than eating the line.
    /// </summary>
    public static List<NoteSpan> ParseInline(string text)
    {
        var spans = new List<NoteSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|`([^`]+)`|([^*`]+|[*`])"))
        {
            if (m.Groups[1].Success)      spans.Add(new NoteSpan(m.Groups[1].Value, true,  false));
            else if (m.Groups[2].Success) spans.Add(new NoteSpan(m.Groups[2].Value, false, true));
            else                          spans.Add(new NoteSpan(m.Value,           false, false));
        }

        return spans;
    }

    private void RenderNotes(string notes, string version)
    {
        var blocks = ParseNotes(notes, version);

        if (blocks.Count == 0)
        {
            NotesPanel.Children.Add(new TextBlock
            {
                Text = "No release notes were provided for this version.",
                FontSize = 12,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0xA9, 0xA4, 0xCE)),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case NoteBlockKind.Blank:
                    NotesPanel.Children.Add(new Border { Height = 6 });
                    break;

                case NoteBlockKind.Separator:
                    NotesPanel.Children.Add(new Border
                    {
                        Height     = 1,
                        Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x25, 0x48)),
                        Margin     = new Thickness(0, 8, 0, 10),
                    });
                    break;

                case NoteBlockKind.Subheading:
                    NotesPanel.Children.Add(new TextBlock
                    {
                        Text       = block.Text.ToUpperInvariant(),
                        FontSize   = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(0xA7, 0x8B, 0xFA)),
                        Margin     = new Thickness(0, 12, 0, 6),
                    });
                    break;

                case NoteBlockKind.Heading:
                    NotesPanel.Children.Add(new TextBlock
                    {
                        Text         = block.Text,
                        FontSize     = 14,
                        FontWeight   = FontWeights.Bold,
                        Foreground   = new SolidColorBrush(WpfColor.FromRgb(0xED, 0xE9, 0xFC)),
                        Margin       = new Thickness(0, 8, 0, 4),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    break;

                case NoteBlockKind.Code:
                    // Consolas rather than a bundled face: it ships with every Windows we
                    // support, and a checksum is unreadable in a proportional font.
                    NotesPanel.Children.Add(new Border
                    {
                        Background   = new SolidColorBrush(WpfColor.FromRgb(0x14, 0x12, 0x28)),
                        BorderBrush  = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x25, 0x48)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding      = new Thickness(10, 8, 10, 8),
                        Margin       = new Thickness(0, 2, 0, 8),
                        Child = new TextBlock
                        {
                            Text         = block.Text,
                            FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
                            FontSize     = 10.5,
                            Foreground   = new SolidColorBrush(WpfColor.FromRgb(0xA6, 0xA2, 0xC6)),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                    break;

                case NoteBlockKind.Bullet:
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var dot = new TextBlock
                    {
                        Text       = "•",
                        FontSize   = 12,
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(0xA9, 0xA4, 0xCE)),
                        Margin     = new Thickness(2, 0, 9, 0),
                    };
                    Grid.SetColumn(dot, 0);
                    row.Children.Add(dot);

                    var body = BuildInline(block.Text);
                    Grid.SetColumn(body, 1);
                    row.Children.Add(body);

                    NotesPanel.Children.Add(row);
                    break;
                }

                default:
                {
                    var para = BuildInline(block.Text);
                    para.Margin = new Thickness(0, 0, 0, 6);
                    NotesPanel.Children.Add(para);
                    break;
                }
            }
        }
    }

    /// Builds a wrapped TextBlock from the runs ParseInline found.
    private static TextBlock BuildInline(string text)
    {
        var block = new TextBlock
        {
            FontSize     = 11.5,
            FontWeight   = FontWeights.Medium,
            Foreground   = new SolidColorBrush(WpfColor.FromRgb(0xA6, 0xA2, 0xC6)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 17,
        };

        foreach (var span in ParseInline(text))
        {
            if (span.Bold)
            {
                block.Inlines.Add(new Run(span.Text)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xE5, 0xE2, 0xF4)),
                });
            }
            else if (span.Code)
            {
                block.Inlines.Add(new Run(span.Text)
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xC4, 0xB5, 0xFD)),
                });
            }
            else
            {
                block.Inlines.Add(new Run(span.Text));
            }
        }

        return block;
    }

    /// <summary>
    /// Shows the scroll hint only while there is more to read.
    ///
    /// Hiding the scrollbar removed the only clue that the notes continue past the fold, and
    /// a release note nobody scrolls is a release note nobody reads. The hint fades out on
    /// the last line so it never sits there pointing at nothing.
    /// </summary>
    private void NotesScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ScrollHint == null || NotesScroller == null) return;

        // A small tolerance: floating point means "at the bottom" is rarely exact.
        const double epsilon = 2;
        bool more = NotesScroller.ScrollableHeight > epsilon
                 && NotesScroller.VerticalOffset < NotesScroller.ScrollableHeight - epsilon;

        ScrollHint.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e) => Close();
}
