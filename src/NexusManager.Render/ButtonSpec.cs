using NexusManager.Actions;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// A touch button on the strip - the `button_attachments` half of .cuescreens.
///
/// Corsair fix six slots (`TouchScreen_Button1..6`) and 640/6 is about 106px,
/// roughly the smallest comfortable target on a 9.4mm-tall strip (D11). Ours are
/// weighted rather than fixed, so a screen can carry two wide buttons or six
/// narrow ones, but six remains the practical limit.
///
/// Buttons share the strip with sensor modules: both are cells laid out by
/// weight across the same 640px, so a screen can be all readouts, all buttons,
/// or a mix.
/// </summary>
public sealed class ButtonSpec
{
    public string Label { get; set; } = "";

    /// <summary>Share of the strip, exactly as a module's weight.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Text colour. Null takes the theme's caption colour.</summary>
    public string? Color { get; set; }

    /// <summary>Fill behind the button. Null draws only an outline, which keeps
    /// a button legible over a background image without hiding it.</summary>
    public string? Background { get; set; }

    /// <summary>Outline colour. Null falls back to the text colour, dimmed.</summary>
    public string? Border { get; set; }

    /// <summary>What pressing it does.</summary>
    public SystemAction Action { get; set; } = new();

    /// <summary>
    /// A user's own image on the button - Corsair's "drag-and-drop custom
    /// graphics". An absolute path, or one relative to the config directory.
    ///
    /// ⛔ Nothing of Corsair's ships with this project; the image is read from
    /// the user's own file at run time, exactly as a background is.
    ///
    /// With a label the image takes the icon's slot on the left. Without one it
    /// fills the button, which is what a picture-only button wants and what a
    /// ~106px cell can actually show.
    /// </summary>
    public string? Image { get; set; }

    /// <summary>Cover crops to fill the cell; Contain fits the whole image
    /// inside it. Contain by default - a cropped icon is usually the wrong
    /// icon.</summary>
    public bool ImageCover { get; set; }

    /// <summary>A glyph drawn above the label. Reuses the sensor icon shapes, so
    /// a volume button can carry the speaker-ish mark the readouts already use.</summary>
    public ButtonIcon Icon { get; set; } = ButtonIcon.None;
}

/// <summary>Icons a button can carry. Deliberately a short closed list: these are
/// vector-drawn, not image assets, so they stay sharp at 48px and ship nothing.</summary>
public enum ButtonIcon
{
    None, VolumeUp, VolumeDown, Mute, Play, Next, Previous,
    Launch, Screen, Key, Brightness,
}

public readonly record struct ButtonLayout(ButtonSpec Spec, SKRect Rect, int Index);
