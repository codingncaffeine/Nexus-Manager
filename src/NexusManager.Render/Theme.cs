using System.Globalization;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Colours and type sizes. Every colour is a "#RRGGBB" or "#AARRGGBB" string so
/// a config file stays hand-editable.
///
/// Palette note (D12): this panel's green primary is yellow-shifted, so a pure
/// green reads lime and blue+green reads white. Pick colours by looking at the
/// panel, not by choosing values that look right on a monitor.
/// </summary>
public sealed class Theme
{
    public string Background { get; set; } = "#08080A";
    public string Caption    { get; set; } = "#F2F2F5";
    public string Value      { get; set; } = "#EBEBF0";
    public string Unit       { get; set; } = "";          // empty = follow the value colour, as iCUE does
    public string ChartLine  { get; set; } = "#EBEBF0";
    public string Divider    { get; set; } = "#2C2C34";

    /// <summary>Colour used once a reading passes its module's warn threshold.</summary>
    public string Warn { get; set; } = "#FFAA28";
    /// <summary>Colour used once a reading passes its module's hot threshold.</summary>
    public string Hot  { get; set; } = "#FF463C";

    /// <summary>0-255. Opacity of the filled area under the chart line.</summary>
    public byte ChartFillAlpha { get; set; } = 166;
    /// <summary>0-255. Chart lines sit behind the numbers, so usually below 255.</summary>
    public byte ChartLineAlpha { get; set; } = 255;

    public float CaptionSize { get; set; } = 12f;
    public float ValueSize   { get; set; } = 22f;
    public string FontFamily { get; set; } = "DejaVu Sans";

    /// <summary>Labels are bold by default: at 12px on a 48px strip the caption
    /// competes with a much larger number for attention, and weight reads better
    /// than size for that.</summary>
    public bool CaptionBold { get; set; } = true;
    public bool ValueBold { get; set; }

    /// <summary>Show the session low and high beside the reading (iCUE's down/up
    /// arrow pair). Drawn inline rather than on its own row: a 48px strip has no
    /// vertical room for the four-row tile iCUE uses on a desktop.</summary>
    public bool ShowMinMax { get; set; } = true;
    public string MinMax { get; set; } = "#8A8A96";

    /// <summary>When true, the value and chart take the warn/hot colour. When
    /// false, only the value does and the chart keeps its own colour.</summary>
    public bool TintChartWithState { get; set; } = true;

    [JsonIgnore] public SKColor BackgroundColor => Parse(Background, SKColors.Black);
    [JsonIgnore] public SKColor CaptionColor    => Parse(Caption, SKColors.Gray);
    [JsonIgnore] public SKColor ValueColor      => Parse(Value, SKColors.White);
    [JsonIgnore] public SKColor MinMaxColor     => Parse(MinMax, SKColors.Gray);
    [JsonIgnore] public SKColor ChartLineColor  => Parse(ChartLine, SKColors.White);
    [JsonIgnore] public SKColor DividerColor    => Parse(Divider, SKColors.DarkGray);
    [JsonIgnore] public SKColor WarnColor       => Parse(Warn, SKColors.Orange);
    [JsonIgnore] public SKColor HotColor        => Parse(Hot, SKColors.Red);

    /// <summary>Accepts #RGB, #RRGGBB and #AARRGGBB. Falls back rather than throwing,
    /// so one bad colour in a config cannot take the whole display down.</summary>
    public static SKColor Parse(string? text, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s[0] == '#') s = s[1..];

        static bool Hex(ReadOnlySpan<char> s, out uint v) =>
            uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);

        switch (s.Length)
        {
            case 3 when Hex(s, out uint v3):
            {
                byte r = (byte)(((v3 >> 8) & 0xF) * 17);
                byte g = (byte)(((v3 >> 4) & 0xF) * 17);
                byte b = (byte)((v3 & 0xF) * 17);
                return new SKColor(r, g, b);
            }
            case 6 when Hex(s, out uint v6):
                return new SKColor((byte)(v6 >> 16), (byte)(v6 >> 8), (byte)v6);
            case 8 when Hex(s, out uint v8):
                return new SKColor((byte)(v8 >> 16), (byte)(v8 >> 8), (byte)v8, (byte)(v8 >> 24));
            default:
                return fallback;
        }
    }
}
