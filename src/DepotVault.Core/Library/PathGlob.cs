using System.IO.Enumeration;

namespace DepotVault.Core.Library;

public static class PathGlob
{
    public static bool IsMatch(string pattern, string relPath)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        var p = pattern.Trim().Replace('\\', '/').Replace("**", "*");
        var path = relPath.Replace('\\', '/');
        if (p.Contains('/'))
            return FileSystemName.MatchesSimpleExpression(p.TrimStart('/'), path, ignoreCase: true);
        return FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(path), ignoreCase: true)
            || FileSystemName.MatchesSimpleExpression(p, path, ignoreCase: true);
    }

    public static Func<string, bool> AnyOf(IEnumerable<string> patterns)
    {
        var list = patterns?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];
        if (list.Length == 0)
            return null;
        return rel =>
        {
            foreach (var p in list)
            {
                if (IsMatch(p, rel))
                    return true;
            }
            return false;
        };
    }
}
