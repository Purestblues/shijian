using ShiJian.Client.Controls;
using ShiJian.Client.Librarys;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class UsageRankingPage : TPage
{
    public UsageRankingPage()
    {
        InitializeComponent();
        DataContext = ServiceLocator.GetRequiredService<UsageRankingPageViewModel>();
    }
}
