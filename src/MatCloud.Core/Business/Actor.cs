namespace MatCloud.Core.Business;

/// <summary>
/// Who asks the business layer to do something: a person signed in in the browser, or a device of an app (then <see cref="DeviceId"/> and
/// <see cref="App"/> are set: a device reads and writes only the data of its app). Every function of the business layer takes it and decides
/// with it what is allowed.
/// </summary>
public sealed record Actor(long UserId, bool IsAdmin, long? DeviceId = null, string? DeviceName = null, string? App = null)
{
    /// <summary>The name what this actor writes is marked with: the device, or "Web".</summary>
    public string Source => DeviceName ?? WebSource;

    public const string WebSource = "Web";

    /// <summary>
    /// A device of another app than <paramref name="app"/> may not touch its data (the browser of the account may) – unless the module of
    /// <paramref name="app"/> grants it to the device's app (<see cref="Modules.ICloudModule.GuestApps"/>: ThixWrite in the Drive). Whether the app is
    /// switched on is a question of its own (<see cref="AppService.IsEnabledAsync"/>).
    /// </summary>
    public bool MayUse(string app) => DeviceId is null || App == app || Modules.CloudModules.IsGuest(app, App);
}

/// <summary>The answer of a change in the business layer: what came out of it, or what is wrong (already worded for the person, in German).</summary>
public sealed record Outcome<T>(T? Value, string? Error, string? Field = null, bool NotFound = false)
{
    public bool Ok => Error is null && !NotFound;

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Fail(string error, string? field = null) => new(default, error, field);

    /// <summary>The record does not exist or the actor may not see it: both answer the same (404), so nothing can be probed.</summary>
    public static Outcome<T> Missing() => new(default, null, null, true);
}

/// <summary>An outcome without a value.</summary>
public sealed record Outcome(string? Error, string? Field = null, bool NotFound = false)
{
    public bool Ok => Error is null && !NotFound;

    public static Outcome Success() => new((string?)null);

    public static Outcome Fail(string error, string? field = null) => new(error, field);

    public static Outcome Missing() => new(null, null, true);
}

/// <summary>Raised when an actor asks for something only an administrator may do (the pages are guarded too; this is the check of the business layer).</summary>
public sealed class NotAllowedException(string message) : Exception(message);
