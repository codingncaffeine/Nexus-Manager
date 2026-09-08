using System.IO.Compression;
using System.Xml.Linq;
using NexusManager.Actions;

namespace NexusManager.Render;

/// <summary>
/// Reads a Corsair `.cuescreens` pack into our own screens.
///
/// The container is a plain ZIP holding `screen_settings`, `button_attachments`
/// and a `backgrounds/` folder. Both XML files are
/// <see href="https://uscilab.github.io/cereal/">cereal</see> archives, so the
/// layout is machine-generated from iCUE's C++ structs and is highly regular -
/// which is what makes positional parsing safe here rather than reckless.
///
/// ⛔ SECURITY. A pack is a file from someone else, and our configuration is
/// otherwise trusted the way a shell profile is. So this imports EXACTLY ONE
/// action type - a keyboard macro - and refuses everything else by name. It can
/// never produce a Launch action, because a pack that could hand you a shell
/// command would turn "open this screen pack" into "run this program".
/// A macro can still type into the focused window, which is why the importer
/// reports every macro it creates rather than adding them silently.
/// </summary>
public static class CueScreensImport
{
    /// <summary>What an import produced. Warnings are for the user to read, not
    /// for the log: an ignored button is a difference they will notice.</summary>
    public sealed record Result(
        List<ScreenSpec> Screens,
        List<string> Warnings,
        string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// cereal stamps a version on every struct. A bump means iCUE changed the
    /// layout, and parsing a newer one positionally would silently produce
    /// wrong buttons rather than failing - so unknown versions are refused.
    ///
    /// These are the values observed across all six official packs.
    /// </summary>
    private static readonly HashSet<int> KnownVersions = [200, 201, 202, 300, 301];

    /// <summary>The only polymorphic types this understands. ⛔ An unknown one is
    /// REFUSED rather than skipped quietly: the six packs available are all
    /// keyboard-macro screens, so the observed vocabulary is a SAMPLE and other
    /// action types certainly exist.</summary>
    private const string MacroActionType = "MacroAction";
    private const string KeyEventType = "KeyboardMacroActionEvent";
    private const string DelayEventType = "DelayMacroActionEvent";

    public static Result Load(string packPath, string backgroundDir)
    {
        var warnings = new List<string>();
        var screens = new List<ScreenSpec>();

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(packPath);
        }
        catch (Exception ex)
        {
            return new Result(screens, warnings, $"cannot open {packPath}: {ex.Message}");
        }

        using (zip)
        {
            var settings = zip.GetEntry("screen_settings");
            var attachments = zip.GetEntry("button_attachments");
            if (settings is null)
                return new Result(screens, warnings, "not a .cuescreens pack: no screen_settings entry");

            XDocument screenDoc, buttonDoc;
            try
            {
                screenDoc = ReadXml(settings);
                buttonDoc = attachments is null ? new XDocument(new XElement("cereal")) : ReadXml(attachments);
            }
            catch (Exception ex)
            {
                return new Result(screens, warnings, $"the pack's XML could not be read: {ex.Message}");
            }

            string? versionFault = CheckVersions(screenDoc, warnings) ?? CheckVersions(buttonDoc, warnings);
            if (versionFault is not null) return new Result(screens, warnings, versionFault);

            var macros = ReadMacros(buttonDoc, warnings);
            var backgrounds = ExtractBackgrounds(zip, backgroundDir, warnings);

            foreach (var data in screenDoc.Descendants("data").Where(d => d.Element("buttons") is not null))
                screens.Add(ReadScreen(data, macros, backgrounds, warnings));

            if (screens.Count == 0)
                return new Result(screens, warnings, "the pack contained no screens");
        }

        return new Result(screens, warnings, null);
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        // ⛔ Read through the archive, never by extracting first: pack entries
        // carry restrictive Unix modes and land unreadable on disk.
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string? CheckVersions(XDocument doc, List<string> warnings)
    {
        foreach (var v in doc.Descendants("cereal_class_version"))
        {
            if (!int.TryParse(v.Value, out int n))
                return $"unreadable cereal_class_version '{v.Value}'";
            if (!KnownVersions.Contains(n))
                return $"this pack uses cereal_class_version {n}, which this importer "
                     + "has never seen. Refusing rather than guessing at the layout.";
        }
        return null;
    }

    /// <summary>Reads button_attachments into macros keyed by button GUID.</summary>
    private static Dictionary<string, MacroSpec> ReadMacros(XDocument doc, List<string> warnings)
    {
        var macros = new Dictionary<string, MacroSpec>(StringComparer.OrdinalIgnoreCase);

        // ⛔ The SAME cereal id-referencing applies to the ACTION type, not just
        // to the events inside it. Only the first attachment names MacroAction;
        // the rest carry a bare polymorphic_id. Reading only the named ones
        // imported ONE button per pack out of up to thirty-two, and looked
        // successful because a macro did appear.
        //
        // It also means a survey that greps for polymorphic_name UNDERCOUNTS
        // the vocabulary - which is why the packs appeared to hold six macros
        // in total rather than six per pack.
        var actionTypeById = new Dictionary<long, string>();

        // ⛔ Type ids are scoped to the DOCUMENT, not to a macro. The first
        // macro declares KeyboardMacroActionEvent as type 2 and
        // DelayMacroActionEvent as type 3; every later macro in the same file
        // just references 2 and 3. Declaring this map inside the per-button
        // loop meant every macro after the first lost all of its events.
        var typeById = new Dictionary<long, string>();

        // ⛔ cereal names map entries value0, value1, value2 ... so matching
        // "value0" reads ONLY THE FIRST ENTRY. Every pack has up to six
        // buttons and this imported one of them, silently and with no warning
        // - the import looked like it worked because a macro did appear.
        // Match any value-N element that carries a key instead.
        foreach (var pair in doc.Descendants()
                                .Where(e => e.Name.LocalName.StartsWith("value", StringComparison.Ordinal)
                                         && e.Element("key") is not null))
        {
            string? guid = pair.Element("key")?.Value;
            if (string.IsNullOrWhiteSpace(guid)) continue;

            var content = pair.Descendants("content").FirstOrDefault();
            var payload = content?.Element("data");
            string? type = payload?.Element("polymorphic_name")?.Value;
            if (long.TryParse(payload?.Element("polymorphic_id")?.Value, out long apid))
            {
                if (type is not null) actionTypeById[apid & 0x7FFFFFFF] = type;
                else actionTypeById.TryGetValue(apid, out type);
            }

            if (type is null) continue;
            if (type != MacroActionType)
            {
                warnings.Add($"button {Short(guid)} carries a '{type}', which this importer "
                           + "does not understand - the button is imported without an action.");
                continue;
            }

            var macroData = payload!.Element("ptr_wrapper")?.Element("data");
            var baseEl = macroData?.Element("base");
            if (baseEl is null) continue;

            var spec = new MacroSpec();

            // ⛔ repeatMode is a SAMPLE: only NoRepeat appears in the packs we have,
            // so anything else is refused for that button rather than assumed to
            // mean "once".
            string mode = baseEl.Element("repeatOptions")?.Element("repeatMode")?.Value ?? "NoRepeat";
            if (!string.Equals(mode, "NoRepeat", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"button {Short(guid)} repeats with mode '{mode}', which this "
                           + "importer has not seen - imported as a single pass.");
            }

            foreach (var ev in baseEl.Parent!.Descendants("events").Elements())
            {
                string? name = ev.Element("polymorphic_name")?.Value;
                if (!long.TryParse(ev.Element("polymorphic_id")?.Value, out long pid)) continue;

                if (name is not null) typeById[pid & 0x7FFFFFFF] = name;
                else if (!typeById.TryGetValue(pid, out name))
                {
                    warnings.Add($"button {Short(guid)} refers to polymorphic type {pid}, which was "
                               + "never declared in this pack - that step is dropped.");
                    continue;
                }

                var evData = ev.Element("ptr_wrapper")?.Element("data");
                if (evData is null) continue;

                switch (name)
                {
                    case KeyEventType:
                    {
                        bool down = !string.Equals(evData.Element("sub")?.Value, "KeyRelease",
                                                   StringComparison.OrdinalIgnoreCase);
                        foreach (var k in evData.Element("keys")?.Elements() ?? [])
                        {
                            string mapped = MapKey(k.Value);
                            if (UinputKeyboard.Resolve(mapped) is null)
                            {
                                warnings.Add($"button {Short(guid)} uses key '{k.Value}', which has no "
                                           + "equivalent here - that step is dropped.");
                                continue;
                            }
                            spec.Steps.Add(new MacroStep
                            {
                                Kind = down ? MacroStepKind.KeyDown : MacroStepKind.KeyUp,
                                Key = mapped,
                            });
                        }
                        break;
                    }

                    case DelayEventType:
                        if (int.TryParse(evData.Element("delay")?.Value, out int ms))
                            spec.Steps.Add(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = ms });
                        break;

                    default:
                        warnings.Add($"button {Short(guid)} contains a '{name}' step, which this "
                                   + "importer does not understand - that step is dropped.");
                        break;
                }
            }

            if (spec.Steps.Count > 0) macros[guid] = spec;
        }

        return macros;
    }

