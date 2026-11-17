using MatCloud.Apps.Note.Business;
using MatCloud.Core.Api;
using MatCloud.Core.Data;
using MatCloud.Core.Web;

namespace MatCloud.Apps.Note.Api;

/// <summary>What the pages fetch with the cookie of the browser: the long poll of the live list (401 without a session, never the sign-in page).</summary>
public static class NoteEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var live = app.MapGroup("/live/note").RequireAuthorization();
        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await NoteService.WaitAsync(db, me.Actor, since ?? 0, DeviceEndpoints.WaitTime(timeout), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });
    }
}
