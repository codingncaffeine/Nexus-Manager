using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NexusManager.Editor;

/// <summary>
/// Middle truncation, which Avalonia's TextTrimming does not offer and iCUE uses
/// everywhere: "NVIDIA G...RTX 3080", "AMD Rad...raphics", "Gigabyte...S MASTER".
///
/// It matters more than it looks. Sensor and device names differ at the END as
/// often as at the start - "Temp #1" versus "Temp #2", "RTX 3080" versus
/// "RTX 5080" - so a trailing ellipsis throws away the half that tells two
/// entries apart, and a column of tiles all reading "NVIDIA GeForce..." is
/// useless.
/// </summary>
public static class TextFit
{
    /// <summary>Applies middle truncation to <paramref name="block"/> whenever it
    /// is laid out, so the result tracks the real available width rather than a
    /// guessed character count.</summary>
    public static void Middle(TextBlock block, string full)
    {
        block.Text = full;
        block.TextTrimming = TextTrimming.None;
        block.TextWrapping = TextWrapping.NoWrap;

        // Assigning Text can change the desired size, which can change Bounds,
        // which re-enters this handler: a change handler that rewrites its own
        // input is how the property panel once pinned a core at 100%. Guarded
        // twice - re-entrancy, and a memo so an unchanged width does no work.
        bool fitting = false;
        double lastWidth = -1;

        void Apply()
        {
            if (fitting) return;
            double avail = block.Bounds.Width;
            if (Math.Abs(avail - lastWidth) < 0.5) return;
            lastWidth = avail;
            if (avail <= 1) return;
            fitting = true;
            try
            {
                string fitted = Fit(full, block, avail);
                if (block.Text != fitted) block.Text = fitted;
            }
            finally { fitting = false; }
        }

        block.PropertyChanged += (_, e) =>
        {
            // Re-fit on resize only. Reacting to the Text change too would be a
            // handler that rewrites what it is reacting to - an infinite loop.
            if (e.Property == Visual.BoundsProperty) Apply();
        };
        Apply();
    }

    private static double Measure(string text, TextBlock block) =>
        new FormattedText(
            text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight),
            block.FontSize, Brushes.White).Width;

    private static string Fit(string text, TextBlock block, double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || Measure(text, block) <= maxWidth) return text;

        for (int keep = text.Length - 1; keep >= 2; keep--)
        {
            int head = (keep + 1) / 2, tail = keep - head;
            string candidate = string.Concat(
                text.AsSpan(0, head), "...", text.AsSpan(text.Length - tail));
            if (Measure(candidate, block) <= maxWidth) return candidate;
        }
        return "...";
    }
}
