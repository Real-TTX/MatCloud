using MatCloud.Apps.Contacts.Business;
using MatCloud.Core.Controls;
using Microsoft.AspNetCore.Mvc;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>
/// "Importieren": a vCard file (3.0, 4.0, also 2.1) is imported at once; a CSV file shows its columns first, each matched to a field (Outlook's and Google's
/// names are known), with the first rows as examples – then it is imported. Duplicates of contacts the account has can be left out.
/// </summary>
public sealed class ImportModel : ContactsPageModel
{
    public CsvTable? Table { get; private set; }

    public IReadOnlyList<string> Mapping { get; private set; } = [];

    public string? Token { get; private set; }

    public static IReadOnlyList<Choice> FieldChoices { get; } =
        [new Choice(ContactCsv.Ignore, "– nicht übernehmen –"), .. ContactCsv.Fields.Select(f => new Choice(f.Id, f.Header))];

    [BindProperty] public IFormFile? Upload { get; set; }

    [BindProperty] public bool SkipDuplicates { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(string? token, bool? skip)
    {
        if (token is null)
        {
            return Page();
        }

        var file = await ContactImports.ReadAsync(Db, Actor, Photos, token, Ct);
        if (!file.Ok)
        {
            FlashError("Die Datei ist nicht mehr da. Bitte noch einmal wählen.");
            return Redirect("/Contacts/Import");
        }

        Token = token;
        SkipDuplicates = skip ?? true;
        Table = ContactCsv.Read(file.Value!);
        Mapping = ContactCsv.AutoMap(Table.Columns);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Upload is not { Length: > 0 })
        {
            AddError(nameof(Upload), "Bitte eine Datei wählen (.vcf oder .csv).");
            return Page();
        }

        if (Upload.Length > ContactImports.MaxFileBytes)
        {
            AddError(nameof(Upload), $"Eine Datei hat höchstens {ContactImports.MaxFileBytes / 1024 / 1024} MB.");
            return Page();
        }

        using var stream = new MemoryStream();
        await Upload.CopyToAsync(stream, Ct);
        var bytes = stream.ToArray();
        if (ContactImports.KindOf(Upload.FileName, bytes) == ImportKind.VCard)
        {
            var contacts = VCard.Read(bytes);
            if (contacts.Count == 0)
            {
                AddError(nameof(Upload), "In dieser Datei steht keine vCard mit einem Namen, einer Firma oder einer E-Mail-Adresse.");
                return Page();
            }

            return Done(await ContactBook.ImportAsync(Db, Actor, Photos, contacts, SkipDuplicates, Ct));
        }

        var table = ContactCsv.Read(bytes);
        if (table.Columns.Count == 0 || table.Rows.Count == 0)
        {
            AddError(nameof(Upload), "Diese Datei ist weder eine vCard noch eine CSV-Datei mit einer Kopfzeile und Zeilen darunter.");
            return Page();
        }

        var kept = await ContactImports.KeepAsync(Db, Actor, Photos, bytes, Ct);
        if (!kept.Ok)
        {
            AddError(nameof(Upload), kept.Error);
            return Page();
        }

        return Redirect($"/Contacts/Import?token={kept.Value}&skip={(SkipDuplicates ? "true" : "false")}");
    }

    /// <summary>The CSV with the columns as the person matched them.</summary>
    public async Task<IActionResult> OnPostMapAsync(string? token, string[] mapping)
    {
        var file = await ContactImports.ReadAsync(Db, Actor, Photos, token, Ct);
        if (!file.Ok)
        {
            FlashError("Die Datei ist nicht mehr da. Bitte noch einmal wählen.");
            return Redirect("/Contacts/Import");
        }

        var table = ContactCsv.Read(file.Value!);
        var allowed = ContactCsv.Fields.Select(f => f.Id).ToHashSet();
        var clean = mapping.Select(m => allowed.Contains(m ?? string.Empty) ? m! : ContactCsv.Ignore).ToList();
        if (clean.All(m => m.Length == 0))
        {
            FlashError("Bitte mindestens eine Spalte einem Feld zuordnen.");
            return Redirect($"/Contacts/Import?token={token}&skip={(SkipDuplicates ? "true" : "false")}");
        }

        var result = await ContactBook.ImportAsync(Db, Actor, Photos, ContactCsv.Import(table, clean), SkipDuplicates, Ct);
        ContactImports.Forget(Photos, Actor, token);
        return Done(result);
    }

    private RedirectResult Done(ImportResult result)
    {
        var text = result.Imported == 1 ? "1 Kontakt importiert" : $"{result.Imported} Kontakte importiert";
        Flash(result.Skipped > 0 ? $"{text}, {result.Skipped} übersprungen (Dubletten oder ohne Namen)." : text + ".");
        return Redirect("/Contacts");
    }
}
