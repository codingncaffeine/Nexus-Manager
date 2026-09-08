using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Which installed font families are worth offering.
///
/// This machine reports **1911** families and **1873** of them begin with
/// "Noto" — script variants (Devanagari, Ethiopic, Tai Tham …) that nobody
/// picks for a 640x48 strip. A picker that lists all of them is a picker
/// nobody can use.
///
/// ⛔ Filtered by RULE, never by a hard-coded list of family names. A name
/// allow-list is right on the machine it was written on and silently shorter
/// everywhere else - the same failure shape as a packaging step that copies
/// files by name and ships most of them missing while every step reports
/// success.
/// </summary>
public static class FontCatalog
{
    /// <summary>Noto families that are general-purpose rather than script-specific.
    /// Noto's convention is "Noto &lt;Style&gt; &lt;Script&gt;", one family per script,
    /// so a name with nothing after the style IS the general-purpose face.</summary>
    private static readonly HashSet<string> GeneralNoto = new(StringComparer.OrdinalIgnoreCase)
    {
        "Noto Sans", "Noto Serif", "Noto Sans Mono",
        "Noto Sans Display", "Noto Serif Display",
    };

    /// <summary>⛔ Weight words only. Width words (Condensed, Narrow) are NOT here:
    /// the editor has a Bold toggle, so a weight variant duplicates an axis it
    /// already controls, but it has no width control - dropping Condensed would
    /// remove the only way to reach that shape.</summary>
    private static readonly string[] WeightWords =
    [
        "Thin", "ExtraLight", "Extra Light", "UltraLight", "Light", "Book",
        "Medium", "DemiBold", "Demi Bold", "SemiBold", "Semi Bold",
        "ExtraBold", "Extra Bold", "UltraBold", "Bold", "Black", "Heavy",
    ];

    private static List<string>? _usable;

    /// <summary>
    /// Does asking for this family actually GET this family?
    ///
    /// ⛔ Measured, and it removes real entries: "DejaVu Sans Condensed",
    /// "DejaVu Serif Condensed" and "Open Sans Condensed" are all reported as
    /// installed families, and all three resolve to their non-condensed base.
    /// Skia reaches a width variant through SKFontStyle, not by family name, so
    /// picking one of those in the editor renders the ordinary face and nothing
    /// the user did has any effect. An option that silently does nothing is
    /// worse than an absent one.
    ///
    /// This supersedes the earlier intention to keep width variants because the
    /// editor has no width control: they are not merely unreachable through the
    /// UI, they are unreachable at all.
    /// </summary>
    private static bool ResolvesToItself(string family)
    {
        try
        {
            using var tf = SKTypeface.FromFamilyName(family);
            return tf is not null
                   && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Every family the system reports, unfiltered.</summary>
    public static List<string> All()
    {
        // A fresh manager, not SKFontManager.Default: the default is a
        // process-wide singleton that enumerates the system fonts once, so a
        // font installed after launch would never appear however often this
        // is called.
        using var fm = SKFontManager.CreateDefault();
        return fm.GetFontFamilies()
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The families worth showing. <paramref name="keep"/> is always included
    /// even when a rule would drop it, so a configuration that already names a
    /// font never silently changes to a different one.
    /// </summary>
    public static List<string> Usable(string? keep = null)
    {
        _usable ??= Build();
        if (string.IsNullOrEmpty(keep) ||
            _usable.Contains(keep, StringComparer.OrdinalIgnoreCase))
            return _usable;

        var withKeep = new List<string>(_usable) { keep };
        withKeep.Sort(StringComparer.OrdinalIgnoreCase);
        return withKeep;
    }

    private static List<string> Build()
    {
        var all = All();
        var installed = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);

        // ⛔ Cheap name rules FIRST. RendersLatin loads a typeface, and doing
        // that 1911 times happens on the UI thread while the property panel is
        // being built. The name rules cut it to a couple of dozen first, so the
        // expensive test runs a couple of dozen times.
        var candidates = all
            .Where(f => !IsScriptNoto(f))
            .Where(f => !IsWeightVariant(f, installed))
            .ToList();

        var usable = candidates.Where(f => ResolvesToItself(f) && RendersReadout(f)).ToList();

        // If the rules somehow removed everything - an unusual font set, or a
        // Skia that reports families this cannot probe - show everything rather
        // than an empty picker. An empty dropdown is worse than a noisy one.
        return usable.Count > 0 ? usable : all;
    }

    /// <summary>"Noto Sans Devanagari" yes, "Noto Sans" no.</summary>
    private static bool IsScriptNoto(string family) =>
        family.StartsWith("Noto", StringComparison.OrdinalIgnoreCase)
        && !GeneralNoto.Contains(family);

    /// <summary>"Open Sans Light" when "Open Sans" is also installed.</summary>
    private static bool IsWeightVariant(string family, HashSet<string> installed)
    {
        foreach (string w in WeightWords)
        {
            if (!family.EndsWith(" " + w, StringComparison.OrdinalIgnoreCase)) continue;
            string basis = family[..^(w.Length + 1)].TrimEnd();
            if (basis.Length > 0 && installed.Contains(basis)) return true;
        }
        return false;
    }

    /// <summary>
    /// <summary>
    /// <summary>
    /// Can this family render a readout at all?
    ///
    /// ⛔ DELIBERATELY PERMISSIVE, and it was tightened once and then loosened
    /// again on purpose. An earlier version demanded all 62 of A-Z, a-z and
    /// 0-9. That would silently hide exactly the fonts someone installs FOR
    /// this app: a 640x48 strip invites pixel and LED display faces, and those
    /// are very often uppercase-only. Measurement also showed it earned
    /// nothing - every family it would have removed was already removed by
    /// ResolvesToItself, and the two symbol faces it was written to catch
    /// (D050000L, Standard Symbols PS) genuinely do carry Latin, confirmed
    /// against fontconfig's own charset.
    ///
    /// So this asks only what the panel actually needs: the digits, and at
    /// least one complete case of the alphabet. A face that cannot manage that
    /// cannot draw "TEMP 44°C" and is not a font this app can use.
    /// </summary>
    private static bool RendersReadout(string family)
    {
        const string Digits = "0123456789";
        const string Upper  = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string Lower  = "abcdefghijklmnopqrstuvwxyz";
        try
        {
            using var tf = SKTypeface.FromFamilyName(family);
            if (tf is null) return false;
            using var font = new SKFont(tf, 12);
            return Covers(font, Digits) && (Covers(font, Upper) || Covers(font, Lower));
        }
        catch (Exception)
        {
            return false;   // a family that throws on load is not one to offer
        }
    }

    private static bool Covers(SKFont font, string text)
    {
        ushort[] glyphs = font.GetGlyphs(text);
        return glyphs.Length == text.Length && Array.TrueForAll(glyphs, g => g != 0);
    }

    /// <summary>
    /// Forget the cached list so a font installed while the app is running can
    /// appear. ⛔ Our cache is only half of it - Skia's own font manager
    /// enumerates once too, which is why All() builds a fresh manager rather
    /// than reusing SKFontManager.Default.
    /// </summary>
    public static void Refresh() => _usable = null;
}