    /// <summary>
    /// Corsair name to ours. Single characters and digits already agree once
    /// lowercased; the named keys are the ones worth a table.
    /// </summary>
    private static string MapKey(string corsair) => corsair.Trim() switch
    {
        "LeftCtrl" => "leftctrl",
        "RightCtrl" => "rightctrl",
        "LeftShift" => "leftshift",
        "RightShift" => "rightshift",
        "LeftAlt" => "leftalt",
        "RightAlt" => "rightalt",
        "LeftGui" or "LeftWin" => "meta",
        "Space" => "space",
        "Enter" or "Return" => "enter",
        "Escape" => "esc",
        "Tab" => "tab",
        "Backspace" => "backspace",
        "Delete" => "delete",
        var other => other.ToLowerInvariant(),
    };

    /// <summary>
    /// Writes the pack's backgrounds beside the user's config and returns a
    /// basename to path map.
    ///
    /// ⛔ The stored backgroundImagePath is the EXPORTING MACHINE'S absolute
    /// Windows path and contains that person's username. It is matched by
    /// basename and otherwise ignored - never used to open anything.
    /// </summary>
    private static Dictionary<string, string> ExtractBackgrounds(
        ZipArchive zip, string dir, List<string> warnings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("backgrounds/", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;

            try
            {
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, Path.GetFileName(entry.Name));
                using (var src = entry.Open())
                using (var dst = File.Create(target))
                    src.CopyTo(dst);
                map[Path.GetFileName(entry.Name)] = target;
            }
            catch (Exception ex)
            {
                warnings.Add($"background '{entry.Name}' could not be written: {ex.Message}");
            }
        }
        return map;
    }

    private static ScreenSpec ReadScreen(
        XElement data, Dictionary<string, MacroSpec> macros,
        Dictionary<string, string> backgrounds, List<string> warnings)
    {
        var screen = new ScreenSpec
        {
            Name = data.Element("name")?.Value is { Length: > 0 } n ? n : "Imported",
        };

        string? bg = data.Element("backgroundImagePath")?.Value;
        if (!string.IsNullOrWhiteSpace(bg))
        {
            // Basename only - see ExtractBackgrounds.
            string basename = bg.Replace('\\', '/').Split('/').Last();
            if (backgrounds.TryGetValue(basename, out string? local))
                screen.Background = new BackgroundSpec { Image = local, Fit = BackgroundFit.Cover };
            else
                warnings.Add($"screen '{screen.Name}' wants background '{basename}', "
                           + "which is not in the pack.");
        }

        string? colour = data.Element("backgroundColor")?.Value;
        // Theme.Background is non-nullable: only assign when the pack gave a
        // colour we recognise, otherwise keep our own default.
        if (FromArgb(colour) is { } parsed)
            screen.Theme.Background = parsed;

        foreach (var entry in data.Element("buttons")?.Elements() ?? [])
        {
            var b = entry.Element("value")?.Element("ptr_wrapper")?.Element("data");
            if (b is null) continue;

            var spec = new ButtonSpec
            {
                Label = b.Element("name")?.Value ?? "",
                Color = FromArgb(b.Element("textColor")?.Value),
                Background = FromArgb(b.Element("backgroundColor")?.Value),
            };

            string? id = b.Element("id")?.Value;
            if (id is not null && macros.TryGetValue(id, out var macro))
            {
                spec.Action = new SystemAction { Kind = ActionKind.Macro, Macro = macro };
                warnings.Add($"'{screen.Name}' button '{Describe(spec.Label)}' imported a macro "
                           + $"of {macro.Steps.Count} step(s) - review it before using it.");
            }

            screen.Buttons.Add(spec);
        }

        return screen;
    }

    private static string Describe(string label) => string.IsNullOrWhiteSpace(label) ? "(no label)" : label;
    private static string Short(string guid) => guid.Length > 10 ? guid[1..9] : guid;

    /// <summary>#AARRGGBB to our #AARRGGBB. Same order, so this only validates.</summary>
    private static string? FromArgb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        return v.StartsWith('#') && (v.Length == 7 || v.Length == 9) ? v : null;
    }
}
