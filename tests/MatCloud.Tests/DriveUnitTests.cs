using MatCloud.Apps.Drive.Business;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using MatCloud.Core.Business;
using MatCloud.Core.Modules;
using MatCloud.Core.Web;

namespace MatCloud.Tests;

/// <summary>The rules of the Drive that need no database: names, ETags, types, the grant to the office apps.</summary>
public sealed class DriveUnitTests
{
    [Theory]
    [InlineData("Angebot.docx", "Angebot.docx")]
    [InlineData("  Leerzeichen vorn.txt", "Leerzeichen vorn.txt")]
    [InlineData("Umlaute äöü ß.odt", "Umlaute äöü ß.odt")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    public void Good_names_are_kept(string name, string kept) => Assert.Equal(kept, DriveNames.Check(name).Name);

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\b")]
    [InlineData("..")]
    [InlineData("x:y")]
    [InlineData("frage?")]
    [InlineData("ende.")]
    [InlineData("ende ")]
    [InlineData("COM1.txt")]
    [InlineData("aux")]
    [InlineData("")]
    public void Bad_names_are_refused_never_changed(string name)
    {
        var (kept, problem) = DriveNames.Check(name);
        Assert.Null(kept);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void A_numbered_name_keeps_its_extension_and_its_length()
    {
        Assert.Equal("Brief (2).docx", DriveNames.Numbered("Brief.docx", DriveItemKind.File, 2));
        Assert.Equal("Ordner.alt (3)", DriveNames.Numbered("Ordner.alt", DriveItemKind.Folder, 3));
        Assert.Equal(DriveContract.MaxNameLength, DriveNames.Numbered(new string('a', 250) + ".docx", DriveItemKind.File, 12).Length);
    }

    [Theory]
    [InlineData("\"42\"", true)]
    [InlineData("W/\"42\"", true)]
    [InlineData("42", true)]
    [InlineData("\"7\", \"42\"", true)]
    [InlineData("*", true)]
    [InlineData("\"41\"", false)]
    [InlineData("\"420\"", false)]
    public void If_match_names_the_current_version(string header, bool matches) => Assert.Equal(matches, DriveFiles.Matches(header, 42));

    [Fact]
    public void Office_files_are_documents_tables_and_presentations_of_the_office_apps()
    {
        Assert.Equal(CloudApps.Write, DriveTypes.OfficeAppOf(DriveTypes.CategoryOf(DriveItemKind.File, "a.DOCX")));
        Assert.Equal(CloudApps.Write, DriveTypes.OfficeAppOf(DriveTypes.CategoryOf(DriveItemKind.File, "a.odt")));
        Assert.Equal(CloudApps.Calc, DriveTypes.OfficeAppOf(DriveTypes.CategoryOf(DriveItemKind.File, "a.xlsx")));
        Assert.Equal(CloudApps.Show, DriveTypes.OfficeAppOf(DriveTypes.CategoryOf(DriveItemKind.File, "a.pptx")));
        Assert.Equal("application/vnd.oasis.opendocument.text", DriveTypes.ContentTypeOf("a.odt"));
        Assert.Equal("application/octet-stream", DriveTypes.ContentTypeOf("a.unbekannt"));
        Assert.Equal(DriveTypes.Folder, DriveTypes.CategoryOf(DriveItemKind.Folder, "a.docx"));
        Assert.Empty(DriveOpeners.For(1, "a.docx"));
    }

    [Fact]
    public void The_grant_lets_exactly_the_office_apps_into_the_Drive_and_nowhere_else()
    {
        Assert.Equal([CloudApps.Write, CloudApps.Calc, CloudApps.Show], DriveContract.GuestApps);
        foreach (var app in CloudApps.All.Select(a => a.Id))
        {
            var device = new Actor(1, false, 2, "PC", app);
            Assert.Equal(app is CloudApps.Drive or CloudApps.Write or CloudApps.Calc or CloudApps.Show, device.MayUse(CloudApps.Drive));
            Assert.Equal(app == CloudApps.Clip, device.MayUse(CloudApps.Clip));
            Assert.Equal(app == CloudApps.Note, device.MayUse(CloudApps.Note));
        }

        Assert.True(new Actor(1, false).MayUse(CloudApps.Drive));
        Assert.False(CloudModules.IsGuest(CloudApps.Clip, CloudApps.Write));
    }

    [Fact]
    public void Every_new_app_has_its_brand()
    {
        foreach (var app in new[] { CloudApps.Drive, CloudApps.Write, CloudApps.Calc, CloudApps.Show, CloudApps.Space })
        {
            var brand = AppBrands.Of(app);
            Assert.False(brand.IsCloud);
            Assert.StartsWith("Thix", brand.Name);
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MatCloud", "wwwroot", brand.Logo.TrimStart('/'))), brand.Logo);
        }

        Assert.Equal("/Drive", AppBrands.Of(CloudApps.Drive).Home);
    }
}
