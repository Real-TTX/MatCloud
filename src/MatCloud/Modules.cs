using MatCloud.Apps.Calendar;
using MatCloud.Apps.Clip;
using MatCloud.Apps.Drive;
using MatCloud.Apps.Note;
using MatCloud.Core.Modules;

namespace MatCloud;

/// <summary>
/// The app modules of this build (src/Apps): one line per module. A new module is a project under src/Apps (an ICloudModule), referenced in
/// MatCloud.csproj and listed here; then a migration adds its tables, if it has any (docs/MODULES.md).
/// </summary>
public static class InstalledModules
{
    public static void Install() => CloudModules.Install(new ClipModule(), new NoteModule(), new CalendarModule(), new DriveModule());
}
