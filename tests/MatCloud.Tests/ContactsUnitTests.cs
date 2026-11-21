using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCloud.Apps.Contacts.Business;
using MatCloud.Contract;
using MatCloud.Contract.Contacts;

namespace MatCloud.Tests;

/// <summary>
/// The rules of ThixContacts that need no database: vCard and CSV against the shared fixtures (Fixtures/Contacts – the same files are in ThixOffice's
/// tests/ThixOffice.Pim.Tests, so MatCloud and ThixContacts read and write alike), validation, duplicates and merging.
/// Set THIXCONTACTS_FIXTURES_WRITE=1 to write the expected files anew (then LOOK at the diff).
/// </summary>
public sealed class ContactsUnitTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contacts");

    private static readonly JsonSerializerOptions Pretty = new(CloudContract.Json) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TheoryData<string> VCardFiles() => ["outlook-3.0.vcf", "apple-3.0.vcf", "google-4.0.vcf", "legacy-2.1.vcf"];

    public static TheoryData<string> CsvFiles() => ["outlook-de.csv", "outlook-en.csv"];

    [Theory]
    [MemberData(nameof(VCardFiles))]
    public void A_vcard_file_reads_as_its_fixture_says(string file)
    {
        var contacts = VCard.Read(File.ReadAllBytes(Path.Combine(Folder, file)));
        var actual = JsonSerializer.Serialize(contacts.Select(c => c.Data), Pretty);
        Expect(file + ".expected.json", actual);
    }

    [Theory]
    [MemberData(nameof(CsvFiles))]
    public void A_csv_file_maps_and_reads_as_its_fixture_says(string file)
    {
        var table = ContactCsv.Read(File.ReadAllBytes(Path.Combine(Folder, file)));
        var mapping = ContactCsv.AutoMap(table.Columns);
        var contacts = ContactCsv.Import(table, mapping);
        var actual = JsonSerializer.Serialize(new { mapping, contacts = contacts.Select(c => c.Data) }, Pretty);
        Expect(file + ".expected.json", actual);
    }

    [Fact]
    public void Contacts_are_written_as_the_fixtures_say_and_read_back_the_same()
    {
        var written = Written();
        Expect("written-3.0.vcf", VCard.Write(written, 3));
        Expect("written-4.0.vcf", VCard.Write(written, 4));
        Expect("written.csv", Encoding.UTF8.GetString(ContactCsv.Write(written.Select(w => w.Data))).TrimStart('﻿'));

        foreach (var version in new[] { 3, 4 })
        {
            var back = VCard.Read(VCard.Write(written, version));
            Assert.Equal(written.Count, back.Count);
            for (var i = 0; i < written.Count; i++)
            {
                var expected = ContactRules.Normalize(written[i].Data with
                {
                    PhotoId = written[i].Photo is { } photo ? Convert.ToHexStringLower(SHA256.HashData(photo)) : string.Empty,
                });
                Assert.Equal(JsonSerializer.Serialize(expected, CloudContract.Json), JsonSerializer.Serialize(back[i].Data, CloudContract.Json));
                Assert.Equal(written[i].Key, back[i].Uid);
                Assert.Equal(written[i].Photo, back[i].Photo);
            }
        }
    }

    [Fact]
    public void Lines_are_folded_at_75_bytes_without_breaking_a_character()
    {
        var text = VCard.Write([new ExportedContact("k", new ContactData { GivenName = "Ä", Notes = string.Concat(Enumerable.Repeat("äöü€ ", 60)) })]);
        foreach (var line in text.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        }

        Assert.Equal(string.Concat(Enumerable.Repeat("äöü€ ", 60)).Trim(), VCard.Read(text)[0].Data.Notes);
    }

    [Fact]
    public void The_rules_refuse_what_is_not_a_contact_and_name_the_field()
    {
        Assert.Equal("GivenName", ContactRules.Validate(ContactRules.Normalize(new ContactData { Notes = "nur Notiz" })).Single().Field);
        var bad = ContactRules.Normalize(new ContactData
        {
            GivenName = "Anna",
            Emails = [new ContactValue("work", "ok@example.com"), new ContactValue("work", "kein-at.example")],
            Phones = [new ContactValue("mobile", "0171 abc")],
            Urls = [new ContactValue("work", "ftp://x.example")],
            Birthday = "1980-02-30",
            Categories = ["a,b"],
        });
        Assert.Equal(["Emails[1]", "Phones[0]", "Urls[0]", "Birthday", "Categories"], ContactRules.Validate(bad).Select(p => p.Field));
        Assert.Empty(ContactRules.Validate(ContactRules.Normalize(new ContactData { Emails = [new ContactValue("", "  a@b.de ")], Urls = [new ContactValue("", "example.com")] })));
        Assert.Equal("https://example.com", ContactRules.Normalize(new ContactData { Urls = [new ContactValue("", "example.com")] }).Urls[0].Value);
        Assert.True(ContactsContract.IsValidDate("--02-29"));
        Assert.False(ContactsContract.IsValidDate("1981-02-29"));
    }

    [Fact]
    public void Folding_sorting_and_searching_ignore_case_and_accents()
    {
        Assert.Equal("jorg muller strasse", ContactRules.Fold("  Jörg   MÜLLER Straße "));
        var contact = new ContactData { GivenName = "Jörg", FamilyName = "Müller", Phones = [new ContactValue("mobile", "+49 (171) 234-5678")] };
        Assert.True(ContactRules.Matches(contact, "joerg") == false && ContactRules.Matches(contact, "jorg mull"));
        Assert.True(ContactRules.Matches(contact, "2345678"));
        Assert.Equal("M", ContactRules.IndexLetter(contact, ContactSort.FamilyName));
        Assert.Equal("Müller, Jörg", ContactRules.ListName(contact, ContactSort.FamilyName));
        Assert.Equal("#", ContactRules.IndexLetter(new ContactData { Company = "3M" }, ContactSort.Company));
    }

    [Fact]
    public void Duplicates_are_found_by_name_mail_or_phone_and_join_into_sets()
    {
        var contacts = new List<(string, ContactData)>
        {
            ("a", new ContactData { GivenName = "Anna", FamilyName = "Müller" }),
            ("b", new ContactData { GivenName = "anna", FamilyName = "Mueller", Emails = [new ContactValue("work", "anna@x.de")] }),
            ("c", new ContactData { GivenName = "ANNA", FamilyName = "MÜLLER" }),
            ("d", new ContactData { Company = "X", Emails = [new ContactValue("work", "Anna@X.de")] }),
            ("e", new ContactData { GivenName = "Bob", Phones = [new ContactValue("work", "+49 89 1234567")] }),
            ("f", new ContactData { GivenName = "Robert", Phones = [new ContactValue("mobile", "089 1234567")] }),
            ("g", new ContactData { GivenName = "Al" }),
            ("h", new ContactData { GivenName = "Al" }),
        };
        var sets = ContactDuplicates.Find(contacts);
        Assert.Equal(3, sets.Count);
        Assert.Equal(["a", "c"], sets.Single(s => s.Contains("a")));
        Assert.Equal(["b", "d"], sets.Single(s => s.Contains("b")));

        // 089 1234567 and +49 89 1234567 are one number
        Assert.Equal(["e", "f"], sets.Single(s => s.Contains("e")));
        Assert.DoesNotContain(sets, s => s.Contains("g"));
        Assert.Empty(ContactDuplicates.Find([("e", contacts[4].Item2), ("f", contacts[5].Item2 with { Phones = [new ContactValue("mobile", "089 7654321")] })]));
    }

    [Fact]
    public void Merging_takes_the_chosen_values_and_joins_the_lists()
    {
        var first = new ContactData { GivenName = "Anna", Emails = [new ContactValue("work", "a@x.de")], Categories = ["Kunde"], Notes = "eins" };
        var second = new ContactData
        {
            GivenName = "Anna", FamilyName = "Müller", JobTitle = "Chefin", Emails = [new ContactValue("home", "A@X.de"), new ContactValue("home", "b@x.de")],
            Categories = ["kunde", "Wichtig"], Notes = "zwei", IsFavorite = true, Birthday = "--01-02",
        };
        var merged = ContactDuplicates.Merge([first, second]);
        Assert.Equal(("Anna", "", "Chefin", "--01-02"), (merged.GivenName, merged.FamilyName, merged.JobTitle, merged.Birthday));
        Assert.Equal(["a@x.de", "b@x.de"], merged.Emails.Select(e => e.Value));
        Assert.Equal(["Kunde", "Wichtig"], merged.Categories);
        Assert.Equal("eins\n\nzwei", merged.Notes);
        Assert.True(merged.IsFavorite);
        Assert.Equal("Müller", ContactDuplicates.Merge([first, second], new Dictionary<string, int> { ["name"] = 1 }).FamilyName);
    }

    [Fact]
    public void Unknown_fields_of_a_newer_app_survive_a_round_trip()
    {
        var json = """{"givenName":"Anna","pronouns":"sie/ihr","emails":[{"label":"work","value":"a@x.de"}]}""";
        var contact = ContactsContract.ReadContact(json)!;
        var back = JsonNode.Parse(ContactsContract.Serialize(contact with { IsFavorite = true }))!;
        Assert.Equal("sie/ihr", back["pronouns"]!.GetValue<string>());
        Assert.Null(ContactsContract.ReadContact("{kaputt"));
    }

    private static List<ExportedContact> Written()
    {
        var items = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "written.json")))!.AsArray();
        return items.Select(i => new ExportedContact(
            i!["key"]!.GetValue<string>(),
            ContactRules.Normalize(i["data"].Deserialize<ContactData>(CloudContract.Json)!),
            i["photo"] is { } photo ? Convert.FromBase64String(photo.GetValue<string>()) : null)).ToList();
    }

    /// <summary>Compares with an expected file (line ends as LF); THIXCONTACTS_FIXTURES_WRITE=1 writes it into the source folder instead.</summary>
    private static void Expect(string name, string actual)
    {
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("THIXCONTACTS_FIXTURES_WRITE") == "1")
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Contacts", name));
            File.WriteAllText(source, actual, new UTF8Encoding(false));
            return;
        }

        var path = Path.Combine(Folder, name);
        Assert.True(File.Exists(path), $"The fixture {name} is missing (THIXCONTACTS_FIXTURES_WRITE=1 writes it).");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }
}
