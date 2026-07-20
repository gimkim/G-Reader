namespace CDisplayEx.CSharp;

internal static class CommandLineOptions
{
    private static readonly string[] PathOptions =
    [
        "--open", "--file", "--folder", "-o", "/open", "/file", "/folder"
    ];

    public static string? GetInitialPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = Clean(args[index]);
            if (string.IsNullOrWhiteSpace(argument)) continue;

            var optionValue = GetInlineOptionValue(argument);
            if (optionValue is not null) return NormalizePath(optionValue);

            if (PathOptions.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                if (++index < args.Count) return NormalizePath(Clean(args[index]));
                return null;
            }

            if (argument.StartsWith('-')) continue;
            return NormalizePath(argument);
        }
        return null;
    }

    private static string? GetInlineOptionValue(string argument)
    {
        foreach (var option in PathOptions.Where(option => option.StartsWith('-')))
        {
            var prefix = option + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Clean(argument[prefix.Length..]);
        }
        return null;
    }

    private static string Clean(string value) => value.Trim().Trim('"');

    private static string NormalizePath(string value)
    {
        value = Environment.ExpandEnvironmentVariables(Clean(value));
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            value = uri.LocalPath;
        try { return Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }
}
