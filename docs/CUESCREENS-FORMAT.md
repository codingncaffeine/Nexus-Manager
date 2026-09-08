# .cuescreens — format specification

Reverse-engineered 2026-09-07 from six official Corsair packs (dated 2020-07-10
to 2020-07-13) held in `icue stuff/`. No VM or USB capture was needed for this;
the container is self-describing.

## Container

A plain ZIP (PK 03 04, deflate, made by v3.0 UNIX). Three entries:

    backgrounds/<name>.<ext>  one or more, ALWAYS 640x48, but PNG *or JPEG*
                              (RGBA for PNG, RGB with no alpha for JPEG)
    screen_settings           XML
    button_attachments        XML

CAUTION: entries carry restrictive Unix modes and extract unreadable. `chmod
u+rwX` after extraction, or set the mode explicitly when reading in code.

One pack may hold several screens. The combined pack holds four game-specific
screens over five backgrounds.

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
                          C:\Users\<user>\AppData\Roaming\Corsair\CUE\touchscreens\backgrounds\<name>.png
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
3. Backgrounds are already 640x48, so they need no scaling - but they are PNG
   OR JPEG, so do not assume an alpha channel. They still need
   BGRA reorder and 18-bit dithering (D9) before upload
4. Refuse unknown `cereal_class_version` majors loudly
5. Corsair's and the games' artwork stays out of the repo (D6). Import from the
   user's own files at runtime, ship nothing

## Animation — where it lives, and where it does NOT

**`.cuescreens` has no animation vocabulary at all.** Every XML element name
across all six official packs was enumerated:

    actionLighting actionSoundPath attachedActions backgroundColor
    backgroundImagePath base buttons cereal cereal_class_version content
    crossDeviceId crossDeviceScreenId data delay delayMode editable enabled
    events execHint executionHints fontFamily fontSize highPriority iconColor
    iconPath iconSize id index key keys locked_ptr name nestedScreenId
    nestedScreenType nullopt parentId polymorphic_id polymorphic_name
    randomDelayFrom randomDelayTo removable repeatCount repeatMode repeatOptions
    resizable restartOnSecondExec retainOriginalKeyOutput size sub
    terminateOnSecondExec terminationMethod textColor type useRandom value*

There is no `frame`, `duration`, `fps`, `loop`, `gif` or `animation` field
anywhere. A grep for those words across every pack returns nothing.

**The animation is the FILE, not the schema.** `backgroundImagePath` names a
file, and iCUE simply plays whatever that file is. From Corsair's own NEXUS FAQ:

- accepted background types are **.bmp, .jpg, .png and .gif**
- **an animated GIF plays at 24 fps**
- there is **no limit on GIF size**
- 640x48 is *recommended*, not required — other sizes are accepted and fitted

That 24 fps is where this project's frame-rate target came from.

So supporting custom animation needs **no format extension**: it needs a decoder
that honours per-frame delays, and a clock. Implemented in
`src/NexusManager.Render/AnimatedImage.cs` on SkiaSharp's `SKCodec`, which
covers GIF and animated WebP.

### Corrections to earlier notes in this document

1. ⛔ **Backgrounds are NOT always PNG/RGBA.** Two of the six packs ship
   **JPEG** (baseline, 3 components,
   no alpha). Both are 640x48. An importer that assumes PNG with an alpha
   channel fails on a third of Corsair's own art.
2. ⛔ **`Install iCUE.exe` is a 3.3 MB DOWNLOADER STUB, not iCUE.** It is a
   PE32+ GUI binary whose only interesting string is the module feed
   `https://www3.corsair.com/software/CUE_V5/public/modules`, plus an
   "Installer expired" message. There are no screens, no animations and no
   assets inside it. Extracting animation behaviour from it is not possible.
3. `iCUE-NEXUS-Downloadable-Game-Screens.zip` contains exactly the same six
   `.cuescreens` packs already held loose — no additional material.

### What our own format adds beyond Corsair

`BackgroundSpec` (`src/NexusManager.Render/Background.cs`) keeps
file-compatibility — point `Image` at a `.gif` and it behaves as iCUE does —
and adds what a 640x48 strip actually needs:

| Field | Why it is not in Corsair's format |
|---|---|
| `Fit` | 640x48 is a 13:1 letterbox. Corsair assume you authored at exactly that size; anything else has to be cropped, fitted, tiled or **scrolled**. |
| `ScrollLeft` / `ScrollRight` | Motion for a *still* image. An ordinary photo is unreadable at 13:1 unless it moves. |
| `Speed` | Play a GIF slower or faster than its authored timing. |
| `Opacity` | A background sits UNDER live readings. At full strength a photograph makes the numbers illegible. |
| `Loop` | Hold the last frame instead of repeating. |

### Decoder behaviour worth knowing

- **GIF frames are differential.** Frame N may carry only the changed pixels,
  with `RequiredFrame` naming what it composites onto. Decoding each frame into
  a fresh buffer yields fragments on a transparent field; the decode reuses one
  working bitmap and passes `priorFrame`.
- **A delay of 0 or 10 ms means 100 ms.** Every renderer since Netscape has
  treated it that way. Honouring the raw value turns such a GIF into a strobe.
- **Playback is wall-clock driven**, not tick-counted, so a 10 fps GIF does not
  speed up because the panel is rendering at 24.
- Frames are stored at panel resolution, so memory is independent of source
  size: 120 KB per frame, capped at 600 frames.

Verified with `nexus-manager image <file>`, which reports frame count, per-pass
duration, average fps, stored size and memory, and can dump composited frames:

    nexus-manager image cool.gif --fit cover --dump /tmp/frames --frames 24

### Formats, verified

| Format | Still | Animated | Verified |
|---|---|---|---|
| PNG | yes | — | Corsair's own packs decode |
| JPEG | yes | — | Corsair's own packs decode |
| BMP | yes | — | via SKCodec |
| **GIF** | yes | **yes** | 12 frames / 1200 ms / 10.0 fps, exact against ffprobe |
| **WebP** | yes | **yes** | 18 frames / 1499 ms / 12.0 fps, exact against ffprobe |

WebP matters in practice: animated WebP is far easier to find and to produce at
an arbitrary size than GIF, and it compresses better. iCUE accepts neither WebP
nor any animation metadata — this is ours.

### Choosing what lands on the strip

640x48 is a **13.3:1** letterbox, so any image that was not authored for it is
cropped hard, and *which part* survives is the whole decision. `BackgroundSpec`
carries `Zoom` plus a normalised `FocusX`/`FocusY`:

- `Zoom` 1 shows as much as the strip can hold; higher shows less, larger.
- `FocusX` / `FocusY` run 0 (left/top edge) to 1 (right/bottom), as a fraction of
  the SOURCE — so the choice survives swapping the image for a different size,
  and means the same thing at any zoom.

Verified on a 600x600 source of three horizontal bands: focus 0.0 renders the red
band, 0.5 the green, 1.0 the blue, and zoom 4 at focus 0.5 stays inside green.

The editor exposes this as a drag: the whole image is drawn dimmed with a bright
box over the part that reaches the panel. Drag the box, scroll to zoom. Showing
the whole image and moving a window over it beats a fixed window with the image
dragged behind it — at 13:1 the window is a sliver, so panning behind it is
panning blind.

    nexus-manager image <file> --fit cover --zoom 2 --focus 0.5 0.25 --dump /tmp/f
