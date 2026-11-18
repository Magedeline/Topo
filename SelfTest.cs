using CELMapViewer.Agent;

namespace CELMapViewer;

/// <summary>
/// <c>Topo --selftest [mapsDir] [pcgLoennRoot]</c>: checks that every .bin round-trips
/// byte-for-byte and that PCGHelper generation imports into a map that re-loads. Output goes to stdout.
/// </summary>
internal static class SelfTest
{
    private static string Dump(BinElement e)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(BinElement el, int depth)
        {
            sb.Append(' ', depth).Append(el.Name);
            foreach (var at in el.Attrs)
                sb.Append(' ').Append(at.Key).Append('=').Append(Convert.ToString(at.Value, System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
            foreach (var c in el.Children) Walk(c, depth + 1);
        }
        Walk(e, 0);
        return sb.ToString();
    }

    /// <summary>Tilesets, autotiled rendering of every map's rooms, tile-grid round trips, room resize and the plugin catalog.</summary>
    private static int AssetTest(string maps)
    {
        int failed = 0;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vfg = Autotiler.For(null, true, null);
            Console.WriteLine($"vanilla fg tilesets: {vfg?.Tilesets.Count() ?? 0} ({vfg?.Source ?? "missing"})");
            if (vfg == null || !vfg.Tilesets.Any()) failed++;

            int rooms = 0, painted = 0, gridFail = 0, maps0 = 0;
            foreach (string file in Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories))
            {
                var map = MapBin.Load(file);
                string? mod = MapModel.ModDirOf(file);
                var fg = Autotiler.For(MapModel.TilesetXml(map, true), true, mod);
                var bg = Autotiler.For(MapModel.TilesetXml(map, false), false, mod);
                if (fg == null || bg == null) { failed++; Console.WriteLine("NO TILESET " + file); continue; }
                maps0++;
                foreach (var l in MapModel.Levels(map).Children)
                {
                    var room = new Room(l);
                    rooms++;
                    var grid = room.Grid(true);
                    if (TileGrid.Parse(grid.Serialize(), room.Cols, room.Rows).Serialize() != grid.Serialize()) gridFail++;
                    if (rooms % 25 != 1) continue; // render a sample of rooms to keep the test quick
                    using var img = RoomRenderJob.From(room, fg, bg).Render();
                    if (img.Fg?[0] != null || img.Bg?[0] != null) painted++;
                }
            }
            Console.WriteLine($"autotile: {maps0} maps, {rooms} rooms, {painted} sampled rooms rendered, {gridFail} grid round-trip failures ({sw.ElapsedMilliseconds} ms)");
            if (gridFail > 0) failed++;

            // resize keeps content fixed in world space
            var t = new MapBin { Root = new BinElement("Map") };
            var lvl = MapModel.NewLevel("t", 0, 0, 32, 24, "1111\n1001\n1111", "");
            MapModel.Levels(t).Children.Add(lvl);
            var e = new BinElement("refill"); e.Set("x", 16); e.Set("y", 8);
            var r = new Room(lvl); r.EntitiesEl.Children.Add(e);
            MapModel.ResizeRoom(r, new Rectangle(-8, 0, 40, 24));
            bool resizeOk = r.W == 40 && e.Int("x") == 24 && r.Grid(true)[1, 0] == '1' && r.Grid(true)[0, 0] == '0';
            Console.WriteLine("room resize: " + (resizeOk ? "ok" : "FAILED"));
            if (!resizeOk) failed++;

            // directional enlarge: normal → xxl from the center, edge tiles extended, rooms beyond pushed
            var m2 = new MapBin { Root = new BinElement("Map") };
            var a2 = MapModel.NewLevel("a", 0, 0, 320, 184, string.Join("\n", Enumerable.Repeat(new string('1', 40), 2).Concat(Enumerable.Repeat("1" + new string('0', 38) + "1", 19)).Concat(Enumerable.Repeat(new string('1', 40), 2))), "");
            var b2 = MapModel.NewLevel("b", 320, 0, 320, 184, "", "");
            MapModel.Levels(m2).Children.Add(a2); MapModel.Levels(m2).Children.Add(b2);
            var ra = new Room(a2);
            var xxl = RoomSizing.TierSize("xxl", "square");
            var (dl, dt, dr, db) = RoomSizing.DeltasFor(ra.Bounds, xxl, "center");
            var res2 = RoomSizing.Resize(m2, ra, dl, dt, dr, db, RoomSizing.Fill.Extend, '1', pushRooms: true);
            var ga = ra.Grid(true);
            bool sizingOk = res2.New.Size == xxl && res2.New.X == -dl && res2.Overlaps.Count == 0 && res2.Pushed.SequenceEqual(new[] { "b" })
                            && new Room(b2).X == 320 + dr && ga[0, ga.Rows / 2] == '1' && ga[ga.Cols - 1, ga.Rows / 2] == '1'
                            && ga[ga.Cols / 2, 0] == '1' && ga[ga.Cols / 2, ga.Rows - 1] == '1' && ga[ga.Cols / 2, ga.Rows / 2] == '0'
                            && RoomSizing.TierOf(res2.New.Width, res2.New.Height) == "xxl";
            Console.WriteLine("room sizing (normal → xxl, all directions): " + (sizingOk ? "ok" : "FAILED"));
            if (!sizingOk) failed++;

            var clip = new BinElement("spinner"); clip.Set("x", 4); clip.Set("y", 8.5f); clip.Set("attachToSolid", true); clip.Set("color", "Blue");
            var nd = new BinElement("node"); nd.Set("x", 12); nd.Set("y", 0); clip.Children.Add(nd);
            var back = ItemClipboard.Deserialize(ItemClipboard.Serialize(new[] { ("entities", clip) }));
            bool clipOk = back is { Count: 1 } && back[0].Layer == "entities" && Dump(back[0].El) == Dump(clip) &&
                          back[0].El.Attrs.Select(a => a.Value.GetType()).SequenceEqual(clip.Attrs.Select(a => a.Value.GetType()));
            Console.WriteLine("clipboard round-trip: " + (clipOk ? "ok" : "FAILED"));
            if (!clipOk) failed++;

            var frames = MainForm.CollapseFrames(new[] { "flag00", "flag01", "flag02", "rock", "sign1", "a/b10" });
            bool framesOk = frames.SequenceEqual(new[] { "a/b10", "flag", "rock", "sign1" }, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("decal frame collapse: " + (framesOk ? "ok" : "FAILED " + string.Join(",", frames)));
            if (!framesOk) failed++;

            var line = MapCanvas.LineCells(new Point(0, 0), new Point(4, 2)).ToList();
            bool lineOk = line.Count == 5 && line[0] == new Point(0, 0) && line[^1] == new Point(4, 2);
            Console.WriteLine("line cells: " + (lineOk ? "ok" : "FAILED"));
            if (!lineOk) failed++;

            if (Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories).FirstOrDefault() is { } shotFile)
            {
                var sm = MapBin.Load(shotFile);
                string? smod = MapModel.ModDirOf(shotFile);
                WindowsFormsSynchronizationContext.AutoInstall = false; // no message loop here to resume awaits on
                using var canvas = new MapCanvas
                {
                    FgTiler = Autotiler.For(MapModel.TilesetXml(sm, true), true, smod),
                    BgTiler = Autotiler.For(MapModel.TilesetXml(sm, false), false, smod)
                };
                canvas.SetRooms(MapModel.Levels(sm).Children.Select(l => new Room(l)).ToList(), keepView: true);
                var opts = new ScreenshotOptions(1f, true, true, true, true, false);
                RectangleF mapArea = canvas.MapWorldBounds!.Value;
                float ms = MapCanvas.ShotFits(mapArea, 1f) ? 1f : 0.25f;
                sw.Restart();
                using var mapShot = canvas.RenderScreenshotAsync(mapArea, canvas.Rooms, opts with { Scale = ms }).GetAwaiter().GetResult();
                var room0 = canvas.Rooms[0];
                using var roomShot = canvas.RenderScreenshotAsync(room0.Bounds, new[] { room0 }, opts with { Scale = 2f }).GetAwaiter().GetResult();
                var expect = MapCanvas.ShotSize(mapArea, ms);
                bool shotOk = mapShot.Size == expect && roomShot.Width == room0.W * 2 && roomShot.Height == room0.H * 2;
                Console.WriteLine($"screenshot: map {mapShot.Width}x{mapShot.Height} ({canvas.Rooms.Count} rooms), room {room0.Name} {roomShot.Width}x{roomShot.Height} " +
                                  $"({sw.ElapsedMilliseconds} ms): " + (shotOk ? "ok" : "FAILED"));
                if (!shotOk) failed++;
                string outDir = Path.Combine(Path.GetTempPath(), "mtz_shots");
                Directory.CreateDirectory(outDir);
                mapShot.Save(Path.Combine(outDir, "map.png"), System.Drawing.Imaging.ImageFormat.Png);
                roomShot.Save(Path.Combine(outDir, "room.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            const string lua = """
                local e = {}
                e.name = "Test/Thing"
                e.nodeLimits = {1, -1}
                e.fieldOrder = {"x", "y", "mode"}
                e.fieldInformation = { mode = { options = {"A", "B"}, editable = false }, speed = { fieldType = "integer", minimumValue = 0 } }
                e.placements = { name = "thing", data = { mode = "A", speed = 3 } }
                return e
                """;
            var defs = PluginCatalog.ParseFile(lua, "test", "entities/test.lua", false);
            var d0 = defs.FirstOrDefault();
            bool luaOk = d0 != null && d0.MinNodes == 1 && d0.MaxNodes == -1 && d0.FieldOrder.SequenceEqual(new[] { "x", "y", "mode" }) &&
                         d0.Fields.TryGetValue("mode", out var fm) && !fm.Editable && fm.Options.Count == 2 &&
                         d0.Fields.TryGetValue("speed", out var fs) && fs.FieldType == "integer" && fs.Min == 0;
            Console.WriteLine("plugin field info: " + (luaOk ? "ok" : "FAILED"));
            if (!luaOk) failed++;

            sw.Restart();
            PluginCatalog.LoadNow();
            var all = PluginCatalog.AllPlacements();
            Console.WriteLine($"plugins: {PluginCatalog.FileCount} files, {PluginCatalog.FailedCount} failed, {all.Count} placements " +
                              $"({all.Count(p => p.Plugin.Source == "CEL")} from CEL) in {sw.ElapsedMilliseconds} ms");
            if (all.Count == 0) failed++;
        }
        catch (Exception ex) { failed++; Console.WriteLine("ASSET ERROR: " + ex); }
        return failed;
    }

#if TOPO_AI
    /// <summary>Media analysis used by the AI tools/attachments: WAV decode + pitch, figures, image compare, ffmpeg video frames.</summary>
    private static int MediaTest()
    {
        int failed = 0;
        string dir = Path.Combine(Path.GetTempPath(), "celmv_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1.5 s, 440 Hz sine at -6 dBFS, 16-bit stereo 22.05 kHz.
            const int rate = 22050, n = rate * 3 / 2;
            string wav = Path.Combine(dir, "sine.wav");
            using (var bw = new BinaryWriter(File.Create(wav)))
            {
                bw.Write("RIFF"u8); bw.Write(36 + n * 4); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
                bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16); bw.Write("data"u8); bw.Write(n * 4);
                for (int i = 0; i < n; i++)
                {
                    short s = (short)(16384 * Math.Sin(2 * Math.PI * 440 * i / rate));
                    bw.Write(s); bw.Write(s);
                }
            }
            var pcm = Media.LoadAudio(wav, 0, 60);
            var stats = Media.AudioStats(pcm);
            double hz = stats["spectrum"]?["dominant_hz"]?.GetValue<double>() ?? 0, peak = stats["peak_dbfs"]?.GetValue<double>() ?? 0;
            using var fig = Media.AudioFigure(pcm, "sine");
            byte[] clip = Media.WavClip(pcm, 1);
            bool audioOk = pcm.Rate == rate && pcm.Ch.Length == 2 && Math.Abs(pcm.Duration - 1.5) < 0.01 && Math.Abs(hz - 440) < 5 &&
                           Math.Abs(peak + 6) < 0.5 && fig.Width > 0 && clip.Length > 44;
            Console.WriteLine($"media audio: {pcm.Duration:0.00}s, dominant {hz} Hz, peak {peak} dBFS " + (audioOk ? "ok" : "FAILED"));
            if (!audioOk) failed++;

            var blocks = AgentAttachments.Build(new[] { wav }, true, true);
            var types = blocks.Select(x => x?["type"]?.GetValue<string>()).ToList();
            bool attachOk = types.Contains("image") && types.Contains("audio") && types.Contains("text") && types.Contains("resource_link");
            Console.WriteLine("media attachment blocks: " + string.Join(",", types) + (attachOk ? " ok" : " FAILED"));
            if (!attachOk) failed++;

            using var a = new Bitmap(16, 8);
            using (var g = Graphics.FromImage(a)) g.Clear(Color.Black);
            using var b = (Bitmap)a.Clone();
            b.SetPixel(3, 2, Color.Red); b.SetPixel(4, 5, Color.Red);
            var (cmpFig, cmp) = Media.Compare(a, b, 0);
            using (cmpFig)
            using (var view = Media.PrepareForView(a, null, null, 512, "checker", out double scale))
            using (var sheet = Media.Sheet(new[] { (a, "a"), (b, "b") }, 1024, "checker"))
            {
                int changed = cmp["changed_pixels"]?.GetValue<int>() ?? -1;
                bool imageOk = changed == 2 && scale > 1 && view.Width == (int)Math.Round(16 * scale) && sheet.Width > 0 && Media.Png(sheet).Length > 0;
                Console.WriteLine($"media image: {changed} changed pixels, view scale {scale} " + (imageOk ? "ok" : "FAILED"));
                if (!imageOk) failed++;
            }

            if (Media.Ffmpeg is not { } ff) Console.WriteLine("media video: ffmpeg not found; skipped");
            else
            {
                string mp4 = Path.Combine(dir, "test.mp4");
                var (code, _, err) = Media.Run(ff, new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=160x90:rate=10", "-pix_fmt", "yuv420p", "-y", mp4 });
                var frames = code == 0 ? Media.FramesAt(mp4, Media.EvenTimes(0, 2, 3), 160) : new();
                double dur = code == 0 ? Media.DurationOf(Media.Probe(mp4)) : 0;
                bool videoOk = frames.Count == 3 && frames.All(f => f.Frame.Width == 160) && Math.Abs(dur - 2) < 0.2;
                foreach (var f in frames) f.Frame.Dispose();
                Console.WriteLine($"media video: {frames.Count} frames, {dur:0.00}s " + (videoOk ? "ok" : "FAILED " + err.Trim()));
                if (!videoOk) failed++;
            }
        }
        catch (Exception ex) { failed++; Console.WriteLine("MEDIA ERROR: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        return failed;
    }
#endif

    /// <summary>Room properties defaults, fillers, project manager, presets, Ahorn plugins and third-party Lönn scripts.</summary>
    private static int EditorToolsTest()
    {
        int failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{name}: {(ok ? "ok" : "FAILED" + (detail != null ? " " + detail : ""))}");
            if (!ok) failed++;
        }
        string dir = Path.Combine(Path.GetTempPath(), "mtz_tools_" + Guid.NewGuid().ToString("N"));
        try
        {
            // fillers: the floor becomes one rectangle covering exactly those tiles, and back
            var map = ProjectManager.NewMap("t/t/t");
            var room = new Room(MapModel.Levels(map).Children[0]);
            int solid = 0;
            for (int y = 0; y < room.Rows; y++) for (int x = 0; x < room.Cols; x++) if (room.Grid(true)[x, y] != '0') solid++;
            var rects = Fillers.TileRects(room);
            Check("fg tiles -> filler rects cover every tile", rects.Sum(r => r.Width * r.Height) == solid && rects.Count == 1, $"{rects.Count} rects, {rects.Sum(r => r.Width * r.Height)} vs {solid}");
            Fillers.RoomToFiller(map, room);
            Check("room removed, filler added", MapModel.Levels(map).Children.Count == 0 && Fillers.Rects(map).Count == 1 && Fillers.Rects(map)[0] == new Rectangle(0, 160, 320, 24));
            var back = Fillers.FillersToRooms(map);
            Check("fillers -> rooms", back.Count == 1 && Fillers.Rects(map).Count == 0 && new Room(back[0]).Grid(true)[39, 2] == '1');

            // project manager
            string mapPath = ProjectManager.Create(dir, "Test Mod", "me", "camp", "1-a", standard: true);
            var camps = ProjectManager.Campaigns(Path.Combine(dir, "Test_Mod"));
            var loaded = MapBin.Load(mapPath);
            Check("standard project", File.Exists(Path.Combine(dir, "Test_Mod", "everest.yaml")) && File.Exists(Path.Combine(dir, "Test_Mod", "Dialog", "English.txt"))
                && camps.ContainsKey("me/camp") && MapModel.Levels(loaded).Children.Count == 1 && loaded.Package == "me/camp/1-a", string.Join(",", camps.Keys));
            string zip = Path.Combine(dir, "TestMod.zip");
            Check("zip mod", ProjectManager.Zip(Path.Combine(dir, "Test_Mod"), zip) >= 3 && File.Exists(zip));

            // room camera offset survives the bin round trip
            var lvl = MapModel.Levels(loaded).Children[0];
            lvl.Set("cameraOffsetX", 1.5f); lvl.Set("cameraOffsetY", -2f);
            loaded.Save(mapPath);
            var re = MapModel.Levels(MapBin.Load(mapPath)).Children[0];
            Check("camera offset saved", re.Num("cameraOffsetX") == 1.5f && re.Num("cameraOffsetY") == -2f);

            // presets keep relative positions
            var a = new BinElement("spring"); a.Set("x", 100); a.Set("y", 50);
            var b = new BinElement("refill"); b.Set("x", 116); b.Set("y", 34);
            var preset = PresetStore.Make("pair", new List<(string, BinElement)> { ("entity", a), ("entity", b) });
            var items = PresetStore.Items(preset)!;
            Check("preset group", preset.Kind == "group" && items[0].El.Int("x") == 0 && items[0].El.Int("y") == 16 && items[1].El.Int("x") == 16 && items[1].El.Int("y") == 0);

            // Ahorn plugins
            var ahorn = PluginCatalog.ParseAhorn("@mapdef Entity \"Foo/Bar\" FooBar(x::Integer, y::Integer, width::Integer=Maple.defaultBlockWidth, fast::Bool=false, speed::Number=1.5, sprite::String=\"a,b\")\n" +
                "@pardef Baz(x1::Integer, y1::Integer, mode::String=\"on\") = Trigger(\"Foo/Baz\", x1=x1)", "test", false);
            var bar = ahorn.FirstOrDefault(d => d.Name == "Foo/Bar");
            Check("Ahorn @mapdef/@pardef", ahorn.Count == 2 && bar != null && bar.Placements[0].Data.Any(kv => kv.Key == "speed" && kv.Value is float f && f == 1.5f)
                && bar.Placements[0].Data.Any(kv => kv.Key == "sprite" && (string)kv.Value == "a,b") && ahorn.Any(d => d.Name == "Foo/Baz" && d.Trigger),
                string.Join("; ", ahorn.Select(d => d.Name + ":" + string.Join(",", d.Placements[0].Data.Select(kv => kv.Key + "=" + kv.Value)))));

            // Lönn script host with an inline script
            var smap = ProjectManager.NewMap("t/t/t");
            var level = MapModel.Levels(smap).Children[0];
            var door = new BinElement("door"); door.Set("id", 7); door.Set("x", 64); door.Set("y", 160);
            level.Child("entities")!.Children.Add(door);
            var info = new LoennScriptInfo
            {
                Name = "t", DisplayName = "t", Mod = "test", Rel = "t.lua",
                Code = """
                    local tiles = require("structs.tiles")
                    local s = { name = "t", parameters = { to = "3", sfx = "x" } }
                    function s.run(room, args)
                      for _, e in ipairs(room.entities) do if e._name == "door" then e._name = "FrostHelper/StaticDoor" e.openSfx = args.sfx end end
                      room.tilesFg = tiles.decode({ innerText = (tiles.matrixToTileString(room.tilesFg.matrix):gsub("1", args.to)) })
                      table.insert(room.entities, { _name = "refill", x = 8, y = 8, oneUse = true })
                      room.cameraOffsetX = 2
                    end
                    return s
                    """
            };
            var res = LoennScripts.Run(info, smap, level, false, (_, ps) => ps.ToDictionary(p => p.Key, p => (object)(p.Key == "sfx" ? "event:/door" : p.Value)));
            var ents = level.Child("entities")!.Children;
            var r2 = new Room(level);
            Check("Lönn script edits entities, tiles, attrs", res?.RoomsChanged == 1 && ents.Any(e => e.Name == "FrostHelper/StaticDoor" && e.Int("id") == 7 && e.Str("openSfx") == "event:/door")
                && ents.Any(e => e.Name == "refill" && e.Int("id") > 7) && r2.Grid(true)[0, 22] == '3' && level.Num("cameraOffsetX") == 2,
                string.Join(",", ents.Select(e => e.Name)) + " tile=" + r2.Grid(true)[0, 22]);

            // installed third-party scripts load (needs the game's Mods folder)
            var found = LoennScripts.Discover();
            Console.WriteLine($"Lönn scripts found: {found.Count} ({string.Join(", ", found.Take(8).Select(f => f.ToString()))}{(found.Count > 8 ? ", …" : "")})");
            if (found.FirstOrDefault(f => f.Name == "doorsToStatic") is { } frost)
            {
                var fmap = ProjectManager.NewMap("t/t/t");
                var fl = MapModel.Levels(fmap).Children[0];
                var d2 = new BinElement("door"); d2.Set("id", 3); d2.Set("x", 64); d2.Set("y", 160);
                fl.Child("entities")!.Children.Add(d2);
                var fr = LoennScripts.Run(frost, fmap, fl, true, (_, ps) => ps.ToDictionary(p => p.Key, p => p.Value));
                Check("FrostHelper doorsToStatic", fr?.RoomsChanged == 1 && fl.Child("entities")!.Children.Any(e => e.Name == "FrostHelper/StaticDoor"));
            }
            if (found.FirstOrDefault(f => f.Name == "removeTiles") is { } rm)
            {
                var rmap = ProjectManager.NewMap("t/t/t");
                var rl = MapModel.Levels(rmap).Children[0];
                var rr = LoennScripts.Run(rm, rmap, rl, false, (_, ps) => ps.ToDictionary(p => p.Key, p => p.Value));
                var rroom = new Room(rl);
                Check("LoennScripts removeAllTiles", rr?.RoomsChanged == 1 && rroom.Grid(true)[0, 22] == '0' && rl.Int("height") == 184 && rl.Child("entities")!.Children[0].Int("y") == 160);
            }
        }
        catch (Exception ex) { failed++; Console.WriteLine("EDITOR TOOLS ERROR: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        return failed;
    }

#if TOPO_AI
    /// <summary>The design tools: movement-model reachability, map connectivity, build_room/draw_tiles/connect_rooms/batch/design_notes through the tool layer.</summary>
    private static int DesignTest()
    {
        int failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{name}: {(ok ? "ok" : "FAILED" + (detail != null ? " " + detail : ""))}");
            if (!ok) failed++;
        }
        // the agent's placement memory and the presets go to a scratch folder, not the user's %APPDATA%
        string scratch = Path.Combine(Path.GetTempPath(), "mtz_design_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        PlacementMemory.FilePathOverride = Path.Combine(scratch, "ai_placements.json");
        PresetStore.FilePathOverride = Path.Combine(scratch, "presets.json");
        try
        {
            var map = new MapBin { Root = new BinElement("Map") };
            var host = new HeadlessHost();
            host.Load(map);
            var tools = new MapTools(host);
            ToolResult Call(string tool, string json) => tools.Call(tool, System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject);
            string Text(ToolResult r) => string.Join("\n", r.Content.OfType<System.Text.Json.Nodes.JsonObject>().Where(c => c["type"]?.ToString() == "text").Select(c => c["text"]?.ToString()));

            // room a: bordered screen with a floor and an exit on the right; spawn auto-placed on the floor
            var ra = Call("build_room", """{"name":"a","x":0,"y":0,"width":320,"height":184,"fill":"border","ops":[{"op":"rect","x":1,"y":20,"w":38,"h":2}],"exits":[{"side":"right","from":17,"to":19}]}""");
            Check("build_room (bordered, floor, exit, auto spawn)", !ra.IsError && Text(ra).Contains("\"reached\":true") && Text(ra).Contains("\"required_tech\":\"none\""), Text(ra));

            // room b to the right: floor on both sides of an 18-cell spiked pit -> the right exit is unreachable even with advanced tech
            var rb = Call("build_room", """{"name":"b","attach":{"room":"a","side":"right"},"fill":"border","ops":[{"op":"rect","x":1,"y":20,"w":10,"h":2},{"op":"rect","x":29,"y":20,"w":10,"h":2}],"exits":[{"side":"left","from":17,"to":19},{"side":"right","from":17,"to":19}],"spawn":{"x":20,"y":160},"entities":[{"name":"spikesUp","x":88,"y":176,"width":144}]}""");
            string tb = Text(rb);
            var roomB = MapModel.Levels(map).Children.Select(l => new Room(l)).First(r => r.Name == "b");
            string advRoute = RoomAnalysis.Analyze(roomB, Tech.Advanced).ToJson().ToJsonString();
            Check("analysis flags an unreachable exit", !rb.IsError && tb.Contains("\"min_tech\":\"unreachable\"") && tb.Contains("\"side\":\"left\",\"from\":17,\"to\":19,\"reached\":true"), "advanced: " + advRoute);

            // a platform in the middle turns it into two 6-tile jumps: no dash needed
            var dt = Call("draw_tiles", """{"room":"b","ops":[{"op":"platform","x":16,"y":20,"len":8}]}""");
            var an = Call("analyze_room", """{"room":"b"}""");
            Check("draw_tiles platform makes the exit reachable without a dash", !dt.IsError && Text(an).Contains("\"required_tech\":\"none\""), Text(an));

            // widen the pit back to 9 cells on one side: a dash is needed now
            Call("draw_tiles", """{"room":"b","ops":[{"op":"clear","x":11,"y":20,"w":9,"h":2}]}""");
            var an2 = Call("analyze_room", """{"room":"b"}""");
            Check("9-tile gap needs a dash", Text(an2).Contains("\"required_tech\":\"dash\""), Text(an2));

            // climbing: a 7-tall wall with a ledge beyond is reachable by grab + climb
            var rc = Call("build_room", """{"name":"c","x":0,"y":184,"width":320,"height":184,"fill":"border","ops":[{"op":"rect","x":1,"y":20,"w":38,"h":2},{"op":"rect","x":20,"y":13,"w":2,"h":7},{"op":"rect","x":22,"y":13,"w":17,"h":1}],"exits":[{"side":"right","from":10,"to":12}],"spawn":{"x":40,"y":160}}""");
            Check("wall climb onto a ledge reaches the exit", !rc.IsError && Text(rc).Contains("\"required_tech\":\"none\"") && Text(rc).Contains("climb"), Text(rc));

            // connect a (above) and c (below) with a carved vertical opening; the map graph then sees 2 connections and a traversable map
            var cc = Call("connect_rooms", """{"room_a":"a","room_b":"c","width":4}""");
            var am = Call("analyze_map", """{"detailed":true}""");
            string tm = Text(am);
            Check("connect_rooms + analyze_map", !cc.IsError && tm.Contains("\"connections\":2") && tm.Contains("\"connected_to_start\":3") && tm.Contains("\"start_room\":\"a\""), tm);

            // batch: atomic rollback leaves the map as it was
            string before = Dump(map.Root);
            var bt = Call("batch", """{"steps":[{"tool":"draw_tiles","args":{"room":"a","ops":[{"op":"rect","x":5,"y":5,"w":3,"h":3}]}},{"tool":"no_such_tool","args":{}}]}""");
            Check("batch atomic rollback", Text(bt).Contains("\"rolled_back\":true") && Dump(map.Root) == before, Text(bt));
            var bt2 = Call("batch", """{"steps":[{"tool":"draw_tiles","args":{"room":"a","ops":[{"op":"rect","x":5,"y":5,"w":3,"h":3}]}},{"tool":"get_room","args":{"room":"a","include_tiles":false}}]}""");
            Check("batch mixed edit + read", Text(bt2).Contains("\"completed\":2") && Dump(map.Root) != before, Text(bt2));

            // duplicate + mirror flips spikes
            Call("add_entity", """{"room":"a","name":"spikesLeft","x":8,"y":100,"attrs":{"height":16}}""");
            var dup = Call("duplicate_room", """{"room":"a","name":"a_m","x":640,"y":0,"mirror":true}""");
            var mirrored = MapModel.Levels(map).Children.Select(l => new Room(l)).First(r => r.Name == "a_m");
            Check("duplicate_room mirror", !dup.IsError && mirrored.EntitiesEl.Children.Any(e => e.Name == "spikesRight") && mirrored.Grid(true)[mirrored.Cols - 2, 20] == '1', Text(dup));

            // design notes (in memory while the map has no path)
            Call("design_notes", """{"action":"write","section":"plan","text":"a -> b -> c"}""");
            Call("design_notes", """{"action":"append","section":"todo","text":"spikes in b"}""");
            var notes = Call("design_notes", """{"action":"read"}""");
            Check("design_notes", Text(notes).Contains("a -> b -> c") && Text(notes).Contains("spikes in b") && tools.NotesSummary()?.Contains("[todo]") == true, Text(notes));

            // validate_map includes connectivity
            var v = Call("validate_map", "{}");
            Check("validate_map runs with connectivity", !v.IsError && Text(v).Contains("rooms"), Text(v));

            // overlay render
            var ov = Call("analyze_room", """{"room":"b","render":true,"scale":1}""");
            Check("analyze_room render", ov.Content.Any(c => c?["type"]?.ToString() == "image"));

            // hand placement: spikes dropped from mid-air land on the floor (row 20 = y 160), a block's bottom edge does, and a wall strip slides onto the wall
            Room RoomOf(string n) => MapModel.Levels(map).Children.Select(l => new Room(l)).First(r => r.Name == n);
            BinElement Last(string n) => RoomOf(n).EntitiesEl.Children[^1];
            var p1 = Call("add_entity", """{"room":"c","name":"spikesUp","x":64,"y":60,"rest":"floor","attrs":{"width":24}}""");
            int spikeId = Last("c").Int("id");
            bool restFloor = !p1.IsError && Last("c").Int("y") == 160 && Last("c").Int("x") == 64;
            var p2 = Call("add_entity", """{"room":"c","name":"testBlock","x":96,"y":43,"snap":true,"rest":"floor","attrs":{"width":16,"height":16}}""");
            int blockId = Last("c").Int("id");
            restFloor &= !p2.IsError && Last("c").Int("y") == 144 && Last("c").Int("x") == 96;
            var p3 = Call("add_entity", """{"room":"c","name":"spikesRight","x":80,"y":120,"rest":"left","attrs":{"height":16}}""");
            restFloor &= !p3.IsError && Last("c").Int("x") == 8;
            var p4 = Call("add_entity", """{"room":"c","name":"spikesDown","x":64,"y":100,"rest":"ceiling","attrs":{"width":16}}""");
            restFloor &= !p4.IsError && Last("c").Int("y") == 8;
            Check("add_entity rest (floor, block, wall, ceiling)", restFloor, Text(p1) + " | " + Text(p2) + " | " + Text(p3) + " | " + Text(p4));

            // every pick is remembered with its surface, notes stick, and the digest for the agent's context mentions it
            Call("placement_memory", """{"action":"note","type":"spikesUp","text":"keep 3 tiles of run-up before a strip"}""");
            Call("placement_memory", """{"action":"note","text":"the user likes wide floors"}""");
            var mem = Call("placement_memory", """{"action":"recall","query":"spikes"}""");
            string tmem = Text(mem);
            Check("placement_memory records picks and notes", tmem.Contains("\"type\":\"spikesUp\"") && tmem.Contains("\"count\":2") && tmem.Contains("\"floor\":1") && tmem.Contains("run-up") && tmem.Contains("\"ceiling\":1")
                && File.Exists(PlacementMemory.FilePath) && PlacementMemory.Summary()?.Contains("spikesUp") == true
                && Text(Call("placement_memory", """{"action":"recall"}""")).Contains("wide floors"), tmem);

            // preset group: saved from ids, listed, placed elsewhere with the same relative layout and fresh ids
            var sp = Call("save_preset", $$"""{"name":"spike step","room":"c","ids":[{{spikeId}},{{blockId}}]}""");
            var sp2 = Call("save_preset", $$"""{"name":"spike step","room":"c","ids":[{{spikeId}}]}""");
            var lp = Call("list_presets", """{"query":"spike"}""");
            int beforeCount = RoomOf("a").EntitiesEl.Children.Count;
            var pp = Call("place_preset", """{"name":"spike step","room":"a","x":106,"y":50}""");
            var ea = RoomOf("a").EntitiesEl.Children;
            bool presetOk = !sp.IsError && sp2.IsError && Text(lp).Contains("\"name\":\"spike step\"") && Text(lp).Contains("\"count\":2") && !pp.IsError
                && ea.Count == beforeCount + 2 && ea[^2].Name == "spikesUp" && ea[^2].Int("x") == 104 && ea[^2].Int("y") == 64
                && ea[^1].Name == "testBlock" && ea[^1].Int("x") == 136 && ea[^1].Int("y") == 48 && ea[^1].Int("id") != blockId;
            var area = Call("save_preset", """{"name":"corner","room":"c","area":{"x":56,"y":130,"width":64,"height":32}}""");
            presetOk &= !area.IsError && Text(area).Contains("2 item(s)");
            var del = Call("delete_preset", """{"name":"corner"}""");
            presetOk &= !del.IsError && !Text(Call("list_presets", "{}")).Contains("corner") && PresetStore.All.Count == 1;
            Check("preset groups (save, list, place, delete)", presetOk, Text(sp) + " | " + Text(lp) + " | " + Text(pp) + " | " + Text(area));

            // the editor-style picture: whole map and one room, plus the headless editor state
            var vm = Call("view_map", """{"max_size":800}""");
            var vr = Call("view_map", """{"room":"c","margin":32}""");
            var es = Call("editor_state", "{}");
            var vv = Call("view_map", """{"target":"view"}""");
            Check("view_map (map, room) and editor_state", !vm.IsError && vm.Content.Any(c => c?["type"]?.ToString() == "image") && Text(vm).Contains("\"name\":\"a_m\"")
                && !vr.IsError && Text(vr).Contains("768x496") && Text(es).Contains("\"window\":false") && vv.IsError, Text(vm) + " | " + Text(vr) + " | " + Text(vv));
        }
        catch (Exception ex) { failed++; Console.WriteLine("DESIGN ERROR: " + ex); }
        finally
        {
            PlacementMemory.FilePathOverride = null;
            PresetStore.FilePathOverride = null;
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
        return failed;
    }
#endif

    public static int Run(string[] args)
    {
        string? modDir = GameLink.FindModDir();
        string maps = args.Length > 0 ? args[0] : Path.Combine(modDir ?? ".", "Maps");
        int failed = 0, ok = 0;

        foreach (string file in Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories))
        {
            string tmp = Path.Combine(Path.GetTempPath(), "celmv_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var map = MapBin.Load(file);
                map.Save(tmp);
                byte[] a = File.ReadAllBytes(file), b = File.ReadAllBytes(tmp);
                if (a.AsSpan().SequenceEqual(b)) { ok++; continue; }
                int at = 0;
                while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
                // A different encoding of the same tree is fine (e.g. a writer that stored small ints as Int32).
                bool sameTree = Dump(MapBin.Load(tmp).Root) == Dump(map.Root);
                if (!sameTree) failed++;
                Console.WriteLine($"{(sameTree ? "REENCODED" : "DIFF")}  {file}: {a.Length} vs {b.Length} bytes, first difference at {at}");
            }
            catch (Exception ex) { failed++; Console.WriteLine("ERROR " + file + ": " + ex.Message); }
            finally { File.Delete(tmp); }
        }
        Console.WriteLine($"round-trip: {ok} identical, {failed} failed");
        failed += AssetTest(maps);
#if TOPO_AI
        failed += MediaTest();
        failed += DesignTest();
#else
        Console.WriteLine("standard edition: AI media and agent-tool tests skipped (not built in)");
#endif
        failed += EditorToolsTest();

        string? root = args.Length > 1 ? args[1] : PcgGenerator.FindLoennRoot(modDir);
        if (root == null) { Console.WriteLine("PCGHelper not found; skipping generation test"); return failed == 0 ? 0 : 1; }

        try
        {
            var gen = new PcgGenerator(root);
            Console.WriteLine("solid tilesets: " + string.Join(", ", gen.SolidTilesets().Select(t => t.Glyph + "=" + t.Name)));
            var res = gen.Generate(new PcgOptions { Seed = "celeste", NbRooms = 4, Background = "bg_1" }, s => Console.WriteLine("  lua: " + s));
            Console.WriteLine($"generated {res.Rooms.Count} rooms in {res.Elapsed.TotalSeconds:0.00}s, seed {res.Seed}, playable={res.AllPlayable}");
            foreach (var r in res.Rooms)
                Console.WriteLine($"  {r.Name} at {r.X},{r.Y} {r.WTiles}x{r.HTiles} tiles, {r.Entities.Count} entities: " +
                                  string.Join(" ", r.Entities.GroupBy(e => e.Name).Select(g => g.Key + "×" + g.Count())));

            var res2 = gen.Generate(new PcgOptions { Seed = "celeste", NbRooms = 4, Background = "bg_1" }, _ => { });
            bool same = res.Rooms.Count == res2.Rooms.Count &&
                        res.Rooms.Zip(res2.Rooms).All(p => p.First.Fg.SequenceEqual(p.Second.Fg));
            Console.WriteLine("deterministic for same seed: " + same);
            if (!same) failed++;

            string sample = Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories).First();
            var host = MapBin.Load(sample);
            int before = MapModel.Levels(host).Children.Count;
            var added = MapModel.ImportPcg(host, res, "pcg_");
            string tmp = Path.Combine(Path.GetTempPath(), "celmv_import.bin");
            host.Save(tmp);
            var back = MapBin.Load(tmp);
            int after = MapModel.Levels(back).Children.Count;
            File.Delete(tmp);
            Console.WriteLine($"import into {Path.GetFileName(sample)}: {before} -> {after} rooms ({string.Join(", ", added.Select(a => a.Str("name")))})");
            if (after != before + res.Rooms.Count) failed++;

            // pipeline: candidates, repair toward a tier, movement-model judgement
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cand = PcgPipeline.Run(gen, new PcgPipelineOptions
            {
                Base = new PcgOptions { Seed = "celeste", NbRooms = 3 }, Candidates = 2, Repair = true, TargetTier = 2,
                Allowed = PcgPipeline.ParseAllowed(new[] { "spikes", "spinners", "refills", "jump-throughs", "crumble blocks" })
            }, _ => { });
            bool pipeOk = cand.Result.Rooms.Count == 3 && cand.Report["candidates"] is { } && cand.Score > 0
                          && cand.Result.Rooms.All(r => r.Entities.All(e => e.Name is "player" or "blackGem" || PcgPipeline.ParseAllowed(new[] { "spikes", "spinners", "refills", "jump-throughs", "crumble blocks" })!.Contains(e.Name)));
            Console.WriteLine($"pcg pipeline: best of 2 = seed \"{cand.Result.Seed}\" score {cand.Score:0.0}, {cand.Fixes.Count} repairs, {cand.Report["rooms_fully_traversable"]} traversable ({sw.ElapsedMilliseconds} ms) " + (pipeOk ? "ok" : "FAILED"));
            if (!pipeOk) failed++;
            using (var preview = PcgPipeline.Preview(cand.Result, null, null, 800))
            {
                bool prevOk = preview.Width > 0 && preview.Height > 0;
                Console.WriteLine($"pcg preview: {preview.Width}x{preview.Height} " + (prevOk ? "ok" : "FAILED"));
                if (!prevOk) failed++;
            }

            // training: a model estimated from the generated rooms, saved, used, deleted
            var corpusMap = new MapBin { Root = new BinElement("Map") };
            MapModel.ImportPcg(corpusMap, res, "t_");
            var corpus = PcgPipeline.Corpus(corpusMap).ToList();
            var tr = gen.Train(corpus, "fg", _ => { });
            const string modelName = "selftest_tmp_model";
            PcgGenerator.DeleteModel(modelName);
            var info = PcgGenerator.SaveModel(modelName, "fg", tr, "selftest");
            bool trainOk = tr.Patterns > 10 && tr.RoomsKept == corpus.Count && PcgGenerator.ModelNames().Contains(modelName);
            PcgResult? fromCustom = null;
            try { fromCustom = gen.Generate(new PcgOptions { Seed = "custom", NbRooms = 2, Model = modelName }, _ => { }); }
            catch (Exception ex) { Console.WriteLine("  custom model generation failed: " + (ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage : ex.Message)); }
            PcgGenerator.DeleteModel(modelName);
            trainOk &= fromCustom is { Rooms.Count: 2 } && !PcgGenerator.ModelNames().Contains(modelName);
            Console.WriteLine($"pcg train: {tr.Patterns} patterns from {tr.RoomsKept}/{tr.RoomsTotal} rooms ({tr.Entities} entities), generated {fromCustom?.Rooms.Count ?? 0} rooms from it, saved as {info.Name} then deleted " + (trainOk ? "ok" : "FAILED"));
            if (!trainOk) failed++;

            failed += RestyleTest(gen, maps);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("PCG ERROR: " + (ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage : ex.ToString()));
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>PCG restyle on real rooms: borders, entities, fillers and other rooms untouched, routes kept, deterministic, tileset swap.</summary>
    private static int RestyleTest(PcgGenerator gen, string maps)
    {
        int failed = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"restyle: {what} " + (ok ? "ok" : "FAILED") + (detail.Length > 0 ? $" ({detail})" : ""));
            if (!ok) failed++;
        }
        string file = Directory.EnumerateFiles(maps, "*Stronghold*.bin", SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories).First();
        var map = MapBin.Load(file);
        var all = MapModel.Levels(map).Children.Select(l => new Room(l)).ToList();
        var picked = all.Where(r => r.Cols >= 20 && r.Rows >= 15).Take(8).ToList();
        var others = all.Except(picked).ToDictionary(r => r.Name, r => r.Grid(true).Serialize());
        var fillersBefore = Fillers.Rects(map).Count;
        var origFg = picked.ToDictionary(r => r.Name, r => r.Grid(true).Clone());
        var origEnt = picked.ToDictionary(r => r.Name, r => string.Join("|", r.EntitiesEl.Children.Select(e => $"{e.Name}{e.Num("x")},{e.Num("y")}")));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = new RestyleOptions { Seed = "restyle", Strength = 0.7 };
        var plan = PcgRestyle.Plan(gen, picked, o, _ => { });
        var plan2 = PcgRestyle.Plan(gen, picked, new RestyleOptions { Seed = "restyle", Strength = 0.7 }, _ => { });
        Check("deterministic", plan.Zip(plan2).All(p => (p.First.Fg?.Serialize() ?? "") == (p.Second.Fg?.Serialize() ?? "")));
        // TOPO_RESTYLE_DUMP=<png path>: before (left) / after (right) of every picked room, for eyeballing
        Bitmap? dump = null;
        if (Environment.GetEnvironmentVariable("TOPO_RESTYLE_DUMP") is { Length: > 0 })
        {
            int dw = picked.Max(r => r.W), dh = picked.Sum(r => r.H + 8);
            dump = new Bitmap(dw * 2 + 8, dh);
            using var g = Graphics.FromImage(dump);
            g.Clear(Color.FromArgb(24, 24, 32));
            int y = 0;
            foreach (var r in picked) { using var img = RoomRenderJob.From(r, Autotiler.For(null, true, null), Autotiler.For(null, false, null)).Render(); if (RoomImage.Level(img.Fg, 0) is { } l) g.DrawImage(l, 0, y, r.W, r.H); y += r.H + 8; }
        }
        int changed = PcgRestyle.Apply(plan);
        if (dump != null)
        {
            using (var g = Graphics.FromImage(dump))
            {
                int y = 0, x0 = picked.Max(r => r.W) + 8;
                foreach (var r in picked) { using var img = RoomRenderJob.From(r, Autotiler.For(null, true, null), Autotiler.For(null, false, null)).Render(); if (RoomImage.Level(img.Fg, 0) is { } l) g.DrawImage(l, x0, y, r.W, r.H); y += r.H + 8; }
            }
            dump.Save(Environment.GetEnvironmentVariable("TOPO_RESTYLE_DUMP")!);
            dump.Dispose();
        }
        Console.WriteLine($"restyle: {Path.GetFileName(file)}, {picked.Count} rooms in {sw.ElapsedMilliseconds} ms (both runs)");
        foreach (var r in plan) Console.WriteLine("  " + PcgRestyle.Describe(r));
        Check("some rooms changed", changed > 0, $"{changed}/{picked.Count}");

        bool borders = true, ents = true, routes = true;
        foreach (var r in picked)
        {
            var g = r.Grid(true);
            var o0 = origFg[r.Name];
            for (int x = 0; x < r.Cols; x++) borders &= g[x, 0] == o0[x, 0] && g[x, r.Rows - 1] == o0[x, r.Rows - 1];
            for (int y = 0; y < r.Rows; y++) borders &= g[0, y] == o0[0, y] && g[r.Cols - 1, y] == o0[r.Cols - 1, y];
            ents &= origEnt[r.Name] == string.Join("|", r.EntitiesEl.Children.Select(e => $"{e.Name}{e.Num("x")},{e.Num("y")}"));
            var before = Agent.RoomAnalysis.Report(PcgRestyle.WithFg(r, o0)).Basic;
            var after = Agent.RoomAnalysis.Report(r).Basic;
            for (int i = 0; i < Math.Min(before.Exits.Count, after.Exits.Count); i++)
                if ((int)(after.Exits[i].MinTech ?? (Agent.Tech)3) > (int)(before.Exits[i].MinTech ?? (Agent.Tech)3)) { routes = false; Console.WriteLine($"  {r.Name}: exit {after.Exits[i].Label} got harder"); }
        }
        Check("borders/openings unchanged", borders);
        Check("entities unchanged", ents);
        Check("no exit got harder", routes);
        Check("other rooms and fillers untouched", Fillers.Rects(map).Count == fillersBefore &&
              all.Except(picked).All(r => others[r.Name] == r.Grid(true).Serialize()));

        var swap = PcgRestyle.Plan(gen, picked.Take(2).ToList(), new RestyleOptions { Surfaces = false, Tileset = '3' }, _ => { });
        PcgRestyle.Apply(swap);
        bool swapped = picked.Take(2).All(r => { var g = r.Grid(true); for (int y = 0; y < r.Rows; y++) for (int x = 0; x < r.Cols; x++) if (g[x, y] != '0' && g[x, y] != '3') return false; return true; });
        Check("tileset swap", swapped && swap.Sum(s => s.Swapped) > 0);

#if TOPO_AI
        // the agent tool: prefix pattern, dry run leaves the map alone, a real run is one undo step
        var host = new Agent.HeadlessHost();
        host.Load(MapBin.Load(file), file);
        var tools = new Agent.MapTools(host);
        string Text(Agent.ToolResult r) => string.Join("\n", r.Content.OfType<System.Text.Json.Nodes.JsonObject>().Where(c => c["type"]?.ToString() == "text").Select(c => c["text"]?.ToString()));
        string Snap() => string.Join("\n", MapModel.Levels(host.Map!).Children.Select(l => new Room(l).Grid(true).Serialize()));
        string s0 = Snap();
        var dry = Text(tools.Call("pcg_restyle", System.Text.Json.Nodes.JsonNode.Parse("""{"rooms":["b-0*"],"seed":"t","dry_run":true}""") as System.Text.Json.Nodes.JsonObject));
        Console.WriteLine("  " + dry.Split('\n')[0]);
        Check("tool dry run leaves the map", Snap() == s0 && dry.Contains("dry run"));
        var run = Text(tools.Call("pcg_restyle", System.Text.Json.Nodes.JsonNode.Parse("""{"rooms":["b-0*"],"seed":"t"}""") as System.Text.Json.Nodes.JsonObject));
        bool edited = Snap() != s0;
        host.Undo();
        Check("tool applies, undo restores", edited && Snap() == s0, run.Split('\n')[0]);
        string prompt = Agent.Prompts.Get("restyle_rooms", new System.Text.Json.Nodes.JsonObject { ["rooms"] = "c-*, d-01" })["messages"]![0]!["content"]!["text"]!.ToString();
        string withArgs = Agent.Prompts.RestyleRooms("a-00", null, Agent.Prompts.RestyleArgs(new RestyleOptions { Strength = 0.4, Tileset = '3' }));
        Check("agent prompt", Agent.Prompts.List().Any(p => p?["name"]?.ToString() == "restyle_rooms") && prompt.Contains("rooms=[\"c-*\", \"d-01\"]")
              && withArgs.Contains("strength=0.4") && withArgs.Contains("tileset=\"3\"") && withArgs.Contains("dry_run=true"));
#endif
        return failed;
    }
}
