using System.Net;
using System.Threading.RateLimiting;
using H5.Master;
using H5.Net.Master;
using Microsoft.AspNetCore.RateLimiting;

// H5Reborn master server: lists public online lobbies, hands out join codes, and
// brokers NAT traversal (UDP rendezvous + relay). It never sees game rules or saves;
// games are hosted by players. Configure with H5_MASTER_* environment variables.
var app = MasterApp.Build(MasterOptions.FromEnvironment(), args);
await app.RunAsync();

namespace H5.Master
{
    public static class MasterApp
    {
        public const string HostTokenHeader = "X-Host-Token";

        public static WebApplication Build(MasterOptions options, string[]? args = null)
        {
            var builder = WebApplication.CreateSlimBuilder(args ?? Array.Empty<string>());
            builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries));
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<LobbyRegistry>();
            builder.Services.AddSingleton<RendezvousService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RendezvousService>());
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(ctx =>
                    RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress ?? IPAddress.None,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.RequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
                o.AddPolicy("create", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress ?? IPAddress.None,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            });

            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.UseRateLimiter();
            WebSocketBridge.Map(app);

            app.MapGet("/api/info", (MasterOptions o, RendezvousService rv) =>
                new MasterInfo(rv.Port, H5.Net.ProtocolInfo.Version, o.Motd));

            app.MapGet("/api/lobbies", (LobbyRegistry reg, string? fingerprint, int? protocol) =>
                reg.List(fingerprint, protocol).OrderByDescending(e => e.Created).Take(200).Select(LobbyRegistry.ToPublic).ToList());

            app.MapGet("/api/lobbies/code/{code}", (LobbyRegistry reg, string code) =>
                reg.GetByCode(code) is { } e ? Results.Ok(LobbyRegistry.ToPublic(e)) : Results.NotFound());

            app.MapPost("/api/lobbies", (LobbyRegistry reg, RendezvousService rv, HttpContext ctx, LobbyAd ad) =>
            {
                if (string.IsNullOrWhiteSpace(ad.Name) || string.IsNullOrWhiteSpace(ad.Mode)) return Results.BadRequest();
                var e = reg.Create(ad, ctx.Connection.RemoteIpAddress ?? IPAddress.None);
                return e == null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(new LobbyCreated(e.Id, e.Code, e.HostToken, rv.Port));
            }).RequireRateLimiting("create");

            app.MapPut("/api/lobbies/{id}", (LobbyRegistry reg, HttpContext ctx, string id, LobbyHeartbeat hb) =>
            {
                if (reg.Authorize(id, ctx.Request.Headers[HostTokenHeader]) is not { } e) return Results.NotFound();
                e.LastHeartbeat = DateTime.UtcNow;
                e.Players = Math.Clamp(hb.Players, 0, 64);
                e.OpenRooms = Math.Clamp(hb.OpenRooms, 0, 64);
                return Results.NoContent();
            });

            app.MapDelete("/api/lobbies/{id}", (LobbyRegistry reg, HttpContext ctx, string id) =>
            {
                if (reg.Authorize(id, ctx.Request.Headers[HostTokenHeader]) is not { } e) return Results.NotFound();
                reg.Remove(e);
                return Results.NoContent();
            });

            app.MapGet("/", () => Results.Text("H5Reborn master server"));
            return app;
        }
    }
}
