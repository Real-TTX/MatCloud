using MatCloud.Apps.Contacts.Business;

namespace MatCloud.Apps.Contacts.Pages.Contacts;

/// <summary>"Dubletten": the sets of contacts that look like the same person (the same name, e-mail address or phone number), each to merge.</summary>
public sealed class DuplicatesModel : ContactsPageModel
{
    public IReadOnlyList<IReadOnlyList<ContactEntry>> Sets { get; private set; } = [];

    public async Task OnGetAsync() => Sets = await ContactBook.DuplicatesAsync(Db, Actor, Ct);
}
