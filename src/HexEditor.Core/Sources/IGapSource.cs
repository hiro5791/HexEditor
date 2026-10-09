namespace HexEditor.Core.Sources;

/// <summary>
/// データのない範囲 (隙間) を持つデータソース (ENG-38 の仕様 3)。Intel HEX・S-record をデコードした内容のように、アドレス空間の
/// 一部にだけデータがあり、残りは塗りつぶしの値として読める。表示では隙間を「データなし」(<see cref="Engine.ByteState.NoData"/>)
/// として区別し、元の形式への保存 (TOOL-11) では隙間を出力しない。
/// </summary>
public interface IGapSource
{
    /// <summary>[<paramref name="offset"/>, + <paramref name="length"/>) の中の隙間を、オフセットの昇順に返す。</summary>
    IEnumerable<(long Offset, long Length)> GapsIn(long offset, long length);
}
