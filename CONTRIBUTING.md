# Contributing

Thanks for taking a look. This is a small mod and PRs are welcome.

## The one rule that will bite you

**The code must be C# 5.**

Duck Game compiles mod source in-process with `Microsoft.CSharp.CSharpCodeProvider`,
which is the in-box .NET Framework compiler. Neither the vanilla nor the Rebuilt install
ships a `<system.codedom>` redirect or a `roslyn/` folder, so there is no newer compiler
to fall back on.

That means **none** of this is available:

| Not allowed | Use instead |
|---|---|
| `$"text {x}"` | `"text " + x` |
| `a?.B` | `a != null ? a.B : null` |
| `int X => 1;` | `int X { get { return 1; } }` |
| `nameof(X)` | `"X"` |
| `if (o is string s)` | `string s = o as string; if (s != null)` |
| `(int, int)` tuples | a small struct |
| `public int X { get; } = 5;` | assign in the constructor |
| `int.TryParse(s, out var v)` | `int v; int.TryParse(s, out v)` |
| `using static X;` | qualify the call |

Run the checker and it will tell you — it passes `/langversion:5` explicitly so these
fail loudly here rather than mysteriously at game launch.

## Setup

```powershell
git clone <your fork>
cd "Duck Game Midi Controller"

# link the repo into Duck Game's Mods folder (no admin rights needed)
powershell -ExecutionPolicy Bypass -File tools\install-dev.ps1
```

Then edit, relaunch Duck Game, test. There is no build step — the game compiles the
source at startup.

## Before every commit

```powershell
powershell -ExecutionPolicy Bypass -File tools\check-compile.ps1
```

This drives the same `csc.exe` the game does, against **both** the vanilla and Rebuilt
installs it can find. Both must pass: the mod ships as source, so it gets compiled against
whichever build each subscriber is running, and the two sit on different graphics stacks
(vanilla is XNA, Rebuilt is FNA).

## Working on a machine with only one install

`check-compile.ps1` skips a target it cannot find and says so. CI runs a syntax-only parse
because it has neither install, so a local run against both is the real gate.

## Debugging in-game

- `midi status` — the first thing to check; reports NAudio, device, attachment, held item.
- `midi test scale` / `midi test drums` / `midi test quack` — play synthetic MIDI through
  the whole pipeline with no hardware attached. If these work and your controller doesn't,
  the problem is upstream of the mod.
- **Settings → MIDI Monitor** — live raw message readout.
- If the mod does not appear in the Mods menu, read `DuckGameMidiController_build.log` in
  the repo root. Delete `DuckGameMidiController_compiled.hash` to force a rebuild if the
  game seems to be caching a stale compile.

## Things worth knowing about the code

- `MidiInputProfile` wraps the player's real profile rather than replacing it, and installs
  a `VirtualInput` on *itself* so the non-virtual analog getters still work. Read the
  comments there before changing it — several non-obvious constraints are load-bearing.
- `DuckHook` sets `Duck._virtualInput` by reflection because the public property for it
  only exists in Rebuilt.
- `NAudioReflection` binds NAudio late, on purpose: it may not be in the AppDomain when
  mods compile, and whether it is depends on which *other* mods the user has installed.
- `InstrumentRouter` has a per-instrument state machine because the game retriggers each
  family differently. The one-frame gap for sax/trombone is required, not an oversight.

## Publishing to the Workshop

Build the clean folder first — never publish the dev junction, which would upload
`.git/`, `tools/` and `docs/` along with everything else:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -InstallToMods
```

Launch the game, then — **with it still open, immediately before pressing UPLOAD** — clear
the compiled artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File tools\pre-upload-clean.ps1
```

Don't relaunch or bounce through the main menu afterwards; the game recompiles on load and
the artifacts come straight back.

This matters more than it looks. Duck Game's uploader *tries* to strip
`_compiled.dll`/`_compiled.hash`, but the paths it builds are wrong — it copies to
`folderPath + "/" + name` and then deletes from `folderPath + name`, with no separator —
so nothing is ever stripped. The Rebuilt variants were never on that list to begin with.

If a stale pair ships, `ModLoader.AttemptCompile` sees a `_compiled.hash` whose CRC32
matches the (identical) `.cs` files, skips recompilation entirely, and loads the shipped
DLL. That DLL was built against *your* game — so a subscriber on the other graphics stack
(vanilla is XNA, Rebuilt is FNA) loads an assembly linked against assemblies they do not
have. Shipping source exists precisely to avoid that, and a leaked DLL silently undoes it.

Publish **private** first, subscribe from a clean profile, confirm it compiles on the
subscriber side, and only then make it public.

## Style

Match what is there: explicit types over `var` where the type is not obvious, comments that
explain *why* rather than what, and no abbreviations in public names.
