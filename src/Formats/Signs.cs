using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class SignWarpM { public string Type { get; set; } = ""; public string Info { get; set; } = ""; }

public sealed class SignLayerM
{
    public string Type { get; set; } = "";           // GroupSignLayer | TextSignLayer | PolygonSignLayer | NoiseSignLayer
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rot { get; set; }
    public double SX { get; set; } = 1;
    public double SY { get; set; } = 1;
    public string Color { get; set; } = "#FFFFFFFF";
    public string Mode { get; set; } = "ColorOnly";
    public List<SignWarpM> Warps { get; set; } = new();
    public int Sides { get; set; }
    public double Starify { get; set; }
    public double Smoothness { get; set; }
    public string? Font { get; set; }
    public string? Text { get; set; }
    public List<SignLayerM>? Layers { get; set; }
}

public sealed class SignModel
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Modified { get; set; } = "";
    public string Sharing { get; set; } = "";
    public List<SignLayerM> Layers { get; set; } = new();
    public int LayerCount => Count(Layers);
    public List<string> Texts => Collect(Layers, new List<string>());
    static int Count(List<SignLayerM> l) => l.Sum(x => 1 + (x.Layers != null ? Count(x.Layers) : 0));
    static List<string> Collect(List<SignLayerM> l, List<string> acc) { foreach (var x in l) { if (x.Text != null) acc.Add(x.Text); if (x.Layers != null) Collect(x.Layers, acc); } return acc; }
}

/// <summary>Drawn signs: the binary player stores (Player/&lt;id&gt;.signs), the xml libraries that ship with POIs, and an approximate SVG preview.</summary>
public static class Signs
{
    public const uint StoreMagic = 1313297232u;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────── binary sign (SignData.Read)
    static string Hex(float r, float g, float b, float a) => "#" + ((int)Math.Round(Math.Clamp(r, 0, 1) * 255)).ToString("X2") + ((int)Math.Round(Math.Clamp(g, 0, 1) * 255)).ToString("X2") + ((int)Math.Round(Math.Clamp(b, 0, 1) * 255)).ToString("X2") + ((int)Math.Round(Math.Clamp(a, 0, 1) * 255)).ToString("X2");

