using MatCloud.Contract.Contacts;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Contacts.Business;

/// <summary>
/// The pictures of contacts: upload (devices of ThixContacts and ThixMail, the browser) and download (also ThixCalendar, which may read). A picture is
/// checked by its first bytes (JPEG, PNG, GIF, WebP) and its size; the id is the SHA-256 of the bytes, so the same picture is kept once per account.
/// </summary>
public static class ContactPhotos
{
    public static async Task<Outcome<ContactPhotoInfo>> UploadAsync(AppDbContext db, Actor actor, ContactPhotoStore store, byte[]? bytes, CancellationToken ct = default)
    {
        await ContactBook.RequireAsync(db, actor, write: true, ct);
        if (bytes is null || bytes.Length > ContactsContract.MaxPhotoBytes)
        {
            return Outcome<ContactPhotoInfo>.Fail($"Ein Bild hat höchstens {ContactsContract.MaxPhotoBytes / 1024 / 1024} MB.", "photo");
        }

        if (VCard.ImageTypeOf(bytes) is not { } type)
        {
            return Outcome<ContactPhotoInfo>.Fail("Das ist kein Bild (JPEG, PNG, GIF oder WebP).", "photo");
        }

        var id = await store.KeepAsync(actor.UserId, bytes, ct);
        return Outcome<ContactPhotoInfo>.Success(new ContactPhotoInfo(id, type, bytes.Length));
    }

    /// <summary>The bytes and the type of a picture of the account, or missing.</summary>
    public static async Task<Outcome<(byte[] Bytes, string Type)>> ReadAsync(AppDbContext db, Actor actor, ContactPhotoStore store, string? id, CancellationToken ct = default)
    {
        await ContactBook.RequireAsync(db, actor, write: false, ct);
        if (!ContactsContract.IsValidPhotoId(id) || await store.ReadAsync(actor.UserId, id!, ct) is not { } bytes)
        {
            return Outcome<(byte[], string)>.Missing();
        }

        return Outcome<(byte[], string)>.Success((bytes, VCard.ImageTypeOf(bytes) ?? "application/octet-stream"));
    }
}

/// <summary>
/// The housekeeping of the pictures (<see cref="ContactPhotoWorker"/>, which has the store; the hourly housekeeping of MatCloud only has the database):
/// pictures no contact of their account names any more go after a day (a device may upload a picture before its contact) – also those of an account whose
/// contacts were deleted.
/// </summary>
public static class ContactHousekeeping
{
    public static async Task<int> MaintainAsync(AppDbContext db, ContactPhotoStore store, CancellationToken ct)
    {
        var removed = ContactImports.CleanUp(store);
        foreach (var userId in store.Users().ToList())
        {
            var named = (await db.SyncItems.AsNoTracking()
                    .Where(i => i.UserId == userId && i.App == ContactBook.App && i.Collection == ContactsContract.Contacts && i.UpdateState != SoftEntity.Deleted)
                    .Select(i => i.Data)
                    .ToListAsync(ct))
                .Select(ContactsContract.ReadContact)
                .Select(c => c?.PhotoId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (id, _, written) in store.List(userId).ToList())
            {
                if (!named.Contains(id) && written < DateTime.UtcNow.AddDays(-1))
                {
                    store.Delete(userId, id);
                    removed++;
                }
            }
        }

        return removed;
    }
}

/// <summary>Runs <see cref="ContactHousekeeping.MaintainAsync"/> every hour.</summary>
public sealed class ContactPhotoWorker(IServiceScopeFactory scopes, ContactPhotoStore store, ILogger<ContactPhotoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var removed = await ContactHousekeeping.MaintainAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), store, stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation("Contacts housekeeping: {Count} unused pictures removed.", removed);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Contacts housekeeping failed; it runs again in an hour.");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the app stops
        }
    }
}