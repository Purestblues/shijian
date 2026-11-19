using System;
using System.Collections.Generic;
using ShiJian.Client.Models;

namespace ShiJian.Client.Controls.Charts.Model;

public class ChartsDataModel : UINotifyPropertyChanged
{
    private List<ChartBadgeModel> BadgeList_;
    private string Name_;

    /// <summary>
    /// 名称
    /// </summary>
    public string Name
    {
        get => Name_;
        set
        {
            Name_ = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 值
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// 列名
    /// </summary>
    public string Tag { get; set; }

    /// <summary>
    /// 悬浮文本
    /// </summary>
    public string PopupText { get; set; }

    /// <summary>
    /// 是否选中
    /// </summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// 图标文件
    /// </summary>
    public string Icon { get; set; }

    /// <summary>
    /// 图标加载失败时的占位文字（域名 / 应用名，只取首字符）
    /// </summary>
    public string FallbackText { get; set; }

    /// <summary>
    /// 所属分类名称（卡片上展示用，已经过对比度处理）
    /// </summary>
    public string? CategoryName { get; set; }

    /// <summary>
    /// 所属分类颜色（与图表同一套配色规则）
    /// </summary>
    public string? CategoryColor { get; set; }

    /// <summary>
    /// 时间（仅在类型为Month样式时有效）
    /// </summary>
    public DateTime DateTime { get; set; }

    /// <summary>
    /// 数据模型
    /// </summary>
    public object Data { get; set; }

    /// <summary>
    /// 值
    /// </summary>
    public double[] Values { get; set; } = [];

    /// <summary>
    /// 列名
    /// </summary>
    public string[] ColumnNames { get; set; }

    /// <summary>
    /// 颜色
    /// </summary>
    public string Color { get; set; }

    /// <summary>
    /// 徽章
    /// </summary>
    public List<ChartBadgeModel> BadgeList
    {
        get => BadgeList_;
        set
        {
            BadgeList_ = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 最大值
    /// </summary>
    public double MaxValue { get; set; }

    /// <summary>当前周期相对占比（0..1）</summary>
    public double Ratio { get; set; }

    /// <summary>上一周期对比占比（0..1）</summary>
    public double CompareRatio { get; set; }

    /// <summary>上一周期对比条颜色（低透明度强调色）</summary>
    public string? CompareColor { get; set; }

    /// <summary>上一周期格式化时长文本</summary>
    public string? CompareTag { get; set; }
}