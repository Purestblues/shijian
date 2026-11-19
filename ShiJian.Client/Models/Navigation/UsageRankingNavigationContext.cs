using System;

namespace ShiJian.Client.Models.Navigation;

public enum UsageRankingFilter
{
    All = 0,
    Apps = 1,
    Sites = 2
}

/// <summary>
/// N17：从概览页卡片下钻到全量总时长排行页的导航上下文。
/// </summary>
public sealed record UsageRankingNavigationContext(
    DateTime StartDate,
    DateTime EndDate,
    string PeriodTitle,
    int PeriodTabIndex,
    bool IsWebBrowserMerged,
    UsageRankingFilter InitialFilter);
