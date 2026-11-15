using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Editor-view tools: the agent sees the map the way the canvas draws it for the user (whole map, the current view,
// a room with its surroundings) and can read the editor's camera, tool and selection.
public sealed partial class MapTools
{
    private void RegisterEditor()
    {
        Add("view_map", "See the map the way the editor canvas draws it: autotiled tiles, decals, entity sprites (spikes, spinners, the player, tile blocks, …), " +
            "trigger boxes, room borders and room names, instead of the labelled boxes of render_room. target: 'map' (whole-map overview, default), " +
            "'view' (exactly what the user is looking at in the editor window right now, with their selection outlined), 'room' (one room; margin > 0 also shows " +
            "its surroundings and neighbours), 'selection' (around the user's selected items or room) or 'area' (a world rectangle). Returns the image and a legend " +
            "with every visible room's rectangle in image pixels. Use it to judge look and composition, to find rooms on the overview, and after edits to check " +
            "the result as the user sees it. Not available inside batch.", true,
            Props(("target", Enum("What to show (default: map, or room/area when those arguments are given).", "map", "view", "room", "selection", "area")),
                  ("room", P("string", "Room name for target room.")),
                  ("x", P("number", "World x of the area.")), ("y", P("number", "World y of the area.")),
                  ("width", P("number", "Area width in world px.")), ("height", P("number", "Area height in world px.")),
                  ("margin", P("integer", "World px added around the target (default 16 for the map, 0 for a room, 48 for a selection).")),
                  ("max_size", P("integer", "Longest image side in px when no scale is given (default 1600, up to 4096).")),
                  ("scale", P("number", "Image px per world px (default: fit max_size, at most 2).")),
                  ("entities", P("boolean", "Draw entities (default true; for target view the editor's own toggle).")),
                  ("triggers", P("boolean", "Draw triggers (default true; for target view the editor's own toggle).")),
                  ("borders", P("boolean", "Draw room borders (default true unless a single room without margin).")),
                  ("labels", P("boolean", "Draw room names (default like borders)."))), None, ViewMap);
        Add("editor_state", "What the user has in front of them in the editor window: the camera (world rectangle and zoom), the rooms in view, the active tool and layer, " +
            "what is shown or hidden, the selected room and items (with ids and positions), the placement picked in the palette and the room the running game is in. " +
            "Without a window (headless --mcp) it says so. Follow up with view_map target='view' to see the same picture.", true, new(), None, _ => EditorState());
    }

    private static RectangleF Inflated(RectangleF r, float by)
    {
        r.Inflate(by, by);
        return r;
    }

    private static RectangleF BoxOfSelected(Room room, string kind, BinElement el) =>
        kind.EndsWith("decal", StringComparison.OrdinalIgnoreCase) ? MapCanvas.DecalBox(room, el) : MapModel.Box(room, el, kind == "Trigger");

    private sealed record ViewPlan(RectangleF Area, string Title, float Scale, ScreenshotOptions Options, bool Labels,
                                   List<(string Name, Rectangle Bounds)> Rooms, List<RectangleF> Marks, JsonObject? View);

    private ToolResult ViewMap(JsonObject a)
    {
        string target = (S(a, "target") ?? (a.ContainsKey("room") ? "room" : a.ContainsKey("width") ? "area" : "map")).ToLowerInvariant();
        var plan = _host.Invoke(() =>
        {
            var map = MapOrThrow();
            var rooms = MapModel.Levels(map).Children.Select(l => new Room(l)).ToList();
            if (rooms.Count == 0) throw new ToolException("The map has no rooms yet.");
            RectangleF mapBounds = MapModel.MapBounds(map);
            var view = _host.View;
            int? margin = I(a, "margin") is int m ? Math.Clamp(m, 0, 8000) : null;
            bool entities = true, triggers = true, context = true;
            var marks = new List<RectangleF>();
            RectangleF area;
            string title;

            List<RectangleF> SelectionBoxes()
            {
                var boxes = new List<RectangleF>();
                if (view == null) return boxes;
                foreach (var (roomName, kind, el) in view.Selected)
                    if (rooms.FirstOrDefault(r => r.Name == roomName) is { } room) boxes.Add(BoxOfSelected(room, kind, el));
                if (boxes.Count == 0 && rooms.FirstOrDefault(r => r.Name == view.SelectedRoom) is { } sr) boxes.Add(sr.Bounds);
                return boxes;
            }

            switch (target)
            {
                case "map" or "all" or "overview":
                    area = Inflated(mapBounds, margin ?? 16);
                    title = $"Whole map ({rooms.Count} rooms)";
                    break;
                case "view" or "editor" or "screen":
                    if (view == null) throw new ToolException("There is no editor window (headless --mcp server). Use target 'map', 'room' or 'area'.");
                    area = view.Bounds;
                    area.Intersect(Inflated(mapBounds, 64));
                    if (area.Width < 8 || area.Height < 8) throw new ToolException("The editor is looking at empty space beside the map. Use target 'map'.");
                    title = $"Editor view at zoom {view.Zoom:0.##}";
                    entities = view.ShowEntities;
                    triggers = view.ShowTriggers;
                    marks = SelectionBoxes();
                    break;
                case "room":
                    var r = FindRoom(Req(a, "room"));
                    area = Inflated(r.Bounds, margin ?? 0);
                    title = $"Room {r.Name}";
                    context = (margin ?? 0) > 0;
                    break;
                case "selection":
                    if (view == null) throw new ToolException("There is no editor window (headless --mcp server), so nothing is selected.");
                    marks = SelectionBoxes();
                    if (marks.Count == 0) throw new ToolException("Nothing is selected in the editor.");
                    area = Inflated(marks.Aggregate(RectangleF.Union), margin ?? 48);
                    title = "Editor selection";
                    break;
                case "area":
                    area = new RectangleF((float)(D(a, "x") ?? 0), (float)(D(a, "y") ?? 0), (float)(D(a, "width") ?? 0), (float)(D(a, "height") ?? 0));
                    if (area.Width < 8 || area.Height < 8) throw new ToolException("target area needs x, y, width and height in world px.");
                    area = Inflated(area, margin ?? 0);
                    title = $"Area ({area.X:0},{area.Y:0}) {area.Width:0}x{area.Height:0}";
                    break;
                default:
                    throw new ToolException("target must be map, view, room, selection or area.");
            }

            var visible = rooms.Where(x => area.IntersectsWith(x.Bounds)).Select(x => (x.Name, x.Bounds)).ToList();
            if (visible.Count == 0) throw new ToolException("No room lies in that area.");

            float longest = Math.Max(area.Width, area.Height);
            float scale = D(a, "scale") is double s && s > 0
                ? (float)Math.Clamp(s, 0.02, 4)
                : Math.Min(2f, Math.Clamp(I(a, "max_size") ?? 1600, 128, 4096) / longest);
            while (scale > 0.02f && (!MapCanvas.ShotFits(area, scale) || area.Width * scale * area.Height * scale > 16_000_000)) scale *= 0.8f;

            JsonObject? viewJson = target is "view" or "selection" && view != null ? ViewJson(view, rooms) : null;
            var options = new ScreenshotOptions(scale, B(a, "entities", entities), B(a, "triggers", triggers), B(a, "borders", context), false, false);
            return new ViewPlan(area, title, scale, options, B(a, "labels", context), visible, marks, viewJson);
        });

        // Small scales are rendered at twice the size and filtered down, the way the canvas uses mips, so thin floors don't vanish.
        float scale = plan.Scale, renderScale = scale < 1 ? Math.Min(1f, scale * 2) : scale;
        if (!MapCanvas.ShotFits(plan.Area, renderScale) || plan.Area.Width * renderScale * plan.Area.Height * renderScale > 40_000_000) renderScale = scale;
        var size = MapCanvas.ShotSize(plan.Area, scale);

        using var shot = _host.RenderWorld(plan.Area, plan.Options with { Scale = renderScale });
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = renderScale == scale ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the image edge when filtering down
                g.DrawImage(shot, new Rectangle(0, 0, size.Width, size.Height), 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel, ia);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (var selPen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash })
                foreach (var mark in plan.Marks)
                    g.DrawRectangle(selPen, (mark.X - plan.Area.X) * scale - 2, (mark.Y - plan.Area.Y) * scale - 2, mark.Width * scale + 4, mark.Height * scale + 4);
            if (plan.Labels)
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Segoe UI", 8.5f);
                using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                foreach (var (name, b) in plan.Rooms)
                {
                    if (b.Width * scale < 28) continue;
                    float x = Math.Max(0, (b.X - plan.Area.X) * scale), y = Math.Max(0, (b.Y - plan.Area.Y) * scale);
                    var ts = g.MeasureString(name, font);
                    g.FillRectangle(shadow, x + 1, y + 1, ts.Width, ts.Height - 1);
                    g.DrawString(name, font, Brushes.Gainsboro, x + 1, y);
                }
            }
        }

        var legend = new JsonObject
        {
            ["target"] = target,
            ["world_area"] = new JsonArray(Math.Round(plan.Area.X), Math.Round(plan.Area.Y), Math.Round(plan.Area.Width), Math.Round(plan.Area.Height)),
            ["scale"] = Math.Round(scale, 4),
            ["image"] = new JsonArray(size.Width, size.Height),
            ["mapping"] = "image px = (world px - world_area x,y) * scale; room-local px = world px - room x,y",
        };
        var roomsJson = new JsonArray();
        foreach (var (name, b) in plan.Rooms.Take(250))
            roomsJson.Add(new JsonObject
            {
                ["name"] = name,
                ["image_rect"] = new JsonArray((int)MathF.Round((b.X - plan.Area.X) * scale), (int)MathF.Round((b.Y - plan.Area.Y) * scale),
                                               (int)MathF.Round(b.Width * scale), (int)MathF.Round(b.Height * scale)),
                ["world"] = new JsonArray(b.X, b.Y, b.Width, b.Height)
            });
        legend["rooms"] = roomsJson;
        if (plan.Rooms.Count > 250) legend["rooms_omitted"] = plan.Rooms.Count - 250;
        if (plan.View != null) legend["editor"] = plan.View;
        if (plan.Marks.Count > 0) legend["selection"] = "outlined with a white dashed frame";

        var (data, mime) = EncodeForModel(bmp);
        return new ToolResult()
            .AddText($"{plan.Title}: {size.Width}x{size.Height} image as the editor canvas draws it.\n" + legend.ToJsonString(JsonOut))
            .AddImage(data, mime);
    }

    /// <summary>PNG, or JPEG when a detailed overview would be too heavy for an MCP message.</summary>
    private static (byte[] Data, string Mime) EncodeForModel(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        if (ms.Length <= 3_500_000) return (ms.ToArray(), "image/png");
        using var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        using var jpg = new MemoryStream();
        flat.Save(jpg, codec, ep);
        return (jpg.ToArray(), "image/jpeg");
    }

    private static JsonObject ViewJson(EditorView view, List<Room> rooms)
    {
        var b = view.Bounds;
        var selected = new JsonArray();
        foreach (var (roomName, kind, el) in view.Selected.Take(100))
        {
            var o = new JsonObject { ["room"] = roomName, ["kind"] = kind, ["name"] = kind.EndsWith("decal") ? el.Str("texture") : el.Name, ["x"] = el.Num("x"), ["y"] = el.Num("y") };
            if (el.Find("id") != null) o["id"] = el.Int("id");
            selected.Add(o);
        }
        var json = new JsonObject
        {
            ["window"] = true,
            ["view_world"] = new JsonArray(Math.Round(b.X), Math.Round(b.Y), Math.Round(b.Width), Math.Round(b.Height)),
            ["zoom"] = Math.Round(view.Zoom, 3),
            ["rooms_in_view"] = new JsonArray(rooms.Where(r => b.IntersectsWith(r.Bounds)).Take(100).Select(r => (JsonNode)r.Name).ToArray()),
            ["tool"] = view.Tool,
            ["layer"] = view.Layer,
            ["showing"] = new JsonObject { ["entities"] = view.ShowEntities, ["triggers"] = view.ShowTriggers, ["bg_tiles"] = view.ShowBg },
            ["selected_room"] = view.SelectedRoom,
            ["selected_items"] = selected,
        };
        if (view.Selected.Count > 100) json["selected_items_omitted"] = view.Selected.Count - 100;
        if (view.PalettePick != null) json["palette_pick"] = view.PalettePick;
        if (view.GameRoom != null) json["game_room"] = view.GameRoom;
        return json;
    }

    private ToolResult EditorState() => _host.Invoke(() =>
    {
        if (_host.View is not { } view)
            return ToolResult.Json(new JsonObject
            {
                ["window"] = false,
                ["note"] = "Headless --mcp server: nobody is looking at an editor window. view_map target map/room/area still renders the map as the editor would."
            });
        var rooms = _host.Map == null ? new List<Room>() : MapModel.Levels(_host.Map).Children.Select(l => new Room(l)).ToList();
        var json = ViewJson(view, rooms);
        json["map"] = _host.Path;
        json["unsaved_changes"] = _host.Dirty;
        return ToolResult.Json(json);
    });
}
