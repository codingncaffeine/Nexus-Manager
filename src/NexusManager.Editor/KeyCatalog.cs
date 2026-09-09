using NexusManager.Actions;

namespace NexusManager.Editor;

/// <summary>
/// The key vocabulary a picker can show: <see cref="UinputKeyboard.KeyCodes"/>,
/// deduped by CODE and put in an order a keyboard reads in.
///
/// ⛔ Dedupe by code, never by name. The table is deliberately aliased -
/// esc/escape are both 1, ctrl/leftctrl 29, meta/super/win 125, enter/return
/// 28 - so listing names gives 103 rows with 9 that do the same thing, and a
/// user picking between two identical entries has been given a decision that
/// does not exist. The aliases stay SEARCHABLE: typing "escape" finds "esc".
///
/// ⛔ The order is written out rather than sorted. Alphabetical separates f1
/// from f10 and scatters the modifiers; KeyCodes itself is in physical-row
/// order for the same reason. That does mean a name added to KeyCodes and not
/// added here would be invisible in the picker while still working in a config
/// - so <see cref="Missing"/> exists and --probe-macro asserts it is empty.
/// </summary>
public static class KeyCatalog
{
    public sealed record Entry(string Name, ushort Code, string Group, IReadOnlyList<string> Aliases)
    {
        /// <summary>Matches a search box: the shown name, or any alias of it.</summary>
        public bool Matches(string q) =>
            Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly (string Group, string[] Names)[] Order =
    [
        ("Modifiers",  ["ctrl", "shift", "alt", "meta", "altgr",
                        "rightctrl", "rightshift", "rightmeta", "capslock"]),
        ("Letters",    ["q","w","e","r","t","y","u","i","o","p",
                        "a","s","d","f","g","h","j","k","l",
                        "z","x","c","v","b","n","m"]),
        ("Digits",     ["1","2","3","4","5","6","7","8","9","0"]),
        ("Function",   ["f1","f2","f3","f4","f5","f6","f7","f8","f9","f10","f11","f12"]),
        ("Navigation", ["esc", "tab", "enter", "backspace", "space",
                        "insert", "delete", "home", "end", "pageup", "pagedown",
                        "up", "down", "left", "right",
                        "printscreen", "menu", "numlock", "scrolllock"]),
        ("Punctuation",["minus", "equal", "[", "]", ";", "'", "`", "\\", ",", ".", "/"]),
        ("Media",      ["mute", "volumedown", "volumeup",
                        "playpause", "nextsong", "previoussong", "stop"]),
    ];

    /// <summary>Every code once, in picker order.</summary>
    public static IReadOnlyList<Entry> All { get; } = Build();

    /// <summary>
    /// Codes that <see cref="UinputKeyboard.KeyCodes"/> knows and this catalogue
    /// does not list. Must be empty: a key you can type into a config but
    /// cannot pick from the list is a hole nobody would find by using the app.
    /// </summary>
    public static IReadOnlyList<string> Missing { get; } = FindMissing();

    /// <summary>The name to SHOW for a code, so a step imported with an alias
    /// ("escape") displays as the catalogue spells it ("esc").</summary>
    public static string Display(string key)
    {
        if (UinputKeyboard.Resolve(key) is not { } code) return key;
        return All.FirstOrDefault(e => e.Code == code)?.Name ?? key;
    }

    private static List<Entry> Build()
    {
        // code -> every spelling of it, so the aliases can be searched
        var byCode = new Dictionary<ushort, List<string>>();
        foreach (var (name, code) in UinputKeyboard.KeyCodes)
        {
            if (!byCode.TryGetValue(code, out var names)) byCode[code] = names = [];
            names.Add(name);
        }

        var seen = new HashSet<ushort>();
        var list = new List<Entry>();
        foreach (var (group, names) in Order)
            foreach (string name in names)
            {
                if (UinputKeyboard.Resolve(name) is not { } code) continue;
                if (!seen.Add(code)) continue;
                var aliases = byCode.TryGetValue(code, out var all)
                    ? all.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList()
                    : [];
                list.Add(new Entry(name, code, group, aliases));
            }
        return list;
    }

    private static List<string> FindMissing()
    {
        var listed = All.Select(e => e.Code).ToHashSet();
        return UinputKeyboard.KeyCodes
            .Where(kv => !listed.Contains(kv.Value))
            .Select(kv => $"{kv.Key}={kv.Value}")
            .Distinct()
            .ToList();
    }
}
