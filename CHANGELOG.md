# Changelog

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
