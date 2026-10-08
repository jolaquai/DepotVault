using System.Text;

namespace DepotVault.Core.SteamInstall;

public sealed class VdfNode
{
    public string Key { get; init; }
    public string Value { get; init; }
    public List<VdfNode> Children { get; } = [];
    internal int ValueStart { get; init; }
    internal int ValueEnd { get; init; }
    internal int CloseBrace { get; set; }
    internal int LineStart { get; init; }

    public bool IsObject => Value is null;

    public VdfNode this[string key]
    {
        get
        {
            foreach (var c in Children)
            {
                if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }
    }
}

public sealed class VdfDocument
{
    private string _text;

    private VdfDocument(string text)
    {
        _text = text;
        Root = ParseTree(text);
    }

    public VdfNode Root { get; private set; }

    public static VdfDocument Parse(string text) => new(text);

    public static VdfDocument Load(string path) => new(File.ReadAllText(path));

    public override string ToString() => _text;

    public VdfNode Get(params ReadOnlySpan<string> path)
    {
        var node = Root;
        foreach (var k in path)
        {
            node = node?[k];
            if (node is null)
                return null;
        }
        return node;
    }

    public string GetValue(params ReadOnlySpan<string> path) => Get(path)?.Value;

    public void Set(string value, params ReadOnlySpan<string> path)
    {
        var node = Root;
        var depth = 0;
        while (depth < path.Length && node[path[depth]] is { } next)
        {
            node = next;
            depth++;
        }
        if (depth == path.Length)
        {
            if (node.IsObject)
                throw new InvalidOperationException($"{string.Join('/', path.ToArray())} is an object.");
            if (node.Value == value)
                return;
            _text = string.Concat(_text.AsSpan(0, node.ValueStart), Escape(value), _text.AsSpan(node.ValueEnd));
        }
        else
        {
            if (!node.IsObject)
                throw new InvalidOperationException($"{node.Key} is a value.");
            var indent = Indent(node, depth);
            var sb = new StringBuilder();
            for (var i = depth; i < path.Length; i++)
            {
                var pad = new string('\t', indent + i - depth);
                if (i == path.Length - 1)
                {
                    sb.Append(pad).Append('"').Append(Escape(path[i])).Append("\"\t\t\"").Append(Escape(value)).Append('"').Append(NewLine);
                }
                else
                {
                    sb.Append(pad).Append('"').Append(Escape(path[i])).Append('"').Append(NewLine);
                    sb.Append(pad).Append('{').Append(NewLine);
                }
            }
            for (var i = path.Length - 2; i >= depth; i--)
                sb.Append(new string('\t', indent + i - depth)).Append('}').Append(NewLine);
            var insertAt = LineStartOf(node.CloseBrace);
            _text = _text.Insert(insertAt, sb.ToString());
        }
        Root = ParseTree(_text);
    }

    private string NewLine => _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private int LineStartOf(int pos)
    {
        var i = pos;
        while (i > 0 && _text[i - 1] is ' ' or '\t')
            i--;
        return i > 0 && _text[i - 1] == '\n' ? i : pos;
    }

    private int Indent(VdfNode parent, int depth)
    {
        foreach (var c in parent.Children)
        {
            var tabs = 0;
            for (var i = c.LineStart; i < _text.Length && _text[i] == '\t'; i++)
                tabs++;
            return tabs;
        }
        return depth;
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, _text, new UTF8Encoding(false));
        var readOnly = File.Exists(path) && FileUtil.IsReadOnly(path);
        if (readOnly)
            FileUtil.SetReadOnly(path, false);
        File.Move(tmp, path, true);
        if (readOnly)
            FileUtil.SetReadOnly(path, true);
    }

    public static string Escape(string s)
    {
        if (s.AsSpan().IndexOfAny("\"\\\n\t") < 0)
            return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static VdfNode ParseTree(string text)
    {
        var root = new VdfNode { Key = "" };
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        var pos = 0;
        string pendingKey = null;
        var pendingLine = 0;
        while (true)
        {
            SkipTrivia(text, ref pos);
            if (pos >= text.Length)
                break;
            var c = text[pos];
            if (c == '{')
            {
                if (pendingKey is null)
                    throw new FormatException($"Unexpected '{{' at {pos}.");
                var obj = new VdfNode { Key = pendingKey, LineStart = pendingLine };
                stack.Peek().Children.Add(obj);
                stack.Push(obj);
                pendingKey = null;
                pos++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count == 1)
                    throw new FormatException($"Unexpected '}}' at {pos}.");
                stack.Pop().CloseBrace = pos;
                pos++;
                continue;
            }
            var lineStart = LineStart(text, pos);
            var (str, start, end) = ReadString(text, ref pos);
            if (pendingKey is null)
            {
                pendingKey = str;
                pendingLine = lineStart;
            }
            else
            {
                stack.Peek().Children.Add(new VdfNode { Key = pendingKey, Value = str, ValueStart = start, ValueEnd = end, LineStart = pendingLine });
                pendingKey = null;
            }
        }
        root.CloseBrace = text.Length;
        return root;
    }

    private static int LineStart(string text, int pos)
    {
        var i = pos;
        while (i > 0 && text[i - 1] != '\n')
            i--;
        return i;
    }

    private static void SkipTrivia(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]))
                pos++;
            else if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n')
                    pos++;
            }
            else
                break;
        }
    }

    private static (string Value, int Start, int End) ReadString(string text, ref int pos)
    {
        if (text[pos] != '"')
        {
            var s = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
                pos++;
            return (text[s..pos], s, pos);
        }
        var start = ++pos;
        var sb = new StringBuilder();
        while (pos < text.Length && text[pos] != '"')
        {
            if (text[pos] == '\\' && pos + 1 < text.Length)
            {
                pos++;
                sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', var x => x });
            }
            else
            {
                sb.Append(text[pos]);
            }
            pos++;
        }
        var end = pos;
        pos++;
        return (sb.ToString(), start, end);
    }
}
