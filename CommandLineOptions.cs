namespace CDisplayEx.CSharp;

internal static class CommandLineOptions
{
    internal readonly record struct OpenRequest(string? Path, bool ForceFullPage);

    private static readonly string[] PathOptions =
    [
        "--open", "--file", "--folder", "-o", "/open", "/file", "/folder"
    ];

    public static OpenRequest GetInitialRequest(IReadOnlyList<string> args)
    {
        var forceFullPage = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = Clean(args[index]);
            if (string.IsNullOrWhiteSpace(argument)) continue;

            if (argument.Equals("--explorer", StringComparison.OrdinalIgnoreCase))
            {
                forceFullPage = true;
                continue;
            }

            var optionValue = GetInlineOptionValue(argument);
            if (optionValue is not null) return new(NormalizePath(optionValue), forceFullPage);

            if (PathOptions.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                if (++index < args.Count) return new(NormalizePath(Clean(args[index])), forceFullPage);
                return new(null, forceFullPage);
            }

            if (argument.StartsWith('-')) continue;
            return new(NormalizePath(argument), forceFullPage);
        }
        return new(null, forceFullPage);
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
