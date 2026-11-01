using System.Text.RegularExpressions;

namespace M3uPlayer.Core.Text;

public static partial class Redactor
{
    [GeneratedRegex(@"(?<=[?&][^=&\s]+=)[^&\s]*")]
    private static partial Regex QueryValue();

    [GeneratedRegex(@"(?<=://)[^/@\s]+@")]
    private static partial Regex UserInfo();

    // Xtream-style paths carry username and password as the two segments after the type.
    [GeneratedRegex(@"(?<=/(?:live|timeshift|movie|series)/)[^/\s]+/[^/\s]+/")]
    private static partial Regex PathCredentials();

    public static string Redact(string text)
    {
        text = QueryValue().Replace(text, "***");
        text = UserInfo().Replace(text, "***@");
        return PathCredentials().Replace(text, "***/***/");
    }
}
