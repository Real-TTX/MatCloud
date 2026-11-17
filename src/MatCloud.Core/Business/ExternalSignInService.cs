using System.Security.Cryptography;
using System.Text;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Business;

/// <summary>A person as MatIdentity names them: the subject (<c>sub</c>), the address, the names and the role claim of MatCloud (or null).</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The outcome of a sign-in through MatIdentity: the account to sign in, or the id of the account with the same user name that has to prove itself with
/// its password before it is linked (<see cref="LinkUserId"/>), or the reason for a refusal.
/// </summary>
public sealed record ExternalSignInResult(User? User, long? LinkUserId, string? Error)
{
    public static ExternalSignInResult SignedIn(User user) => new(user, null, null);

    public static ExternalSignInResult LinkRequired(long userId) => new(null, userId, null);

    public static ExternalSignInResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// How a person signed in by MatIdentity becomes an account of MatCloud (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"): found by
/// <see cref="User.ExternalSubject"/>; else, when a local account has the user name MatIdentity names (<c>preferred_username</c>, else the part of the
/// address before the @), that account is <b>not</b> entered: it is linked only after its own sign-in with its password (<see cref="LinkAsync"/>), since
/// a name proves nothing; else a new account is made (without a password). The claim <c>role</c> (<c>admin</c> or <c>user</c>) sets the role at every
/// sign-in, except that the last active administrator is never demoted; without the claim MatCloud keeps its own roles (the very first account is an
/// administrator).
/// </summary>
public static class ExternalSignInService
{
    public static async Task<ExternalSignInResult> SignInAsync(AppDbContext db, ExternalIdentity identity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity hat keine Person genannt (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (user is not null)
        {
            if (!user.IsActive)
            {
                return ExternalSignInResult.Refused("Dieses Konto ist in der MatCloud deaktiviert.");
            }

            await ApplyRoleAsync(db, user, identity.Role, ct);
            user.LastLoginDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ExternalSignInResult.SignedIn(user);
        }

        var wanted = WantedUserName(identity);
        if (wanted is not null)
        {
            var match = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == wanted, ct);
            if (match is not null && match.ExternalSubject is null && !string.IsNullOrEmpty(match.PasswordHash))
            {
                if (!match.IsActive)
                {
                    return ExternalSignInResult.Refused("Dieses Konto ist in der MatCloud deaktiviert.");
                }

                // Not linked by the name alone: the person proves the account with its password first.
                return ExternalSignInResult.LinkRequired(match.Id);
            }
        }

        return ExternalSignInResult.SignedIn(await CreateAsync(db, subject, wanted, identity, ct));
    }

    /// <summary>
    /// Links the account <paramref name="userId"/> with the person of <paramref name="identity"/> after the account proved itself with its password. Refused
    /// when the account is disabled or linked meanwhile, or when another account has the subject by now.
    /// </summary>
    public static async Task<ExternalSignInResult> LinkAsync(AppDbContext db, long userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity hat keine Person genannt (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return ExternalSignInResult.Refused("Dieses Konto ist in der MatCloud deaktiviert.");
        }

        if (user.ExternalSubject is not null || await db.Users.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject, ct))
        {
            return ExternalSignInResult.Refused("Dieses Konto ist schon mit MatIdentity verbunden.");
        }

        user.ExternalSubject = subject;
        await ApplyRoleAsync(db, user, identity.Role, ct);
        await db.SaveChangesAsync(ct);
        SessionService.Invalidate();
        return ExternalSignInResult.SignedIn(user);
    }

    /// <summary>The user name MatIdentity suggests (normalised, valid), or null.</summary>
    private static string? WantedUserName(ExternalIdentity identity)
    {
        foreach (var candidate in new[] { identity.PreferredUserName, identity.Email?.Split('@')[0] })
        {
            var name = UserService.NormalizeUserName(candidate);
            if (name.Length > 0 && UserService.UserNameProblem(name) is null)
            {
                return name;
            }
        }

        return null;
    }

    private static async Task<User> CreateAsync(AppDbContext db, string subject, string? wanted, ExternalIdentity identity, CancellationToken ct)
    {
        var first = !await db.Users.IgnoreQueryFilters().AnyAsync(ct);
        var name = wanted ?? "id-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))[..12].ToLowerInvariant();
        var userName = name;
        for (var i = 2; await db.Users.AnyAsync(u => u.UserName == userName, ct); i++)
        {
            var suffix = "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            userName = (name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] : name) + suffix;
        }

        var display = new[] { identity.Name, identity.PreferredUserName, userName }.First(v => !string.IsNullOrWhiteSpace(v))!.Trim();
        var user = new User
        {
            UserName = userName,
            DisplayName = display.Length > 120 ? display[..120] : display,
            PasswordHash = string.Empty,
            IsAdmin = first,
            IsActive = true,
            ExternalSubject = subject,
            LastLoginDate = DateTime.UtcNow,
        };
        await ApplyRoleAsync(db, user, identity.Role, ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>The claim <c>role</c>: <c>admin</c> or <c>user</c> sets the role (never demoting the last active administrator); anything else, or none, leaves it.</summary>
    private static async Task ApplyRoleAsync(AppDbContext db, User user, string? claim, CancellationToken ct)
    {
        bool? admin = claim?.Trim().ToLowerInvariant() switch
        {
            "admin" => true,
            "user" => false,
            _ => null,
        };
        if (admin is not { } wanted || user.IsAdmin == wanted)
        {
            return;
        }

        if (!wanted && user.Id != 0 && user.IsActive && !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsAdmin && u.IsActive, ct))
        {
            return;
        }

        user.IsAdmin = wanted;
        SessionService.Invalidate();
    }
}
