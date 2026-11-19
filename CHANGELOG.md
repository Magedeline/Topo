# Changelog

## 1.4.0

**Edit ▸ Find in map (Alt+F)**, inspired by OldmanG's [Lönn Searcher](https://gamebanana.com/tools/25016)
- Find placed entities, triggers, decals and rooms by type, texture, room name or any attribute, and jump to the result (Enter or double-click; F3 / Shift+F3 step through results).
- *Match* can be limited to names, attribute values, **flags** (flag attributes split into single flags, `!` handled) or **dialog keys**. Leave the box empty with Flags or Dialog keys to list them all. *Edit ▸ List flags in map…* opens it on Flags.
- Whole word, match case and current-room-only options.

**View ▸ Camera replay (Ctrl+Shift+C)**, for recordings from rifs's [Lönn Camera Simulator](https://gamebanana.com/tools/25020)
- Install that mod and press F8 in a level to record. Topo reads the recordings from `Celeste/ModFiles/CameraSim/Runs` and plays them over the map: player trail, hitbox, the game's camera, and active triggers. Pick a recording or follow the latest as it is written.
- Topo also simulates the camera for the map **as it is now**, using Celeste's camera rules: room bounds and camera offset, `cameraOffsetTrigger`, `cameraTargetTrigger`, `cameraAdvanceTargetTrigger`, `everest/smoothCameraOffsetTrigger`, killbox lower bound and the red-dash/feather/summit leads. Edit a trigger and the simulated camera updates at once, with no need to go back into the game. The window shows how far it is from the recorded camera.
- Force flags on or off to try flag-controlled cameras (`deleteFlag`). MaxHelpingHand camera triggers are listed as not simulated.
- Both tools are Topo's own code. Nothing from either plugin is bundled, and Topo only reads the recording files.

**AI edition:** new agent tools `find_in_map`, `list_flags` (who sets and who reads each flag, and flags read but never set), `camera_recordings` and `camera_replay` (replays a recording against the edited map and reports per-room differences from the recorded camera).

## 1.3.0

**PCG ▸ Restyle rooms (Ctrl+Shift+G, also on the canvas right-click menu)**
- Reworks the tiles of rooms you already have instead of generating new ones. Pick rooms (filter by name, e.g. `c-`), and the seleCte model resamples only the tiles near walls, floors and ceilings. Room size, position, border, openings, entities and the spawn stay as they are; new solids continue the tileset next to them.
- Fillers (the grey hatched blocks) and rooms you didn't pick are never touched.
- Thin pillars, ledges and stalactites (3 tiles or less) are never worn down. A wall is never cut in two, nothing new floats on its own, and no sealed air bubbles or one-tile slots are left.
- Each room is checked with the movement model against how it was. If an exit or item gets harder, Topo tries another seed, and after a few tries leaves the room as it was.
- Optional tileset swap (all solids, or only one tileset) and background regenerate/clear. Apply is one undo step.
- Works the same in both editions. The standard edition has only the dialog and contains no AI code.
- **AI edition:**
  - **Hand to AI agent…** in the dialog: the agent restyles the checked rooms with your settings, compares before and after, retunes rooms that look off, finishes them by hand and re-checks every exit. The prompt lands in the AI tab for you to review before sending.
  - Right-click a room → *Restyle room … with the AI agent…*, and *Design ▾ → Restyle the selected room with PCG and refine it*, start the same workflow.
  - New `pcg_restyle` agent tool (room names or `c-*` prefixes, `dry_run`) and `restyle_rooms` MCP prompt.

## 1.2.0

