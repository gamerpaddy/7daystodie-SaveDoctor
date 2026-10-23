using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>Players &amp; land claims manager endpoints.</summary>
public static partial class Api
{
    static void MapPlayers(WebApplication app, Func<SaveCtx> Need)
    {
        IResult J(object o) => Results.Json(o);

        app.MapGet("/api/players/list", () => J(Claims.List(Need())));

        app.MapPost("/api/players/claim", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            string action = b.GetProperty("action").GetString()!, user = b.GetProperty("user").GetString()!;
            int[] pos = b.GetProperty("pos").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (pos.Length != 3) throw new InvalidOperationException("Position needs x, y, z.");
            string msg;
            lock (writeLock)
            {
                msg = action switch
                {
                    "add" => Claims.AddClaim(c, user, pos),
                    "remove" => Claims.RemoveClaim(c, user, pos, b.TryGetProperty("removeBlock", out var rb) && rb.ValueKind == JsonValueKind.True),
                    "transfer" => Claims.TransferClaim(c, user, b.GetProperty("to").GetString()!, pos),
                    _ => throw new InvalidOperationException("Unknown action " + action),
                };
            }
            Log("players.xml", "claim-" + action, msg);
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/players/transfer", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            bool Flag(string n) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string msg; lock (writeLock) msg = Claims.TransferAssets(c, b.GetProperty("from").GetString()!, b.GetProperty("to").GetString()!, Flag("vehicles"), Flag("drones"), Flag("claims"));
            Log("", "transfer-assets", msg);
            return J(new { ok = true, message = msg });
        });

        app.MapGet("/api/players/source", (HttpContext http) =>
        {
            Need();
            var path = Q(http, "path").Trim().Trim('"');
            if (!Directory.Exists(path)) throw new InvalidOperationException("Folder not found: " + path);
            return J(Claims.SourcePlayers(path));
        });

        app.MapPost("/api/players/import", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            bool Flag(string n) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string msg; lock (writeLock) msg = Claims.ImportCharacter(c, b.GetProperty("path").GetString()!.Trim().Trim('"'), b.GetProperty("sourceRel").GetString()!, b.GetProperty("targetRel").GetString()!, Flag("items"), Flag("skills"), Flag("stats"), Flag("position"));
            Log(b.GetProperty("targetRel").GetString()!, "import-character", msg);
            return J(new { ok = true, message = msg });
        });
    }
}
