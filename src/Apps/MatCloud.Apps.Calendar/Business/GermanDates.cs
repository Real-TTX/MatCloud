namespace MatCloud.Apps.Calendar.Business;

/// <summary>German names of days and months (the server runs without culture data: CultureInfo("de-DE") is not there).</summary>
public static class GermanDates
{
    private static readonly string[] Days = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];

    private static readonly string[] Months = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

    /// <summary>"Mo" … "So".</summary>
    public static string DayShort(DateTime day) => Days[(int)day.DayOfWeek][..2];

    /// <summary>"Montag, 17. November 2026" (without the year: "Montag, 17. November").</summary>
    public static string Long(DateTime day, bool year = true) => $"{Days[(int)day.DayOfWeek]}, {day.Day}. {Months[day.Month - 1]}{(year ? " " + day.Year : string.Empty)}";

    /// <summary>"17. Nov." (with the year: "17. Nov. 2026"; May stays "Mai").</summary>
    public static string Short(DateTime day, bool year = false) => $"{day.Day}. {MonthShort(day.Month)}{(year ? " " + day.Year : string.Empty)}";

    /// <summary>"November 2026".</summary>
    public static string MonthYear(DateTime day) => $"{Months[day.Month - 1]} {day.Year}";

    private static string MonthShort(int month) => Months[month - 1].Length <= 4 ? Months[month - 1] : Months[month - 1][..3] + ".";
}
