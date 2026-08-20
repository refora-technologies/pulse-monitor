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
    /// Renders the subset of Markdown that Pulse's own release notes use: "##" and "###"
    /// headings, "-" bullets, and inline "**bold**". Anything else falls through as a
    /// plain paragraph, so an unexpected note format degrades to readable text rather
    /// than showing raw markup. Deliberately hand-rolled to avoid taking a dependency
    /// on a Markdown library for one dialog.
    /// </summary>
    private void RenderNotes(string notes, string version)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            NotesPanel.Children.Add(new TextBlock
            {
                Text = "No release notes were provided for this version.",
                FontSize = 12,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6E, 0x6B, 0x8C)),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        bool skippedTitle = false;

        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                NotesPanel.Children.Add(new Border { Height = 6 });
                continue;
            }

            // Our own notes open with "## What's new in vX.Y.Z", which the dialog header
            // already says. Drop that one line rather than showing it twice, but only
            // when it really is that title so arbitrary notes are left intact.
            if (!skippedTitle && line.StartsWith("## ")
                && (line.Contains("what's new", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(version, StringComparison.OrdinalIgnoreCase)))
            {
                skippedTitle = true;
                continue;
            }

            if (line.StartsWith("### "))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text       = line[4..].Trim().ToUpperInvariant(),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0x8B, 0x5C, 0xF6)),
                    Margin     = new Thickness(0, 12, 0, 6),
                });
            }
            else if (line.StartsWith("## "))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text       = line[3..].Trim(),
                    FontSize   = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xED, 0xE9, 0xFC)),
                    Margin     = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text       = "•",
                    FontSize   = 12,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0x5C, 0x59, 0x7E)),
                    Margin     = new Thickness(2, 0, 9, 0),
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var body = BuildInline(line[2..].Trim());
                Grid.SetColumn(body, 1);
                row.Children.Add(body);

                NotesPanel.Children.Add(row);
            }
            else
            {
                var para = BuildInline(line);
                para.Margin = new Thickness(0, 0, 0, 6);
                NotesPanel.Children.Add(para);
            }
        }
    }

    /// Builds a wrapped TextBlock, turning **bold** spans into bold runs.
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

        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|([^*]+|\*)"))
        {
            if (m.Groups[1].Success)
            {
                block.Inlines.Add(new Run(m.Groups[1].Value)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xE5, 0xE2, 0xF4)),
                });
            }
            else
            {
                block.Inlines.Add(new Run(m.Value));
            }
        }

        return block;
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e) => Close();
}
