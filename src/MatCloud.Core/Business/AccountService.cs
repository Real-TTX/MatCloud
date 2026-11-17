using MatCloud.Contract;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>The account as its owner sees it.</summary>
public sealed record AccountView(string UserName, string DisplayName, string Theme, bool IsAdmin, bool IsExternal, bool HasPassword, DateTime CreateDate);

/// <summary>What a person changes about their own account: the name, the theme, the password.</summary>
public static class AccountService
{
    public static async Task<Outcome<AccountView>> GetAsync(AppDbContext db, Actor actor)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actor.UserId);
        return user is null
            ? Outcome<AccountView>.Missing()
            : Outcome<AccountView>.Success(new AccountView(
                user.UserName, user.DisplayName, user.Theme, user.IsAdmin, user.ExternalSubject is not null, !string.IsNullOrEmpty(user.PasswordHash), user.CreateDate));
    }

    /// <summary>The account as a device sees it (GET /api/v1/account).</summary>
    public static async Task<Outcome<AccountInfo>> InfoAsync(AppDbContext db, Actor actor, string serverVersion)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actor.UserId);
        return user is null
            ? Outcome<AccountInfo>.Missing()
            : Outcome<AccountInfo>.Success(new AccountInfo(user.UserName, user.DisplayName, actor.DeviceName ?? string.Empty, actor.App ?? string.Empty, serverVersion));
    }

    /// <summary>The tab "Allgemein": the name.</summary>
    public static async Task<Outcome> SaveGeneralAsync(AppDbContext db, Actor actor, string displayName)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return Outcome.Fail("Bitte einen Namen eingeben.", "DisplayName");
        }

        if (name.Length > 120)
        {
            return Outcome.Fail("Der Name hat höchstens 120 Zeichen.", "DisplayName");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        user.DisplayName = name;
        await db.SaveChangesAsync();
        SessionService.Invalidate();
        return Outcome.Success();
    }

    public static async Task SetThemeAsync(AppDbContext db, Actor actor, string theme)
    {
        var value = theme is "light" or "dark" ? theme : "system";
        await db.Users.Where(u => u.Id == actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, value));
        SessionService.Invalidate();
    }

    /// <summary>
    /// A new password: the current one is checked (an account of MatIdentity without a password sets its first one without it); the other sessions of
    /// the browser end, the devices stay signed in.
    /// </summary>
    public static async Task<Outcome> ChangePasswordAsync(AppDbContext db, Actor actor, string current, string password, string repeat, long? currentSessionId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);
        if (hasPassword && !Passwords.Verify(user, current))
        {
            return Outcome.Fail("Das aktuelle Passwort stimmt nicht.", "Current");
        }

        if (Passwords.Problem(password, repeat) is { } problem)
        {
            return Outcome.Fail(problem, problem.Contains("überein", StringComparison.Ordinal) ? "Repeat" : "New");
        }

        if (hasPassword && password == current)
        {
            return Outcome.Fail("Das neue Passwort muss sich vom aktuellen unterscheiden.", "New");
        }

        user.PasswordHash = Passwords.Hash(user, password);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        await SessionService.RevokeAllAsync(db, user.Id, currentSessionId);
        return Outcome.Success();
    }
}
