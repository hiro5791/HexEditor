using Acornima;
using Acornima.Ast;

namespace HexEditor.Core.Scripting.Debugging;

/// <summary>
/// スクリプトの構文の情報 (AUTO-21): 行ごとの実行できる文の位置 (ブレークポイントを置く位置)、<c>try</c> ... <c>catch</c> の範囲
/// (「捕まえられない例外で止まる」の判定)。Jint のブレークポイントは「行と列」で文の先頭を指すため、行の指定を文の位置に直す。
/// </summary>
internal sealed class JsSourceMap
{
    private readonly SortedDictionary<int, List<int>> _statements = [];
    private readonly List<((int Line, int Column) Start, (int Line, int Column) End)> _caught = [];

    private JsSourceMap()
    {
    }

    /// <summary>構文エラーなどで読めなかった。</summary>
    public bool Valid { get; private set; }

    /// <summary>本文を読んで作る。読めなければ <see cref="Valid"/> は false (ブレークポイントは設定できない)。</summary>
    public static JsSourceMap Build(string code)
    {
        var map = new JsSourceMap();
        try
        {
            Module module = new Parser(new ParserOptions()).ParseModule(code);
            map.Walk(module, inFunctionBody: false);
            map.Valid = true;
        }
        catch (ParseErrorException)
        {
            // 構文エラーのスクリプトは実行前に失敗する。
        }

        return map;
    }

    private void Walk(Node node, bool inFunctionBody)
    {
        if (node is Statement and not (BlockStatement or FunctionDeclaration or ClassDeclaration or EmptyStatement))
        {
            (int line, int column) = (node.Location.Start.Line, node.Location.Start.Column);
            if (!_statements.TryGetValue(line, out List<int>? columns))
            {
                _statements[line] = columns = [];
            }

            if (!columns.Contains(column))
            {
                columns.Add(column);
            }
        }

        if (node is TryStatement { Handler: not null } t)
        {
            _caught.Add(((t.Block.Location.Start.Line, t.Block.Location.Start.Column), (t.Block.Location.End.Line, t.Block.Location.End.Column)));
        }

        foreach (Node child in node.ChildNodes)
        {
            Walk(child, inFunctionBody);
        }
    }

    /// <summary>
    /// 行 <paramref name="line"/> 以降で、最初に実行できる文がある行とその列 (複数)。ブレークポイントを置いた行にコードがなければ
    /// 次のコードのある行に寄せる。なければ空。
    /// </summary>
    public (int Line, IReadOnlyList<int> Columns)? Resolve(int line)
    {
        foreach ((int l, List<int> columns) in _statements)
        {
            if (l >= line)
            {
                return (l, [.. columns.Order()]);
            }
        }

        return null;
    }

    /// <summary>位置が <c>catch</c> を持つ <c>try</c> ブロックの中か。</summary>
    public bool IsCaught(int line, int column)
    {
        foreach (((int Line, int Column) start, (int Line, int Column) end) in _caught)
        {
            bool afterStart = line > start.Line || (line == start.Line && column >= start.Column);
            bool beforeEnd = line < end.Line || (line == end.Line && column <= end.Column);
            if (afterStart && beforeEnd)
            {
                return true;
            }
        }

        return false;
    }
}
