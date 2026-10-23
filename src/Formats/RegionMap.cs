using System.Text.RegularExpressions;
using System.Xml.Linq;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>Top-down picture and marker overlay of a region file (players, vehicles, power, containers…).</summary>
public static class RegionMap
{
    static readonly (Regex rx, byte r, byte g, byte b)[] Rules =
    {
        (new Regex("bedrock", RegexOptions.IgnoreCase), 48, 48, 54), (new Regex("snow|ice|frost", RegexOptions.IgnoreCase), 236, 241, 246),
        (new Regex("oreCoal|coal", RegexOptions.IgnoreCase), 34, 34, 36), (new Regex("oreIron", RegexOptions.IgnoreCase), 176, 112, 92),
        (new Regex("ore", RegexOptions.IgnoreCase), 206, 176, 70),
        (new Regex("grass|plant|leaf|leaves|moss|cactus|bush|shrub|crop|corn|potato|mushroom|aloe|yucca", RegexOptions.IgnoreCase), 74, 134, 58),
        (new Regex("tree|trunk|log", RegexOptions.IgnoreCase), 46, 98, 44),
        (new Regex("sand|desert", RegexOptions.IgnoreCase), 216, 198, 132), (new Regex("gravel", RegexOptions.IgnoreCase), 142, 137, 132),
        (new Regex("dirt|soil|mud|farm|clay|forest", RegexOptions.IgnoreCase), 122, 86, 56),
        (new Regex("concrete|cement|asphalt|road|pavement", RegexOptions.IgnoreCase), 152, 152, 158),
        (new Regex("stone|rock|cobble|boulder|granite", RegexOptions.IgnoreCase), 126, 126, 132),
        (new Regex("glass|window", RegexOptions.IgnoreCase), 150, 212, 232), (new Regex("brick|terracotta|tile", RegexOptions.IgnoreCase), 164, 72, 56),
        (new Regex("wood|plank|pole|door|frame|fence|scaffold|beam", RegexOptions.IgnoreCase), 168, 122, 72),
        (new Regex("iron|steel|metal|rebar|tin|aluminum|vault|pipe|barrel|duct|vent|bar|grate", RegexOptions.IgnoreCase), 122, 134, 150),
        (new Regex("cloth|carpet|couch|bed|curtain|fabric|wool|sofa", RegexOptions.IgnoreCase), 176, 92, 124),
        (new Regex("light|lamp|torch|neon|candle|lantern|fire|flame", RegexOptions.IgnoreCase), 255, 226, 110),
        (new Regex("cnt|chest|storage|safe|crate|locker|cabinet|workbench|forge|campfire|cooker|desk", RegexOptions.IgnoreCase), 208, 124, 42),
        (new Regex("wire|power|solar|battery|generator|switch|trap|turret|spike", RegexOptions.IgnoreCase), 244, 204, 44),
    };

    static (byte, byte, byte) ColorOf(string name, int id)
    {
        var b = name.Split(':')[0];
        foreach (var (rx, r, g, bl) in Rules) if (rx.IsMatch(b)) return (r, g, bl);
        uint h = 2166136261; foreach (var ch in b.Length > 0 ? b : id.ToString()) h = (h ^ ch) * 16777619;
        return ((byte)(90 + (h & 127)), (byte)(90 + ((h >> 8) & 127)), (byte)(90 + ((h >> 16) & 127)));
    }

    static readonly Dictionary<string, (long len, long ticks, byte[] png)> cache = new();

