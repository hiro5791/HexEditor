using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Services;

/// <summary>
/// ContentDialog を 1 つずつ出す。WinUI は同時に 1 つの ContentDialog しか開けず、別のダイアログが開いている間 (閉じる動きの途中を
/// 含む) に ShowAsync を呼ぶと例外 (0x80000019) になる。async void のイベントハンドラーから出すダイアログでは、その例外でアプリが
/// 落ちる。前のダイアログの表示が終わるまで待ち、閉じる動きの途中で断られたら少し待って出し直す。
/// </summary>
public static class DialogQueue
{
    private const int AsyncOperationNotStarted = unchecked((int)0x80000019);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>前のダイアログが閉じてから <paramref name="dialog"/> を出す。出せなかったら None (キャンセルと同じ扱い)。</summary>
    public static async Task<ContentDialogResult> ShowQueuedAsync(this ContentDialog dialog)
    {
        // ダイアログを出している処理の中から別のダイアログを出す場合に止まらないよう、待つのは一定の時間まで。
        bool entered = await Gate.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await dialog.ShowAsync();
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == AsyncOperationNotStarted && attempt < 20)
                {
                    await Task.Delay(100);
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == AsyncOperationNotStarted)
                {
                    AppLog.Warning("A dialog was not shown because another dialog stayed open.");
                    return ContentDialogResult.None;
                }
            }
        }
        finally
        {
            if (entered)
            {
                Gate.Release();
            }
        }
    }
}
