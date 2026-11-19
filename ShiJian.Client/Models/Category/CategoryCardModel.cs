using System;
using ShiJian.Client.Librarys;
using ShiJian.Client.Shared.Librarys;

namespace ShiJian.Client.Models.Category;

/// <summary>
/// 分类卡片上要展示的统计量：应用数 + 周期时长 + 占比 + 匹配角标。
///
/// 应用分类（<see cref="CategoryModel"/>）与网站分类（<c>WebCategoryModel</c>）是两套 Data 模型，
/// 但卡片（<c>Controls/Category/CategoryCard</c>）只有一份模板，所以展示层要的属性都收在这里。
/// </summary>
public abstract class CategoryCardModel
{
    /// <summary>分类下的应用数 / 站点数</summary>
    public int Count { get; set; }

    /// <summary>周期内时长（秒）</summary>
    public double Duration { get; set; }

    /// <summary>占同维度时长合计的比例（0..1），给占比条用</summary>
    public double Ratio { get; set; }

    /// <summary>
    /// 「目录匹配 / 通配符匹配」角标是否显示。
    /// 两种维度挂在不同字段上（应用看 IsDirectoryMatch、网站看 IsUrlMatch），由页面填。
    /// </summary>
    public bool IsMatchFlag { get; set; }

    /// <summary>角标的悬停说明：应用是「目录匹配」，网站是「通配符匹配」</summary>
    public string? MatchTip { get; set; }

    /// <summary>分类名称</summary>
    public abstract string? Name { get; }

    /// <summary>分类图标路径</summary>
    public abstract string? IconFile { get; }

    /// <summary>分类的原始色（未按主题对比度处理）</summary>
    protected abstract string? RawColor { get; }

    protected abstract bool IsSystemCategory { get; }

    /// <summary>卡片上展示的颜色：和图表同一套对比度规则</summary>
    public string DisplayColor => ChartColorHelper.Resolve(RawColor, IsSystemCategory);

    /// <summary>图标容器底色（14% 透明度分类强调色浅染）</summary>
    public string AccentTintColor => ChartColorHelper.WithAlpha(DisplayColor, 0x24);

    /// <summary>不足 1 秒按「没有数据」处理，避免卡片上出现 0s 和一条看不见的进度条</summary>
    public bool HasDuration => Duration >= 1;

    public string DurationText => HasDuration ? Time.ToString((int)Duration) : "—";

    public string RatioText => HasDuration ? Math.Round(Ratio * 100).ToString("f0") + "%" : string.Empty;

    public string CountText => "×" + Count;
}
