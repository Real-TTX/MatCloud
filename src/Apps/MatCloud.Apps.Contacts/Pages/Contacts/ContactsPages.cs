using System.Net;
using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract;
using MatCloud.Contract.Contacts;
using MatCloud.Core.Business;
using MatCloud.Core.Controls;
using MatCloud.Core.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>What every page of the contacts has: the store of the pictures, and a 404 when the app is switched off (the business layer checks again).</summary>
public abstract class ContactsPageModel : AppPageModel
{
    protected ContactPhotoStore Photos => HttpContext.RequestServices.GetRequiredService<ContactPhotoStore>();

    protected CancellationToken Ct => HttpContext.RequestAborted;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await AppService.IsEnabledAsync(Db, CloudApps.Contacts, HttpContext.RequestAborted))
        {
            context.Result = NotFound();
            return;
        }

        try
        {
            await next();
        }
        catch (NotAllowedException)
        {
            context.Result = NotFound();
        }
    }
}

/// <summary>One address row of the form (the partial <c>_AddressRow</c>).</summary>
public sealed record AddressRow(string Label, string Street, string PostalCode, string City, string Region, string Country, string? Error);

/// <summary>What the pages say about contacts: avatars, labels, dates, the choices of the forms.</summary>
public static class ContactsUi
{
    public static IReadOnlyList<Choice> EmailLabels { get; } = ContactLabels.Email.Select(l => new Choice(l, ContactLabels.NameOf(l))).ToList();

    public static IReadOnlyList<Choice> PhoneLabels { get; } = ContactLabels.Phone.Select(l => new Choice(l, ContactLabels.NameOf(l))).ToList();

    public static IReadOnlyList<Choice> AddressLabels { get; } = ContactLabels.Address.Select(l => new Choice(l, ContactLabels.NameOf(l))).ToList();

    public static IReadOnlyList<Choice> UrlLabels { get; } = ContactLabels.Url.Select(l => new Choice(l, ContactLabels.NameOf(l))).ToList();

    /// <summary>The choices of a label, with a label of the user's own added when the value has one.</summary>
    public static IReadOnlyList<Choice> LabelsWith(IReadOnlyList<Choice> labels, string current)
        => labels.Any(l => l.Value == current) ? labels : [.. labels, new Choice(current, ContactLabels.NameOf(current))];

    /// <summary>The round picture of a contact, or its initials on the accent.</summary>
    public static IHtmlContent Avatar(ContactData contact, string size = "")
    {
        var cls = "mat-avatar mc-contact-avatar" + (size.Length > 0 ? " mat-avatar--" + size : string.Empty);
        if (contact.PhotoId.Length > 0 && ContactsContract.IsValidPhotoId(contact.PhotoId))
        {
            return new HtmlString($"<span class=\"{cls}\" aria-hidden=\"true\"><img src=\"/live/contacts/photo/{contact.PhotoId}\" alt=\"\" loading=\"lazy\" /></span>");
        }

        return new HtmlString($"<span class=\"{cls}\" aria-hidden=\"true\">{WebUtility.HtmlEncode(Initials(contact))}</span>");
    }

    /// <summary>The initials: the first letters of the given and the family name, else of the name to show.</summary>
    public static string Initials(ContactData contact)
    {
        var given = contact.GivenName.Trim();
        var family = contact.FamilyName.Trim();
        if (given.Length > 0 || family.Length > 0)
        {
            return string.Concat(new[] { given, family }.Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0])));
        }

        return Ui.Initials(contact.NameToShow());
    }

    /// <summary>"1. Mai 1980" / "1. Mai" for a date of a contact.</summary>
    public static string DateText(string date)
    {
        if (!ContactsContract.TryParseDate(date, out var year, out var month, out var day))
        {
            return date;
        }

        string[] months = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
        return year is null ? $"{day}. {months[month - 1]}" : $"{day}. {months[month - 1]} {year}";
    }

    /// <summary>A date as the form shows it ("01.05.1980", "01.05.").</summary>
    public static string DateInput(string date)
    {
        if (!ContactsContract.TryParseDate(date, out var year, out var month, out var day))
        {
            return date;
        }

        return year is null ? $"{day:00}.{month:00}." : $"{day:00}.{month:00}.{year:0000}";
    }

    /// <summary>What the person typed as a date ("1.5.1980", "01.05.", "1980-05-01") in the stored form; null when it is not a date, empty when nothing.</summary>
    public static string? ParseDateInput(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var parsed = ContactCsv.Date(value);
        return parsed.Length > 0 ? parsed : null;
    }

    /// <summary>The address of a contact as lines (street lines, "PLZ Ort", region, country).</summary>
    public static IEnumerable<string> AddressLines(ContactAddress address)
    {
        foreach (var line in address.Street.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return line;
        }

        var city = string.Join(' ', new[] { address.PostalCode, address.City }.Where(p => p.Length > 0));
        if (city.Length > 0)
        {
            yield return city;
        }

        if (address.Region.Length > 0)
        {
            yield return address.Region;
        }

        if (address.Country.Length > 0)
        {
            yield return address.Country;
        }
    }

    /// <summary>"Firma · Position" for a line under the name.</summary>
    public static string Work(ContactData contact)
        => string.Join(" · ", new[] { contact.JobTitle, contact.Company }.Where(p => p.Length > 0));
}
