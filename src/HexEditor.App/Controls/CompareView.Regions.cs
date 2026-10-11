using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// プロセスメモリ・スナップショットの側の「領域」コンボボックス (ANA-09 の「画面」: 左右の Hex ビューは領域を切り替えるコンボボックスを上部に
/// 持つ)。読める領域を「領域名 (モジュール名+0x… または private 0x…)」で並べ、選ぶとその領域の先頭へ移動する。カーソルのある領域を選んだ
/// 状態にする。領域を持たないデータの側では表示しない。
/// </summary>
public sealed partial class CompareView
{
    private readonly Dictionary<bool, (ComboBox Box, List<SourceRegion> Regions)> _regionBoxes = [];
    private bool _settingRegion;

    /// <summary>片側の領域のコンボボックス (領域を持たないデータなら null)。</summary>
    private ComboBox? CreateRegionBox(CompareSideViewModel side)
    {
        if (side.Document.Source is not IRegionMapSource)
        {
            return null;
        }

        string name = Loc.Get("Compare_Region_Name");
        var box = new ComboBox { MinWidth = 220, MaxWidth = 420, Margin = new Thickness(8, 0, 8, 4), PlaceholderText = name };
        AutomationProperties.SetAutomationId(box, side.IsRight ? "Compare_RightRegion" : "Compare_LeftRegion");
        AutomationProperties.SetName(box, name);
        ToolTipService.SetToolTip(box, name);
        var regions = new List<SourceRegion>();
        _regionBoxes[side.IsRight] = (box, regions);
        FillRegions(side);
        box.DropDownOpened += (_, _) => FillRegions(side);
        box.SelectionChanged += (_, _) =>
        {
            if (_settingRegion || box.SelectedIndex < 0 || box.SelectedIndex >= regions.Count)
            {
                return;
            }

            SelectRegion(side.IsRight, box.SelectedIndex);
        };
        return box;
    }

    /// <summary>読める領域を並べ直す (プロセスメモリでは領域が変わることがある)。</summary>
    private void FillRegions(CompareSideViewModel side)
    {
        if (!_regionBoxes.TryGetValue(side.IsRight, out var entry) || side.Document.Source is not IRegionMapSource map)
        {
            return;
        }

        IReadOnlyList<Core.Processes.ProcessModule>? modules = side.Document.Source switch
        {
            Core.Processes.SnapshotByteSource s => s.Modules,
            Core.Processes.ProcessMemoryByteSource p => p.Modules,
            _ => null,
        };
        List<SourceRegion> readable = [.. map.Regions.Where(r => r.Access == RegionAccess.Readable && r.Length > 0)];
        if (readable.SequenceEqual(entry.Regions))
        {
            return;
        }

        _settingRegion = true;
        try
        {
            entry.Regions.Clear();
            entry.Regions.AddRange(readable);
            entry.Box.Items.Clear();
            foreach (SourceRegion region in readable)
            {
                entry.Box.Items.Add(RegionComparer.RegionName(map.Regions, region.Offset, modules));
            }
        }
        finally
        {
            _settingRegion = false;
        }

        UpdateRegionSelection();
    }

    /// <summary>領域を選ぶ (その領域の先頭へ移動する。同期スクロールなら相手側も対応する位置へ動く)。テスト用の命令からも呼ぶ。</summary>
    internal void SelectRegion(bool right, int index)
    {
        if (!_regionBoxes.TryGetValue(right, out var entry) || index < 0 || index >= entry.Regions.Count)
        {
            return;
        }

        CompareSideViewModel side = right ? Session.Right : Session.Left;
        Session.FocusedRight = right;
        side.Editor.GoTo(entry.Regions[index].Offset);
        UpdateRegionSelection();
    }

    /// <summary>カーソルのある領域をコンボボックスで選んだ状態にする。</summary>
    private void UpdateRegionSelection()
    {
        foreach ((bool right, (ComboBox box, List<SourceRegion> regions)) in _regionBoxes)
        {
            long cursor = (right ? Session.Right : Session.Left).Editor.Cursor;
            int index = regions.FindIndex(r => cursor >= r.Offset && cursor < r.End);
            if (box.SelectedIndex != index)
            {
                _settingRegion = true;
                box.SelectedIndex = index;
                _settingRegion = false;
            }
        }
    }

    /// <summary>領域のコンボボックスの状態 (テスト用)。</summary>
    internal System.Text.Json.Nodes.JsonObject RegionsState(bool right)
    {
        if (!_regionBoxes.TryGetValue(right, out var entry))
        {
            return new System.Text.Json.Nodes.JsonObject { ["shown"] = false };
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["shown"] = entry.Box.Visibility == Visibility.Visible,
            ["items"] = new System.Text.Json.Nodes.JsonArray([.. entry.Box.Items.Select(i => (System.Text.Json.Nodes.JsonNode?)(i as string))]),
            ["selected"] = entry.Box.SelectedIndex,
        };
    }
}
