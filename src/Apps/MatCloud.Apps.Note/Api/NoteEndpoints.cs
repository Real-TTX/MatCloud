using MatCloud.Apps.Note.Business;
using MatCloud.Core.Api;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace MatCloud.Apps.Note.Api;

/// <summary>
/// What the pages fetch with the cookie of the browser (401 without a session, never the sign-in page): the long poll of the live list and the preview of
/// the editor (the Markdown drawn like the page of the note; the anti-forgery token of the page comes in the header <c>RequestVerificationToken</c>).
/// </summary>
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

        live.MapPost("/preview", async (HttpContext http, CurrentUser me, IAntiforgery antiforgery) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            try
            {
                var outcome = NoteService.Render(me.Actor, form["text"], form["title"]);
                return outcome.Ok
                    ? Results.Content(outcome.Value!, "text/html; charset=utf-8")
                    : Results.Json(new { message = outcome.Error, field = outcome.Field }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (NotAllowedException)
            {
                return Results.Forbid();
            }
        }).DisableAntiforgery();
    }
}