    static SignLayerM ReadLayer(BinaryReader r, int depth = 0)
    {
        if (depth > 32) throw new ParseFail("Sign nesting is too deep", r.Pos());
        long at = r.Pos();
        int t = r.ReadByte();
        var l = new SignLayerM();
        switch (t)
        {
            case 0:
                l.Type = "GroupSignLayer"; r.ReadByte(); r.ReadSingle(); r.ReadSingle(); r.ReadByte();
                int n = r.ReadInt32(); if (n < 0 || n > 100000) throw new ParseFail("Implausible layer count", r.Pos());
                l.Layers = new(); for (int i = 0; i < n; i++) l.Layers.Add(ReadLayer(r, depth + 1));
                break;
            case 1: l.Type = "TextSignLayer"; l.Font = r.Str(); l.Text = r.Str(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); break;
            case 2: l.Type = "PolygonSignLayer"; l.Sides = r.ReadInt32(); l.Smoothness = r.ReadSingle(); l.Starify = r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadByte(); break;
            case 3: l.Type = "NoiseSignLayer"; r.ReadInt32(); r.ReadInt32(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); break;
            default: throw new ParseFail($"Unknown sign layer type {t}", at);
        }
        l.Name = r.Str();
        l.X = r.ReadSingle(); l.Y = r.ReadSingle(); l.Rot = r.ReadSingle(); l.SX = r.ReadSingle(); l.SY = r.ReadSingle();
        l.Color = Hex(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        l.Mode = new[] { "ColorOnly", "ColorAndMask", "MaskOnly", "PunchOut" }.ElementAtOrDefault(r.ReadByte()) ?? "?";
        int wc = r.ReadInt32(); if (wc < 0 || wc > 1000) throw new ParseFail("Implausible warp count", r.Pos());
        for (int i = 0; i < wc; i++)
        {
            long wa = r.Pos(); int wt = r.ReadByte();
            string[] names = { "Skew", "Bulge", "Twirl", "Kaleido", "Perspective", "Arc", "Stretch", "Grid" };
            if (wt > 7) throw new ParseFail($"Unknown warp type {wt}", wa);
            int[] floats = { 3, 3, 4, 5, 4, 3, 6, 7 };       // payload size in 4-byte values
            r.Need(floats[wt] * 4L, "warp");
            r.BaseStream.Seek(floats[wt] * 4L, SeekOrigin.Current);
            l.Warps.Add(new SignWarpM { Type = names[wt] });
        }
        return l;
    }

    public static SignModel ReadBlob(byte[] blob)
    {
        using var r = BinUtil.Reader(blob);
        int ver = r.ReadInt32();
        if (ver <= 0 || ver > 3) throw new ParseFail($"Sign blob version {ver} is not readable", 0);
        var m = new SignModel { Guid = new Guid(r.ReadBytes(16)).ToString(), Name = r.Str() };
        m.Modified = new DateTime(r.ReadInt64(), DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm");
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
        m.Sharing = new[] { "Private", "Selectable", "Public" }.ElementAtOrDefault(r.ReadByte()) ?? "?";
        int n = r.ReadInt32(); if (n < 0 || n > 100000) throw new ParseFail("Implausible layer count", r.Pos());
        for (int i = 0; i < n; i++) m.Layers.Add(ReadLayer(r));
        if (r.Left() != 0) throw new ParseFail($"{r.Left()} trailing bytes in the sign", r.Pos());
        return m;
    }

    // ───────── player sign store container
    public static uint Crc32(byte[] d, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = off; i < off + len; i++) { c ^= d[i]; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1; }
        return ~c;
    }

    /// <summary>Parse the store container; throws ParseFail with the same reasons the game logs.</summary>
    public static List<byte[]> ReadStore(byte[] image)
    {
        if (image.Length < 32) throw new ParseFail($"File length {image.Length} is less than the 32 byte header", 0);
        if (BitConverter.ToUInt32(image, 0) != StoreMagic) throw new ParseFail("Invalid magic bytes", 0);
        int version = BitConverter.ToInt32(image, 4), count = BitConverter.ToInt32(image, 8), len = BitConverter.ToInt32(image, 16);
        uint crc = BitConverter.ToUInt32(image, 12);
        if (version != 3) throw new ParseFail($"Container version {version} is not readable (game reads 3)", 4);
        if (count < 0 || len < 0 || 32L + len != image.Length) throw new ParseFail($"Size mismatch: header records {len} payload bytes for {count} sign(s), file holds {image.Length - 32}", 16);
        if ((len > 0 ? Crc32(image, 32, len) : 0) != crc) throw new ParseFail("Checksum mismatch – likely an interrupted write", 12);
        var list = new List<byte[]>(count); int p = 32;
        for (int i = 0; i < count; i++)
        {
            if (p + 4 > image.Length) throw new ParseFail($"Ran out of data reading the length of sign {i} of {count}", p);
            int n = BitConverter.ToInt32(image, p); p += 4;
            if (n < 0 || p + n > image.Length) throw new ParseFail($"Sign {i} of {count} declares {n} bytes", p - 4);
            list.Add(image.AsSpan(p, n).ToArray()); p += n;
        }
        if (p != image.Length) throw new ParseFail($"{image.Length - p} trailing byte(s) after {count} sign(s)", p);
        return list;
    }

    public static byte[] BuildStore(List<byte[]> blobs)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(new byte[32]);
        foreach (var b in blobs) { w.Write(b.Length); w.Write(b); }
        w.Flush();
        var img = ms.ToArray();
        int len = img.Length - 32;
        BitConverter.GetBytes(StoreMagic).CopyTo(img, 0); BitConverter.GetBytes(3).CopyTo(img, 4); BitConverter.GetBytes(blobs.Count).CopyTo(img, 8);
        BitConverter.GetBytes(len > 0 ? Crc32(img, 32, len) : 0u).CopyTo(img, 12); BitConverter.GetBytes(len).CopyTo(img, 16);
        return img;
    }

    // ───────── xml sign libraries (game / POI)
    static double D(string? s, double def = 0) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : def;
    static (double, double) V2(string? s, double dx = 0, double dy = 0) { var p = (s ?? "").Split(','); return p.Length == 2 ? (D(p[0], dx), D(p[1], dy)) : (dx, dy); }