    public static byte[] TopDown(SaveCtx ctx, string rel, byte[] data)
    {
        var fi = new FileInfo(ctx.Abs(rel)); var key = ctx.Root + "|" + rel;
        lock (cache) if (cache.TryGetValue(key, out var c) && c.len == fi.Length && c.ticks == fi.LastWriteTimeUtc.Ticks) return c.png;
        RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz);
        var sc = RegionIO.Scan(data, rx, rz, false, false);
        const int W = 512;
        var height = new short[W * W]; Array.Fill(height, (short)-1);
        var rgb = new byte[W * W * 3];
        for (int i = 0; i < W * W; i++) { rgb[i * 3] = 20; rgb[i * 3 + 1] = 24; rgb[i * 3 + 2] = 31; }
        var lut = new Dictionary<int, (byte, byte, byte)>();
        foreach (var s in sc.Slots.Where(s => s.Blob != null))
        {
            var ci = ChunkCodec.Inspect(s.Blob!, false);
            if (!ci.Ok || ci.Inflated == null || ci.Version != ChunkCodec.CurrentVersion) continue;
            int bx = (s.ChunkX - rx * 32) * 16, bz = (s.ChunkZ - rz * 32) * 16;
            if (bx < 0 || bz < 0 || bx >= W || bz >= W) continue;
            var done = new bool[256]; int left = 256;
            foreach (var lr in ci.Layers.OrderByDescending(l => l.Index))
            {
                for (int yy = 3; yy >= 0 && left > 0; yy--)
                    for (int cell = 0; cell < 256; cell++)
                    {
                        if (done[cell]) continue;
                        int id = ChunkCodec.BlockIdAt(ci.Inflated, lr, yy * 256 + cell);
                        if (id == 0) continue;
                        done[cell] = true; left--;
                        int x = cell % 16, z = cell / 16;
                        int px = bx + x, py = W - 1 - (bz + z);
                        if (!lut.TryGetValue(id, out var col)) lut[id] = col = ColorOf(ctx.Game.BlockNames.GetValueOrDefault(id, "block#" + id), id);
                        int o = py * W + px;
                        height[o] = (short)(lr.Index * 4 + yy);
                        rgb[o * 3] = col.Item1; rgb[o * 3 + 1] = col.Item2; rgb[o * 3 + 2] = col.Item3;
                    }
                if (left == 0) break;
            }
        }
        // relief shading (light from the north-west)
        for (int y = 1; y < W - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                int o = y * W + x; if (height[o] < 0) continue;
                int n = height[(y + 1) * W + (x - 1)]; if (n < 0) n = height[o];
                double sh = 1 + Math.Clamp((height[o] - n) * 0.06, -0.3, 0.3) + (height[o] - 60) * 0.0015;
                for (int k = 0; k < 3; k++) rgb[o * 3 + k] = (byte)Math.Clamp(rgb[o * 3 + k] * sh, 0, 255);
            }
        var png = PngWriter.Encode(rgb, W, W);
        lock (cache) { cache[key] = (fi.Length, fi.LastWriteTimeUtc.Ticks, png); if (cache.Count > 24) cache.Remove(cache.Keys.First()); }
        return png;
    }

    sealed class Mk
    {
        public string Type { get; set; } = "";
        public string Label { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? Detail { get; set; }
        public string? File { get; set; }
        public int Rx { get; set; }
        public int Rz { get; set; }
    }

    static readonly Regex ContainerRx = new("^cnt|storage|chest|safe|locker|crate|cabinet|desk|dresser|nightstand|mailbox|trunk|fridge|vending|trader|workbench|forge|campfire|cement|chemistry|bedroll|sleepingbag|landclaim|lpc|sign|drawer|shelf|bookcase|toolbox|armorybox|gunsafe|container", RegexOptions.IgnoreCase);

    public static object Collect(SaveCtx c, string rel)
    {
        var baseRel = Registry.SplitBackup(rel).baseRel;
        RegionIO.TryParseName(baseRel, out int rx, out int rz);
        var g = c.Game; var list = new List<Mk>();
        void Add(string type, string label, double x, double y, double z, string? detail = null, string? file = null)
            => list.Add(new Mk { Type = type, Label = label, X = x, Y = y, Z = z, Detail = detail, File = file, Rx = (int)Math.Floor(x / 512), Rz = (int)Math.Floor(z / 512) });
        var explored = new Dictionary<string, bool[]>();

        // players
        if (Directory.Exists(c.Abs("Player")))
            foreach (var f in Directory.EnumerateFiles(c.Abs("Player"), "*.ttp"))
            {
                var prel = c.Rel(f);
                try
                {
                    var p = PlayerFile.Parse(File.ReadAllBytes(f), g); var e = p.Ecd; var nm = string.IsNullOrEmpty(e.EntityName) ? Path.GetFileNameWithoutExtension(f) : e.EntityName;
                    Add("player", nm, e.PosX, e.PosY, e.PosZ, $"{(p.Dead ? "dead · " : "")}level {p.Skills?.Level.ToString() ?? "?"} · {p.ZombieKills} zombie kills", prel);
                    if (p.LastSpawnPos.Any(v => v != 0)) Add("spawn", nm + " – last spawn", p.LastSpawnPos[0], p.LastSpawnPos[1], p.LastSpawnPos[2], null, prel);
                    foreach (var sp in p.SpawnPoints) Add("spawn", nm + " – spawn point", sp[0], sp[1], sp[2], null, prel);
                    foreach (var w in p.Waypoints) Add("waypoint", string.IsNullOrEmpty(w.Name) ? "waypoint" : w.Name, w.Pos[0], w.Pos[1], w.Pos[2], nm + " · " + w.Icon, prel);
                    if (p.MarkerPosition.Any(v => v != 0)) Add("marker", nm + " – map marker", p.MarkerPosition[0], p.MarkerPosition[1], p.MarkerPosition[2], null, prel);
                    var mapRel = prel.Substring(0, prel.Length - 4) + "/r." + rx + "." + rz + ".7rm";
                    if (File.Exists(c.Abs(mapRel)))
                    {
                        var raw = MapRegionHandler.Decode(c.ReadAll(mapRel), out _);
                        if (raw != null && raw.Length == MapRegionHandler.Expected)
                        {
                            var ex = new bool[1024];
                            for (int k = 0; k < 1024; k++) for (int i = 0; i < 256; i++) if (raw[k * 512 + i * 2] != 0 || raw[k * 512 + i * 2 + 1] != 0) { ex[k] = true; break; }
                            explored[nm] = ex;   // index = chunkZ*32 + chunkX (offset inside the region)
                        }
                    }
                }
                catch { }
            }

        // stored vehicles / drones
        foreach (var (file, type) in new[] { ("vehicles.dat", "vehicle"), ("drones.dat", "drone") })
            try
            {
                if (!File.Exists(c.Abs(file))) continue;
                foreach (var e in EntityListFile.Parse(c.ReadAll(file), g).Entities)
                {
                    string? owner = null;
                    if (type == "vehicle") owner = VehicleDataM.TryParse(e.EntityData, g, out _)?.Owner?.UserId;
                    else owner = DroneDataM.TryParse(e.EntityData, g, out _)?.Owner?.UserId;
                    Add(type, g.EntityClassName(e.EntityClass), e.PosX, e.PosY, e.PosZ, $"#{e.Id}" + (owner != null ? " · owner " + owner : ""), file);
                }
            }
            catch { }

        // power grid
        try
        {
            if (File.Exists(c.Abs("power.dat")))
                foreach (var n in PowerFile.Parse(c.ReadAll("power.dat"), g).All())
                    Add("power", PowerNodeM.TypeName(n.Type) + " – " + g.BlockNames.GetValueOrDefault(n.BlockId, "block#" + n.BlockId), n.Pos[0], n.Pos[1], n.Pos[2], PowerNodeM.IsSource(n.Type) ? (n.IsOn ? "on" : "off") : null, "power.dat");
        }
        catch { }

        // land claims, backpacks, beds … from players.xml
        try
        {
            if (File.Exists(c.Abs("players.xml")))
            {
                var doc = XDocument.Parse(XmlHandler.DecodeText(c.ReadAll("players.xml"), out _));
                foreach (var pl in doc.Descendants("player"))
                {
                    var who = (string?)pl.Attribute("playername") ?? "player";
                    void P(string el, string type, string label) { foreach (var e in pl.Descendants(el)) { var v = ((string?)e.Attribute("pos"))?.Split(','); if (v?.Length == 3 && double.TryParse(v[0], out var x) && double.TryParse(v[1], out var y) && double.TryParse(v[2], out var z)) Add(type, who + " – " + label, x, y, z, null, "players.xml"); } }
                    P("lpblock", "claim", "land claim"); P("bedroll", "bed", "bedroll"); P("backpack", "backpack", "dropped backpack");
                    foreach (var e in pl.Descendants("vendingmachinepositions").Elements("position")) { var v = ((string?)e.Attribute("pos"))?.Split(','); if (v?.Length == 3 && double.TryParse(v[0], out var x) && double.TryParse(v[1], out var y) && double.TryParse(v[2], out var z)) Add("vending", who + " – vending machine", x, y, z, null, "players.xml"); }
                }
            }
        }
        catch { }

        // things stored in the chunks of this region: containers / workstations and creatures
        var scan = RegionHandler.CachedScan(c, rel, c.ReadAll(rel), false);
        int te = 0, en = 0;
        foreach (var s in scan.Slots)
        {
            var ci = s.Chunk; if (ci == null) continue;
            foreach (var (block, lx, ly, lz) in ci.TeList)
            {
                var nm = g.BlockNames.GetValueOrDefault(block, "block#" + block);
                if (!ContainerRx.IsMatch(nm) || te >= 4000) continue; te++;
                Add("container", nm, s.ChunkX * 16 + lx, ly, s.ChunkZ * 16 + lz);
            }
            foreach (var (cls, x, y, z) in ci.EntList)
            {
                if (en >= 3000) break; en++;
                var nm = g.EntityClassName(cls);
                string type = nm.StartsWith("zombie", StringComparison.OrdinalIgnoreCase) ? "zombie" : nm.StartsWith("animal", StringComparison.OrdinalIgnoreCase) ? "animal" : nm == "item" ? "drop" : "entity";
                Add(type, nm, x, y, z);
            }
        }
        var inside = list.Where(m => m.Rx == rx && m.Rz == rz).ToList();
        var others = list.Where(m => (m.Rx != rx || m.Rz != rz) && m.Type is "player" or "vehicle" or "drone").ToList();
        return new { rx, rz, markers = inside, elsewhere = others, explored };
    }
}
