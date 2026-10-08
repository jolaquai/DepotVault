using System.Globalization;
using DepotVault.Core.Library;

namespace DepotVault.Core.Import;

public enum ImportStatus
{
    New,
    Duplicate,
    Invalid,
}

public sealed record ImportRow(int Line, ulong ManifestId, DateTime DateUtc, ImportStatus Status, string Raw, string Branch = null);

public sealed class SteamDbParseResult
{
    public uint DepotId { get; init; }
    public List<ImportRow> Rows { get; } = [];
    public int NewCount => Rows.Count(r => r.Status == ImportStatus.New);
}

public static class SteamDbParser
{
    public const string DefaultBranch = "public";

    private static readonly string[] DateFormats =
    [
        "d MMMM yyyy - HH:mm:ss",
        "d MMMM yyyy - HH:mm",
        "d MMMM yyyy HH:mm:ss",
        "d MMMM yyyy",
        "d MMM yyyy - HH:mm:ss",
        "d MMM yyyy",
        "MMMM d, yyyy - HH:mm:ss",
        "MMMM d, yyyy HH:mm:ss",
        "MMMM d, yyyy",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    ];

    public static SteamDbParseResult Parse(string text, IEnumerable<ulong> existing = null)
    {
        var known = existing is null ? [] : new HashSet<ulong>(existing);
        var seen = new HashSet<ulong>();
        var result = new SteamDbParseResult { DepotId = FindDepotId(text) };
        var lineNo = 0;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            lineNo++;
            var line = raw.Trim();
            if (line.IsEmpty || line.Contains("steamdb.info", StringComparison.OrdinalIgnoreCase))
                continue;
            var (start, length) = FindIdRun(line);
            if (length == 0)
            {
                if (line.IndexOfAnyInRange('0', '9') >= 0 && LongestDigitRun(line) >= 10)
                    result.Rows.Add(new ImportRow(lineNo, 0, default, ImportStatus.Invalid, line.ToString()));
                continue;
            }
            if (!ulong.TryParse(line.Slice(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                result.Rows.Add(new ImportRow(lineNo, 0, default, ImportStatus.Invalid, line.ToString()));
                continue;
            }
            var date = ParseDate(line, start, length);
            var status = known.Contains(id) || !seen.Add(id) ? ImportStatus.Duplicate : ImportStatus.New;
            result.Rows.Add(new ImportRow(lineNo, id, date, status, line.ToString(), ExtractBranch(line[(start + length)..], date != default)));
        }
        return result;
    }

    public static int Commit(AppRecord app, uint depotId, IEnumerable<ImportRow> rows)
    {
        app.History ??= [];
        var have = new HashSet<ulong>(app.History.Where(h => h.DepotId == depotId).Select(h => h.ManifestId));
        var added = 0;
        foreach (var r in rows)
        {
            if (r.Status == ImportStatus.Duplicate && r.Branch is not null && app.History.Find(h => h.DepotId == depotId && h.ManifestId == r.ManifestId) is { Branch: null } existing)
                existing.Branch = r.Branch;
            if (r.Status != ImportStatus.New || !have.Add(r.ManifestId))
                continue;
            app.History.Add(new ManifestHistoryEntry { DepotId = depotId, ManifestId = r.ManifestId, DateUtc = r.DateUtc, Branch = r.Branch });
            added++;
        }
        app.History.Sort(static (a, b) => b.DateUtc.CompareTo(a.DateUtc));
        return added;
    }

    public static uint FindDepotId(ReadOnlySpan<char> text)
    {
        const string marker = "/depot/";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var rest = text[(idx + marker.Length)..];
            var n = 0;
            while (n < rest.Length && char.IsAsciiDigit(rest[n]))
                n++;
            if (n > 0 && uint.TryParse(rest[..n], NumberStyles.None, CultureInfo.InvariantCulture, out var depot))
                return depot;
            var next = text[(idx + 1)..].IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            idx = next < 0 ? -1 : idx + 1 + next;
        }
        return 0;
    }

    private static (int Start, int Length) FindIdRun(ReadOnlySpan<char> line)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (!char.IsAsciiDigit(line[i]))
            {
                i++;
                continue;
            }
            var s = i;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
                i++;
            var len = i - s;
            if (len is >= 15 and <= 20 && (s == 0 || !char.IsAsciiLetter(line[s - 1])) && (i == line.Length || !char.IsAsciiLetter(line[i])))
                return (s, len);
        }
        return default;
    }

    private static string ExtractBranch(ReadOnlySpan<char> rest, bool dated)
    {
        Span<char> buf = rest.Length <= 512 ? stackalloc char[rest.Length] : new char[rest.Length];
        foreach (var range in rest.Split('\t'))
        {
            var seg = rest[range].Trim();
            if (seg.IsEmpty || TryParseDate(seg, buf, out _))
                continue;
            var end = seg.IndexOfAny(' ', '\u00A0');
            var word = end < 0 ? seg : seg[..end];
            if (word.Length is > 0 and <= 64 && !word.ContainsAnyExcept(BranchChars))
                return word.ToString();
            break;
        }
        return dated ? DefaultBranch : null;
    }

    private static readonly System.Buffers.SearchValues<char> BranchChars = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.");

    private static int LongestDigitRun(ReadOnlySpan<char> line)
    {
        int best = 0, cur = 0;
        foreach (var c in line)
        {
            cur = char.IsAsciiDigit(c) ? cur + 1 : 0;
            best = Math.Max(best, cur);
        }
        return best;
    }

    private static DateTime ParseDate(ReadOnlySpan<char> line, int idStart, int idLength)
    {
        Span<char> buf = line.Length <= 512 ? stackalloc char[line.Length] : new char[line.Length];
        var segments = 0;
        foreach (var range in line.Split('\t'))
        {
            segments++;
            var (off, len) = range.GetOffsetAndLength(line.Length);
            if (off <= idStart && idStart < off + len)
                continue;
            if (TryParseDate(line.Slice(off, len), buf, out var d))
                return d;
        }
        if (segments == 1)
        {
            if (idStart > 0 && TryParseDate(line[..idStart], buf, out var before))
                return before;
            if (idStart + idLength < line.Length && TryParseDate(line[(idStart + idLength)..], buf, out var after))
                return after;
        }
        return default;
    }

    private static bool TryParseDate(ReadOnlySpan<char> segment, Span<char> buf, out DateTime date)
    {
        var n = 0;
        var space = false;
        foreach (var c in segment.Trim())
        {
            var ch = c is '–' or '—' ? '-' : char.IsWhiteSpace(c) ? ' ' : c;
            if (ch == ' ')
            {
                if (space)
                    continue;
                space = true;
            }
            else
            {
                space = false;
            }
            buf[n++] = ch;
        }
        var s = buf[..n].Trim();
        if (s.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
            s = s[..^4].TrimEnd();
        if (s.Length < 6)
        {
            date = default;
            return false;
        }
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }
}