    static SignLayerM XmlLayer(XElement e)
    {
        var l = new SignLayerM { Type = (string?)e.Attribute("type") ?? "", Name = (string?)e.Attribute("name") ?? "", Rot = D((string?)e.Attribute("rot")), Color = (string?)e.Attribute("color") ?? (string?)e.Attribute("tint") ?? "#FFFFFFFF", Mode = (string?)e.Attribute("mode") ?? "ColorOnly" };
        (l.X, l.Y) = V2((string?)e.Attribute("pos")); (l.SX, l.SY) = V2((string?)e.Attribute("scale"), 1, 1);
        l.Sides = (int)D((string?)e.Attribute("sides")); l.Starify = D((string?)e.Attribute("starify")); l.Smoothness = D((string?)e.Attribute("smoothness"));
        l.Font = (string?)e.Attribute("font"); l.Text = (string?)e.Attribute("text");
        foreach (var w in e.Elements("warp")) l.Warps.Add(new SignWarpM { Type = ((string?)w.Attribute("type") ?? "").Replace("Warp", "") });
        if (l.Type == "GroupSignLayer") l.Layers = e.Elements("layer").Select(XmlLayer).ToList();
        return l;
    }

    public static SignModel FromXml(XElement e) => new()
    {
        Guid = ((string?)e.Attribute("guid") ?? "").ToLowerInvariant(), Name = (string?)e.Attribute("name") ?? "", Modified = (string?)e.Attribute("modified") ?? "", Sharing = (string?)e.Attribute("sharing") ?? "",
        Layers = e.Elements("layer").Select(XmlLayer).ToList(),
    };

