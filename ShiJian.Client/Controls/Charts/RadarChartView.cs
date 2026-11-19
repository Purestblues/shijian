using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Shared.Librarys;

namespace ShiJian.Client.Controls.Charts;

public class RadarChartView : TemplatedControl
{
    public static readonly DirectProperty<RadarChartView, IEnumerable<ChartsDataModel>> DataProperty =
        AvaloniaProperty.RegisterDirect<RadarChartView, IEnumerable<ChartsDataModel>>(
            nameof(Data), o => o.Data, (o, v) => o.Data = v);

    public static readonly DirectProperty<RadarChartView, double> DataMaximumProperty =
        AvaloniaProperty.RegisterDirect<RadarChartView, double>(
            nameof(DataMaximum), o => o.DataMaximum, (o, v) => o.DataMaximum = v);

    public static readonly DirectProperty<RadarChartView, IEnumerable<ChartsDataModel>?> CompareDataProperty =
        AvaloniaProperty.RegisterDirect<RadarChartView, IEnumerable<ChartsDataModel>?>(
            nameof(CompareData), o => o.CompareData, (o, v) => o.CompareData = v);

    public static readonly DirectProperty<RadarChartView, string?> CompareLabelProperty =
        AvaloniaProperty.RegisterDirect<RadarChartView, string?>(
            nameof(CompareLabel), o => o.CompareLabel, (o, v) => o.CompareLabel = v);

    private IEnumerable<ChartsDataModel> _data = [];
    private IEnumerable<ChartsDataModel>? _compareData;
    private string? _compareLabel;
    private double _dataMaximum;
    private Border _container;

    public IEnumerable<ChartsDataModel> Data
    {
        get => _data;
        set => SetAndRaise(DataProperty, ref _data, value);
    }

    public IEnumerable<ChartsDataModel>? CompareData
    {
        get => _compareData;
        set => SetAndRaise(CompareDataProperty, ref _compareData, value);
    }

    public string? CompareLabel
    {
        get => _compareLabel;
        set => SetAndRaise(CompareLabelProperty, ref _compareLabel, value);
    }

    public double DataMaximum
    {
        get => _dataMaximum;
        set => SetAndRaise(DataMaximumProperty, ref _dataMaximum, value);
    }

    protected override Type StyleKeyOverride => typeof(RadarChartView);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _container = e.NameScope.Get<Border>("RadarContainer");
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty ||
            change.Property == CompareDataProperty ||
            change.Property == CompareLabelProperty ||
            change.Property == DataMaximumProperty)
        {
            Render();
        }
    }

    /// <summary>
    /// 按当前周期分类顺序对齐上一周期分类时长，并取两者共用最大刻度
    /// </summary>
    public static (List<double> Current, List<double> Compare, double MaxValue) AlignSeries(
        IReadOnlyList<ChartsDataModel> current,
        IEnumerable<ChartsDataModel>? compare)
    {
        var currentValues = current.Select(m => m.Value > 0 ? m.Value : m.Values.Sum()).ToList();
        var maxCurrent = currentValues.Count > 0 ? currentValues.Max() : 0d;

        if (compare == null)
            return (currentValues, [], maxCurrent);

        var lookup = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in compare)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) continue;
            var val = item.Value > 0 ? item.Value : item.Values.Sum();
            if (val <= 0) continue;
            lookup[item.Name] = lookup.TryGetValue(item.Name, out var existing) ? existing + val : val;
        }

        if (lookup.Count == 0)
            return (currentValues, [], maxCurrent);

        var compareValues = new List<double>(current.Count);
        var maxCompare = 0d;
        foreach (var item in current)
        {
            var val = !string.IsNullOrEmpty(item.Name) && lookup.TryGetValue(item.Name, out var found) ? found : 0d;
            compareValues.Add(val);
            if (val > maxCompare) maxCompare = val;
        }

        return (currentValues, compareValues, Math.Max(maxCurrent, maxCompare));
    }

    private void Render()
    {
        if (_container == null) return;
        _container.Child = null;

        var list = Data?.ToList();
        if (list == null || list.Count <= 2)
        {
            _container.Child = new EmptyData();
            return;
        }

        var (currentValues, compareValues, alignedMax) = AlignSeries(list, CompareData);
        var maxValue = DataMaximum > 0 ? DataMaximum : alignedMax;

        var radar = new RadarChart
        {
            Labels = list.Select(x => x.Name.Length > 4 ? x.Name[..4] : x.Name).ToList(),
            Values = currentValues,
            CompareValues = compareValues,
            MaxValue = maxValue
        };

        var hasCompare = compareValues.Any(v => v > 0);
        var compareCaption = string.IsNullOrWhiteSpace(CompareLabel) ? ResourceStrings.CategoryVsPrevious : CompareLabel;
        var tipLines = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var currentStr = Time.ToString((int)currentValues[i]);
            if (hasCompare)
            {
                var prevStr = Time.ToString((int)compareValues[i]);
                tipLines.Add($"{list[i].Name} {currentStr} ({compareCaption} {prevStr})");
            }
            else
            {
                tipLines.Add($"{list[i].Name} {currentStr}");
            }
        }
        ToolTip.SetTip(radar, string.Join("\n", tipLines));
        _container.Child = radar;
    }
}
