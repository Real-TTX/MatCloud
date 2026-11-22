using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract;
using MatCloud.Contract.Contacts;
using MatCloud.Core.Api;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;

namespace MatCloud.Apps.Contacts.Api;

/// <summary>
/// The routes of the module. For the devices (<see cref="DeviceEndpoints.ForApp"/> with guests – the one group of the module its grant reaches besides
/// the general sync: ThixContacts, ThixMail, and ThixCalendar for the GET): the pictures of
/// contacts, <c>POST /api/v1/contacts/photos</c> and <c>GET /api/v1/contacts/photos/{id}</c> (the contacts themselves travel in the general sync). For the
/// pages (the cookie of the browser, 401 without a session): the long poll of the live list and the pictures.
/// </summary>
public static class ContactsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = DeviceEndpoints.ForApp(app, CloudApps.Contacts, guests: true);
        api.MapPost(ContactsContract.Photos, async (HttpContext http, AppDbContext db, ContactPhotoStore store) =>
        {
            var bytes = await DeviceEndpoints.ReadBodyAsync(http.Request, ContactsContract.MaxPhotoBytes, http.RequestAborted);
            if (bytes is null)
            {
                return DeviceEndpoints.Error(StatusCodes.Status413PayloadTooLarge, $"Ein Bild hat höchstens {ContactsContract.MaxPhotoBytes / 1024 / 1024} MB.", "photo");
            }

            return DeviceEndpoints.Answer(await ContactPhotos.UploadAsync(db, DeviceEndpoints.ActorOf(http), store, bytes, http.RequestAborted));
        });
        api.MapGet(ContactsContract.Photos + "/{id}", async (HttpContext http, AppDbContext db, ContactPhotoStore store, string id) =>
        {
            var photo = await ContactPhotos.ReadAsync(db, DeviceEndpoints.ActorOf(http), store, id, http.RequestAborted);
            return photo.Ok
                ? Picture(http, photo.Value.Bytes, photo.Value.Type)
                : DeviceEndpoints.Error(StatusCodes.Status404NotFound, "Nicht gefunden.");
        });

        var live = app.MapGroup("/live/contacts").RequireAuthorization();
        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await ContactBook.WaitAsync(db, me.Actor, since ?? 0, DeviceEndpoints.WaitTime(timeout), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (NotAllowedException)
            {
                return Results.NotFound();
            }
        });
        // the field that picks contacts (MatUi FORM-25: a list too long for the page): ?q=&page= → { items: [{ value, name, description }], more, total }
        live.MapGet("/suggest", async (HttpContext http, AppDbContext db, CurrentUser me, string? q, int? page) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                const int size = 30;
                var number = Math.Max(1, page ?? 1);
                var found = await ContactBook.ListAsync(db, me.Actor, new ContactQuery(q, Offset: (number - 1) * size, Limit: size), http.RequestAborted);
                var items = found.Items.Select(c => new { value = c.Key, name = c.Name, description = c.Data.Emails.FirstOrDefault()?.Value ?? c.Data.Company });
                return Results.Json(new { items, more = number * size < found.Matching, total = found.Matching });
            }
            catch (NotAllowedException)
            {
                return Results.NotFound();
            }
        });
        live.MapGet("/photo/{id}",async (HttpContext http, AppDbContext db, CurrentUser me, ContactPhotoStore store, string id) =>
        {
            try
            {
                var photo = await ContactPhotos.ReadAsync(db, me.Actor, store, id, http.RequestAborted);
                return photo.Ok ? Picture(http, photo.Value.Bytes, photo.Value.Type) : Results.NotFound();
            }
            catch (NotAllowedException)
            {
                return Results.NotFound();
            }
        });
    }

    /// <summary>A picture: its id is its content, so it may be kept by the browser for long.</summary>
    private static IResult Picture(HttpContext http, byte[] bytes, string type)
    {
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(bytes, type);
    }
}
