using System.IO.Compression;
using NexusManager.Actions;
using NexusManager.Render;

namespace NexusManager.Cli;

/// <summary>
/// Guards the one property of cereal that produced three separate faults in this
/// importer, each of which looked like success.
///
/// ⛔ A polymorphic type is NAMED ONLY ON ITS FIRST USE in a document; every
/// later instance carries a bare numeric id referring back to it, and those ids
/// are scoped to the DOCUMENT rather than to the macro. A parser that reads only
/// the named ones still produces a macro, so the failure never announces itself
/// - it just imports two of twenty-five buttons, or three of eighteen steps.
///
/// The pack built here is synthetic on purpose. The real packs are Corsair's and
/// cannot be committed, and a fixture that ships with the repository is the only
/// kind that can be relied on in CI.
/// </summary>
public static class ImportSelfTest
{
    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-import-selftest-" + Environment.ProcessId);
        string pack = Path.Combine(dir, "synthetic.cuescreens");
        Directory.CreateDirectory(dir);
        int fails = 0;

        try
        {
            Build(pack);
            var result = CueScreensImport.Load(pack, Path.Combine(dir, "backgrounds"));

            if (!result.Ok)
            {
                Console.WriteLine($"  FAIL  import failed: {result.Error}");
                return 1;
            }

            // Two buttons, and the SECOND declares no type names at all.
            var buttons = result.Screens.SelectMany(s => s.Buttons)
                                        .Where(b => b.Action.Kind == ActionKind.Macro).ToList();
            bool okCount = buttons.Count == 2;
            Report(okCount, $"macros imported: {buttons.Count} of 2"
                          + (okCount ? "" : "  <- the second names no types and was lost"), ref fails);

            // Three steps each: down, delay, up. The second and third steps of the
            // FIRST macro, and every step of the second, are id references.
            foreach (var b in buttons)
            {
                int steps = b.Action.Macro?.Steps.Count ?? 0;
                Report(steps == 3, $"steps in '{b.Label}': {steps} of 3", ref fails);
            }

            if (buttons.Count > 0 && buttons[0].Action.Macro is { } m && m.Steps.Count == 3)
            {
                bool shape = m.Steps[0].Kind == MacroStepKind.KeyDown
                          && m.Steps[1].Kind == MacroStepKind.Delay
                          && m.Steps[2].Kind == MacroStepKind.KeyUp;
                Report(shape, $"step order: {string.Join(", ", m.Steps.Select(s => s.Kind))}", ref fails);
            }

            // ⛔ An unknown action type must never become an action.
            Build(pack, actionType: "LaunchAction");
            var evil = CueScreensImport.Load(pack, Path.Combine(dir, "backgrounds"));
            bool noLaunch = evil.Screens.SelectMany(s => s.Buttons)
                                        .All(b => b.Action.Kind != ActionKind.Launch);
            Report(noLaunch, "an unknown action type does not become a Launch", ref fails);

            // ⛔ And an unknown layout version must be refused, not guessed at.
            Build(pack, version: 999);
            var bumped = CueScreensImport.Load(pack, Path.Combine(dir, "backgrounds"));
            Report(!bumped.Ok, "an unknown cereal_class_version is refused", ref fails);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "  import self-test PASSED" : $"  import self-test FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    private static void Report(bool ok, string what, ref int fails)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}");
        if (!ok) fails++;
    }

    /// <summary>
    /// Writes a pack whose SECOND button names no polymorphic types at all, and
    /// whose first macro names each type once. That is exactly the shape the real
    /// packs have, and the shape that broke three versions of this parser.
    /// </summary>
    private static void Build(string path, string actionType = "MacroAction", int version = 300)
    {
        const string Guid1 = "{11111111-1111-1111-1111-111111111111}";
        const string Guid2 = "{22222222-2222-2222-2222-222222222222}";
        // Held in a variable: inside an interpolated raw string a literal brace
        // pair reads as an interpolation, not as an escape.
        const string ScreenId = "{33333333-3333-3333-3333-333333333333}";

        string Events(bool declare) => $"""
                    <events size="dynamic">
                      <value0>
                        <polymorphic_id>{(declare ? 2147483650 : 2)}</polymorphic_id>
                        {(declare ? "<polymorphic_name>KeyboardMacroActionEvent</polymorphic_name>" : "")}
                        <ptr_wrapper><id>1</id><data>
                          <cereal_class_version>200</cereal_class_version>
                          <keys size="dynamic"><value0>L</value0></keys>
                          <sub>KeyPress</sub>
                        </data></ptr_wrapper>
                      </value0>
                      <value1>
                        <polymorphic_id>{(declare ? 2147483651 : 3)}</polymorphic_id>
                        {(declare ? "<polymorphic_name>DelayMacroActionEvent</polymorphic_name>" : "")}
                        <ptr_wrapper><id>2</id><data>
                          <cereal_class_version>200</cereal_class_version>
                          <delay>40</delay>
                        </data></ptr_wrapper>
                      </value1>
                      <value2>
                        <polymorphic_id>2</polymorphic_id>
                        <ptr_wrapper><id>3</id><data>
                          <cereal_class_version>200</cereal_class_version>
                          <keys size="dynamic"><value0>L</value0></keys>
                          <sub>KeyRelease</sub>
                        </data></ptr_wrapper>
                      </value2>
                    </events>
            """;

        string Attachment(string key, bool declare) => $"""
              <value{(declare ? 0 : 1)}>
                <key>{key}</key>
                <value>
                  <polymorphic_id>1073741824</polymorphic_id>
                  <ptr_wrapper><id>9</id><data>
                    <cereal_class_version>300</cereal_class_version>
                    <type>0</type>
                    <content>
                      <index>0</index>
                      <data>
                        <polymorphic_id>{(declare ? 2147483649 : 1)}</polymorphic_id>
                        {(declare ? $"<polymorphic_name>{actionType}</polymorphic_name>" : "")}
                        <ptr_wrapper><id>10</id><data>
                          <cereal_class_version>201</cereal_class_version>
                          <base>
                            <cereal_class_version>202</cereal_class_version>
                            <name>Macro</name>
                            <repeatOptions>
                              <cereal_class_version>300</cereal_class_version>
                              <repeatCount>1</repeatCount>
                              <repeatMode>NoRepeat</repeatMode>
                              <delay>0</delay>
                            </repeatOptions>
                          </base>
                {Events(declare)}
                        </data></ptr_wrapper>
                      </data>
                    </content>
                  </data></ptr_wrapper>
                </value>
              </value{(declare ? 0 : 1)}>
            """;

        string buttons = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <cereal><value0 size="dynamic">
            {Attachment(Guid1, declare: true)}
            {Attachment(Guid2, declare: false)}
            </value0></cereal>
            """;

        string Button(string id, string name) => $"""
                  <value{(id == Guid1 ? 0 : 1)}>
                    <key>TouchScreen_Button{(id == Guid1 ? 1 : 2)}</key>
                    <value><polymorphic_id>1073741824</polymorphic_id>
                      <ptr_wrapper><id>5</id><data>
                        <cereal_class_version>301</cereal_class_version>
                        <id>{id}</id>
                        <name>{name}</name>
                        <textColor>#ffffffff</textColor>
                      </data></ptr_wrapper>
                    </value>
                  </value{(id == Guid1 ? 0 : 1)}>
            """;

        string screens = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <cereal><value0 size="dynamic"><value0>
              <polymorphic_id>1073741824</polymorphic_id>
              <ptr_wrapper><id>1</id><data>
                <cereal_class_version>{version}</cereal_class_version>
                <id>{ScreenId}</id>
                <name>Synthetic</name>
                <backgroundColor>#ff000000</backgroundColor>
                <buttons size="dynamic">
            {Button(Guid1, "First")}
            {Button(Guid2, "Second")}
                </buttons>
              </data></ptr_wrapper>
            </value0></value0></cereal>
            """;

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "screen_settings", screens);
        Write(zip, "button_attachments", buttons);

        static void Write(ZipArchive zip, string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
    }
}
