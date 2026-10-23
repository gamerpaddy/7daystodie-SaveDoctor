using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "scan" or "--scan")
            return Cli.Scan(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "selftest")
            return SelfTest.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("SaveDoctor – 7 Days to Die save editor / repair tool\n\n" +
                "  SaveDoctor.exe [savefolder]            open the web UI (optionally with a save folder preselected)\n" +
                "  SaveDoctor.exe scan <savefolder>       check every file and print a report (exit code 1 if damage was found)\n" +
                "  SaveDoctor.exe scan <savefolder> --fix apply the safe automatic repairs (restore from .bak, rebuild regions)\n" +
                "  options:  --port N   --no-browser");
            return 0;
        }
        return Server.Run(args);
    }
}

public static class Cli
{
    public static int Scan(string[] a)
    {
        var path = a.FirstOrDefault(x => !x.StartsWith("--"));
        if (path == null) { Console.Error.WriteLine("usage: SaveDoctor scan <savefolder> [--fix]"); return 2; }
        bool fix = a.Contains("--fix");
        var ctx = new SaveCtx(path);
        Console.WriteLine($"Scanning {ctx.Root}");
        Console.WriteLine();
        int bad = 0, warn = 0, total = 0;
        foreach (var rel in ctx.ListFilesRel().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (rel.StartsWith("ConfigsDump/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("map/", StringComparison.OrdinalIgnoreCase) && !a.Contains("--all")) continue;
            var an = Registry.Analyze(ctx, rel);
            total++;
            var tag = an.Worst switch { Sev.Error => "BAD ", Sev.Warn => "WARN", Sev.Info => "info", _ => " ok " };
            Console.WriteLine($"[{tag}] {rel,-60} {an.Summary}");
            foreach (var i in an.Issues.Where(i => i.Sev >= Sev.Warn || a.Contains("-v"))) Console.WriteLine($"         {i.Sev,-5} {i.Message}");
            if (an.Worst == Sev.Error) bad++; else if (an.Worst == Sev.Warn) warn++;
            if (fix && an.Worst == Sev.Error && !rel.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                var r = an.Repairs.FirstOrDefault(x => x.Id.StartsWith("restore")) ?? an.Repairs.FirstOrDefault(x => x.Id is "rebuild" or "truncate" or "salvage" or "fixStats");
                if (r != null)
                {
                    try
                    {
                        string msg = r.Id.StartsWith("restore") ? Registry.RestoreFrom(ctx, rel, r.Id.Substring("restore".Length))
                            : Repairs.Run(ctx, rel, r.Id, default);
                        Console.WriteLine($"         FIXED: {msg}");
                    }
                    catch (Exception ex) { Console.WriteLine($"         could not fix: {ex.Message}"); }
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{total} files checked: {bad} damaged, {warn} with warnings.");
        return bad > 0 ? 1 : 0;
    }
}

public static class Repairs
{
    public static string Run(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        rel = rel.Replace('\\', '/');
        var (baseRel, suffix) = Registry.SplitBackup(rel);
        if (action == "restoreFromThis" && suffix != null)
            return Registry.RestoreFrom(ctx, baseRel, suffix);
        if (action.StartsWith("restore.") || action.StartsWith("restore_"))
            return Registry.RestoreFrom(ctx, baseRel, "." + action.Substring(8));
        if (action == "restore.bak" || action == "restore.ext.bak") return Registry.RestoreFrom(ctx, baseRel, action.Substring("restore".Length));
        var h = Registry.For(rel);
        return h.Repair(ctx, baseRel, action, args);
    }
}

public static class Server
{
    static string token = "";
    static SaveCtx? ctx;
    static int port;
    static readonly object gate = new();

    public static int Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.Logging.ClearProviders();
        port = FreePort(args);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var app = builder.Build();

        // Anything other than the page itself needs the per-run token and a loopback Host header (blocks DNS-rebinding / cross-site calls).
        app.Use(async (c, next) =>
        {
            var host = c.Request.Host.Host;
            if (host != "127.0.0.1" && host != "localhost") { c.Response.StatusCode = 421; return; }
            if (c.Request.Path.StartsWithSegments("/api"))
            {
                var t = c.Request.Headers["X-SD-Token"].FirstOrDefault() ?? c.Request.Query["t"].FirstOrDefault();
                if (t != token) { c.Response.StatusCode = 403; await c.Response.WriteAsync("bad token"); return; }
                var origin = c.Request.Headers.Origin.FirstOrDefault();
                if (origin != null && origin != $"http://127.0.0.1:{port}" && origin != $"http://localhost:{port}") { c.Response.StatusCode = 403; return; }
            }
            try { await next(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!c.Response.HasStarted)
                {
                    c.Response.StatusCode = ex is SaveCtx.WriteGuardException ? 409 : ex is UnauthorizedAccessException ? 403 : 400;
                    c.Response.ContentType = "application/json";
                    await c.Response.WriteAsync(Json.Ser(new { error = ex.Message, type = ex.GetType().Name }));
                }
            }
        });

        Api.Map(app, () => ctx, c => { lock (gate) ctx = c; }, () => token);

        app.MapGet("/", (HttpContext c) =>
        {
            var html = ReadEmbedded("wwwroot/index.html").Replace("__TOKEN__", token);
            c.Response.Headers.CacheControl = "no-store";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/viewer3d.js", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Results.Content(ReadEmbedded("wwwroot/viewer3d.js"), "text/javascript; charset=utf-8");
        });

        var pre = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        if (pre != null) ctx = new SaveCtx(pre);

        string url = $"http://127.0.0.1:{port}/";
        Console.WriteLine("SaveDoctor is running at " + url);
        Console.WriteLine("Close this window (or press Ctrl+C) to stop it.");
        if (!args.Contains("--no-browser"))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
        app.Run();
        return 0;
    }

    static int FreePort(string[] args)
    {
        var i = Array.IndexOf(args, "--port");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int p)) return p;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return port;
    }

    public static string ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
