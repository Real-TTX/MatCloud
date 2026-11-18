// What the copied providers of ThixCalendar (ThixCalendar/, a copy) use of ThixCalendar's infrastructure, standing in for it on the server.
namespace ThixCalendar.Core.Infrastructure;

/// <summary>The log of the providers: into MatCloud's log (the worker sets <see cref="Sink"/>). They never log tokens or event contents.</summary>
internal static class Log
{
    public static Action<string>? Sink { get; set; }

    /// <summary>Writes a warning; a log that fails (a logger of a host that has stopped) never breaks the sync.</summary>
    public static void Warning(string message)
    {
        try
        {
            Sink?.Invoke(message);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // the log is gone; the warning with it
        }
    }
}

/// <summary>What the providers send as user agent ("ThixCalendar/MatCloud").</summary>
internal static class AppInfo
{
    public const string BuildNumber = "MatCloud";
}
