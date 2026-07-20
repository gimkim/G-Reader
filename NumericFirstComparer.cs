using System.Text.RegularExpressions;

namespace CDisplayEx.CSharp;

internal sealed class NumericFirstComparer : IComparer<string>
{
    public static NumericFirstComparer Instance { get; } = new();
    private static readonly Regex FirstNumber = new(@"\d+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var xName = Path.GetFileNameWithoutExtension(x);
        var yName = Path.GetFileNameWithoutExtension(y);
        var xm = FirstNumber.Match(xName);
        var ym = FirstNumber.Match(yName);
        if (xm.Success && ym.Success && long.TryParse(xm.Value, out var xn) && long.TryParse(ym.Value, out var yn))
        {
            var numeric = xn.CompareTo(yn);
            if (numeric != 0) return numeric;
            return NaturalStringComparer.Instance.Compare(xName, yName);
        }
        if (xm.Success != ym.Success) return xm.Success ? -1 : 1;
        return StringComparer.CurrentCultureIgnoreCase.Compare(xName, yName);
    }
}
