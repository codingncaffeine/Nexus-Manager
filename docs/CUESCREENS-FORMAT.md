# .cuescreens — format specification

Reverse-engineered 2026-09-07 from six official Corsair packs (dated 2020-07-10
to 2020-07-13) held in `icue stuff/`. No VM or USB capture was needed for this;
the container is self-describing.

## Container

A plain ZIP (PK 03 04, deflate, made by v3.0 UNIX). Three entries:

    backgrounds/<name>.png    one or more, ALWAYS 640x48 8-bit RGBA
    screen_settings           XML
    button_attachments        XML

CAUTION: entries carry restrictive Unix modes and extract unreadable. `chmod
u+rwX` after extraction, or set the mode explicitly when reading in code.

One pack may hold several screens. `All_Screens.cuescreens` holds four
("Far Cry New Dawn", "Overwatch", "World Of Warcraft", "Division 2") over five
backgrounds.

## Both XML files are cereal archives

They are output from [cereal](https://uscilab.github.io/cereal/), the C++
serialization library — `<cereal>` root, `<polymorphic_id>`, `<ptr_wrapper>`,
`<cereal_class_version>`, `size="dynamic"`, and maps serialized as
`<value0>..<valueN>` each holding `<key>`/`<value>`.

Consequence: the layout is machine-generated from iCUE's own C++ structs, so it
is highly regular and safe to parse positionally. It also means version drift
shows up as `cereal_class_version` bumps — read that field and refuse unknown
majors rather than mis-parsing.

Versions seen: screen 300, button 301, MacroAction 201, macro base 202,
repeatOptions 300, macro events 200.

## screen_settings

Per screen:

    id                    GUID
    crossDeviceId         optional, <nullopt>true</nullopt> when unset
    crossDeviceScreenId   GUID, zero GUID when unset
    parentId              GUID, zero GUID at top level -> SCREENS FORM A TREE
    enabled               bool
    name                  display name
    backgroundImagePath   ABSOLUTE WINDOWS PATH, e.g.
                          C:\Users\<user>\AppData\Roaming\Corsair\CUE\touchscreens\backgrounds\RPG (1).png
    backgroundColor       #AARRGGBB
    buttons               map, keys TouchScreen_Button1 .. TouchScreen_Button6

Per button:

    id                    GUID, joins to button_attachments
    nestedScreenId        GUID -> drill-down target
    nestedScreenType      enum, only 0 observed
    name                  label text
    fontFamily            e.g. "Open Sans"
    fontSize              enum, only "Medium" observed
    textColor             #AARRGGBB
    backgroundImagePath   per-button image
    backgroundColor       #AARRGGBB
    iconPath / iconColor / iconSize
    editable / removable / resizable    bools, editor hints

## button_attachments

Map keyed by GUID (matches a button `id`) ->

    type          enum, only 0 observed
    content.index integer
    content.data  polymorphic

Observed polymorphic types:

    MacroAction
      base.name, base.id
      repeatOptions: repeatCount, repeatMode (NoRepeat), delay,
                     delayMode (Constant), randomDelayFrom, randomDelayTo
      executionHints
    KeyboardMacroActionEvent
      keys   list of key names, e.g. <value0>L</value0>
      sub    KeyPress
    DelayMacroActionEvent

## Known gaps

These six packs are all keyboard-macro screens, so they exercise only part of
the vocabulary. Every enum above shows exactly one value, which means the value
space is UNKNOWN, not single-valued. iCUE also offers launch, media and device
actions that appear nowhere here.

Resolve by driving iCUE in the VM and exporting screens that use each action
type and each font size — that enumerates the enums cheaply, without USB capture.

## Import rules for us

1. Ignore `backgroundImagePath` as a path. Match by BASENAME against the
   `backgrounds/` entries in the same ZIP. The stored path is the exporting
   machine's, and it leaks their Windows username
2. Honour `parentId` / `nestedScreenId` — screens are a TREE. See DESIGN.md D10
3. Backgrounds are already 640x48 RGBA, so they need no scaling; they still need
   BGRA reorder and 18-bit dithering (D9) before upload
4. Refuse unknown `cereal_class_version` majors loudly
5. Corsair's and the games' artwork stays out of the repo (D6). Import from the
   user's own files at runtime, ship nothing