    static Dictionary<string, string>? signFiles;
    static string? FindLibraryFile(string libraryId)
    {
        if (signFiles == null)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = GameInstall.DataDir();
            var roots = new List<string>(); if (data != null) roots.Add(Path.Combine(data, "Prefabs"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "LocalPrefabs"));
            foreach (var r in roots.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(r, "*_signs.xml", SearchOption.AllDirectories)) d[Path.GetFileName(f)[..^"_signs.xml".Length]] = f;
            signFiles = d;
        }
        return signFiles.GetValueOrDefault(libraryId);
    }

    /// <summary>Find a sign by library + guid: default library "[D]" (signs.xml), POI libraries (prefab *_signs.xml) or a player's .signs store.</summary>
    public static SignModel? Find(SaveCtx ctx, string library, string guid, out string source)
    {
        guid = guid.ToLowerInvariant(); source = "";
        try
        {
            if (library == "[D]")
            {
                var f = ctx.Game.ConfigFile("signs.xml");
                if (f != null) { source = "default signs (signs.xml)"; var e = XDocument.Load(f).Descendants("sign").FirstOrDefault(x => ((string?)x.Attribute("guid"))?.ToLowerInvariant() == guid); if (e != null) return FromXml(e); }
                return null;
            }
            var lib = FindLibraryFile(library);
            if (lib != null)
            {
                source = "POI sign library " + Path.GetFileName(lib);
                var e = XDocument.Load(lib).Descendants("sign").FirstOrDefault(x => ((string?)x.Attribute("guid"))?.ToLowerInvariant() == guid);
                if (e != null) return FromXml(e);
            }
            var rel = "Player/" + library + ".signs";
            if (File.Exists(ctx.Abs(rel)))
            {
                source = "player sign store " + rel;
                foreach (var b in ReadStore(ctx.ReadAll(rel))) { var m = ReadBlob(b); if (m.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)) return m; }
            }
        }
        catch (Exception ex) when (ex is ParseFail || ex is System.Xml.XmlException || ex is IOException) { source += " (unreadable: " + ex.Message + ")"; }
        return null;
    }

    // ───────── SVG preview (approximate: shapes, text, groups, colours; warps / noise / masks are not simulated)
    static string Col(string c, out double op)
    {
        op = 1;
        var h = c.TrimStart('#');
        if (h.Length == 8) { op = Convert.ToInt32(h.Substring(6, 2), 16) / 255.0; return "#" + h[..6]; }
        return "#" + (h.Length >= 6 ? h[..6] : "ffffff");
    }

    static void Svg(StringBuilder sb, List<SignLayerM> layers, ref double minX, ref double maxX, ref double minY, ref double maxY, double ox, double oy, double sx, double sy)
    {
        foreach (var l in layers)
        {
            sb.Append($"<g transform=\"translate({l.X.ToString(Inv)} {l.Y.ToString(Inv)}) rotate({l.Rot.ToString(Inv)}) scale({l.SX.ToString(Inv)} {l.SY.ToString(Inv)})\">");
            var fill = Col(l.Color, out var op);
            if (l.Type == "GroupSignLayer" && l.Layers != null)
            {
                double a = 0, b = 0, c = 0, d = 0; Svg(sb, l.Layers, ref a, ref b, ref c, ref d, 0, 0, 1, 1);
            }
            else if (l.Type == "PolygonSignLayer")
            {
                if (l.Sides == 4) sb.Append($"<rect x=\"-0.5\" y=\"-0.5\" width=\"1\" height=\"1\" fill=\"{fill}\" fill-opacity=\"{op.ToString(Inv)}\"/>");
                else
                {
                    int n = Math.Max(3, Math.Min(l.Sides, 64)); var pts = new List<string>();
                    for (int i = 0; i < n * (l.Starify > 0.01 ? 2 : 1); i++)
                    {
                        bool inner = l.Starify > 0.01 && i % 2 == 1; int k = l.Starify > 0.01 ? i / 2 : i;
                        double ang = -Math.PI / 2 + (l.Starify > 0.01 ? (i * Math.PI / n) : (2 * Math.PI * k / n)), rad = 0.5 * (inner ? 1 - l.Starify : 1);
                        pts.Add($"{(Math.Cos(ang) * rad).ToString("0.###", Inv)},{(Math.Sin(ang) * rad).ToString("0.###", Inv)}");
                    }
                    sb.Append($"<polygon points=\"{string.Join(' ', pts)}\" fill=\"{fill}\" fill-opacity=\"{op.ToString(Inv)}\"/>");
                }
            }
            else if (l.Type == "TextSignLayer")
                sb.Append($"<text x=\"0\" y=\"0\" font-size=\"1\" text-anchor=\"middle\" dominant-baseline=\"central\" font-family=\"{System.Security.SecurityElement.Escape(l.Font ?? "sans-serif")},sans-serif\" fill=\"{fill}\" fill-opacity=\"{op.ToString(Inv)}\" transform=\"scale(1 -1)\">{System.Security.SecurityElement.Escape(l.Text ?? "")}</text>");
            else if (l.Type == "NoiseSignLayer")
                sb.Append($"<rect x=\"-0.5\" y=\"-0.5\" width=\"1\" height=\"1\" fill=\"{fill}\" fill-opacity=\"{(op * 0.35).ToString(Inv)}\"/>");
            sb.Append("</g>");
        }
    }

    public static string ToSvg(SignModel m)
    {
        var sb = new StringBuilder();
        double a = 0, b = 0, c = 0, d = 0;
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-2 -1 4 2\" width=\"400\" height=\"200\"><rect x=\"-2\" y=\"-1\" width=\"4\" height=\"2\" fill=\"#1b1f27\"/><g transform=\"scale(1 -1)\">");
        Svg(sb, m.Layers, ref a, ref b, ref c, ref d, 0, 0, 1, 1);
        sb.Append("</g></svg>");
        return sb.ToString();
    }
}

public sealed class SignStoreHandler : FormatHandler
{
    public override string Kind => "signs";
    public override string Title => "Drawn signs of a player";
    public override bool Match(string rel) => rel.EndsWith(".signs", StringComparison.OrdinalIgnoreCase);

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data);
        if (data.Length == 0) return a.Add(Sev.Error, "empty", "File is empty (0 bytes).");
        List<byte[]> blobs;
        try { blobs = Signs.ReadStore(data); }
        catch (ParseFail pf) { return a.Add(Sev.Error, "container", "The game would ignore this file: " + pf.Message + ". Players lose their drawn signs unless the .bak is used.", pf.Offset); }
        int bad = 0; var names = new List<string>();
        for (int i = 0; i < blobs.Count; i++)
            try { var m = Signs.ReadBlob(blobs[i]); names.Add(m.Name); }
            catch (ParseFail pf) { bad++; a.Add(Sev.Error, "sign", $"Sign {i + 1} cannot be read: {pf.Message}"); }
        a.Summary = $"{blobs.Count} drawn sign(s)" + (bad > 0 ? $", {bad} damaged" : "");
        a.Info["signs"] = blobs.Count; a.Info["names"] = names.Take(100).ToList(); a.Editable = false;
        return a;
    }
}
