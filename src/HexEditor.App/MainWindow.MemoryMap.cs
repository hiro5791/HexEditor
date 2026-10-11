using HexEditor.App.Panels;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml.Automation;

namespace HexEditor.App;

/// <summary>メモリマップのパネルの登録 (ENG-33)。</summary>
public sealed partial class MainWindow
{
    private const string MemoryMapPanelId = "memoryMap";

    /// <summary>パネルの一覧に登録する (起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterMemoryMapPanel()
    {
        if (PanelRegistry.Find(MemoryMapPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(MemoryMapPanelId, "Panel_MemoryMap_Title", PanelDock.Bottom,
                ctx =>
                {
                    var panel = new MemoryMapPanel(ctx);
                    AutomationProperties.SetAutomationId(panel, "MemoryMapPanel");
                    return panel;
                }));
        }
    }

    /// <summary>メモリマップのパネルを表示する (プロセスを開いたときに自動で。ENG-33 の受け入れ基準 1)。</summary>
    public void ShowMemoryMapPanel() => ShowPanel(MemoryMapPanelId, focus: false);
}