**Renamed to Topo** (was MT_Zenith)
- New logo: a contour map with a climbing route and a summit flag (a "topo" is a climber's route diagram).
- Each zip now carries an `everest.yaml` (`Topo`, with the release version) so Everest, Olympus and mod databases recognise the download. Topo is still a standalone tool: unzip it outside `Celeste/Mods`; Everest loads nothing from it.
- The exe is now `Topo.exe` and the downloads are `Topo-<ver>-…zip` / `Topo-AI-<ver>-…zip`. Delete the old `MT_Zenith.exe` after unzipping.
- Settings, presets and custom PCG models are copied from `%APPDATA%\MT_Zenith` to `%APPDATA%\Topo` on first run; the old folder is left in place.
- A mod's recorded credits are now saved as `Topo.credits.json`; an existing `MT_Zenith.credits.json` is still read.
- The MCP server is now called `topo` (was `mt-zenith`): re-add it in your AI client with *AI ▸ Copy MCP client config*. The port is unchanged.
- The `MT_ZENITH_PCGHELPER` environment variable is now `TOPO_PCGHELPER` (the old name still works); the build switch is `-p:TopoAi=false`.

**Two editions**
- **Topo** (main download) now has no AI features: the MCP server, agent panel and AI media tools aren't compiled into it at all.
- **Topo + AI** is a separate, optional download with the AI tools. The window title shows which edition you're running.
- `publish.ps1` and the release workflow build both editions (`Topo-<ver>-…zip` and `Topo-AI-<ver>-…zip`). Build the standard edition from source with `dotnet build -p:TopoAi=false`.

**Agent tools (AI edition)**
- `view_map`: the agent sees the map as the editor canvas draws it (autotiled tiles, decals, entity sprites, triggers, room names): the whole map, a room with its surroundings, an area, or exactly what you are looking at in the window with your selection outlined. `editor_state` reports the camera, tool, layer and selection.
- Hand-crafted placement: `add_entity` and `build_room` entities take `rest` (floor, ceiling, left, right) to set an item down on the surface like placing it by hand, and `snap` for the editor's 8px grid. Items the agent places show up selected in the editor.
- `placement_memory`: what the agent picked and placed is remembered across maps and sessions (`%APPDATA%\Topo\ai_placements.json`), with the surface, attributes and its own notes; the agent gets a digest every turn.
- Preset groups for the agent: `list_presets`, `save_preset`, `place_preset`, `delete_preset`, sharing the Presets menu's `presets.json`. Presets saved by another running Topo (or an `--mcp` server) show up without a restart.

## 1.1.0

Released as MT_Zenith. Update from 1.0.0: replace `MT_Zenith.exe` with the new one (or unzip over the old folder). Settings, caches and PCG models in `%APPDATA%\MT_Zenith` are kept.

**New**
- **Room properties dialog** (Ctrl+R, also used for *New room*): camera offset, colour, wind, flags, checkpoint, music and ambience.
- **Project menu**: choose campaign/map, new empty or standard project, open/resync project, new map in a campaign, zip the mod.
- **Presets menu**: save selected entities/triggers as presets or preset groups (relative positions kept) and place them at the mouse.
- **Scripts menu**: run installed mods' Lönn scripts on the current room or all rooms through an emulated Lönn script API, undoable.
- Ahorn `@mapdef`/`@pardef` entities and triggers in the palette.
- Convert a room's FG tiles to filler rectangles and back; fillers drawn on the canvas.
- **Asset credits** (Assets menu): every decal, styleground, tileset, entity and trigger the map uses with who to credit, matched via Maddie480's mod database; exports `credits.txt`. Also an `asset_credits` MCP tool.
- **Community Asset Drive** browser: author readmes, "Recommended for this map", add files to your mod with their credit recorded.
- **UI themes** (View > Theme): 13 themes including Dark, Celeste, Desolo Zantas, Strawberry Jam, Spring Collab and Game Boy, or System.
- **Agent tools**: room/map reachability analysis (`analyze_room`, `analyze_map`), `build_room`, `draw_tiles`, `connect_rooms`, `duplicate_room`, atomic `batch`, design notes, Strongest-effort mode and permission policy in the agent panel.
- **PCG pipeline**: best-of-N candidates scored by traversability and tier, repair, mechanics filter, previews, and custom Markov models trained from your maps.

**Fixes**
- `get_room` / `study_reference_map` with tiles failed on newer .NET runtimes.
- Failing edit tools roll back instead of leaving half-applied changes.
