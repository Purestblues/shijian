using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ShiJian.Client.Controls;
using ShiJian.Client.Controls.Button;
using ShiJian.Client.Librarys;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class CategoryMembersPage : TPage
{
    private readonly CategoryMembersPageViewModel _viewModel;
    private bool _appPressedInList;
    private bool _sitePressedInList;

    public CategoryMembersPage()
    {
        InitializeComponent();
        _viewModel = ServiceLocator.GetRequiredService<CategoryMembersPageViewModel>();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.MainSearchInput))
            _ = ListRowMotion.PlayStaggerInAsync(_viewModel.IsAppTab ? AppListBox : SiteListBox);
    }

    private void OnPageUnloaded(object? sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_viewModel.MainSearchInput)) return;

        if (e.PropertyName == nameof(CategoryMembersPageViewModel.Apps) && _viewModel.IsAppTab)
            _ = ListRowMotion.PlayStaggerInAsync(AppListBox);
        else if (e.PropertyName == nameof(CategoryMembersPageViewModel.Sites) && _viewModel.IsSiteTab)
            _ = ListRowMotion.PlayStaggerInAsync(SiteListBox);
    }

    private static bool IsInteractiveButton(RoutedEventArgs e) =>
        e.Source is Visual source &&
        (source.FindAncestorOfType<IconButton>() != null ||
         source.FindAncestorOfType<Avalonia.Controls.Button>() != null);

    private void OnAppListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _appPressedInList = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsInteractiveButton(e);
    }

    private void OnAppListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _appPressedInList;
        _appPressedInList = false;
        if (!pressed || e.InitialPressMouseButton != MouseButton.Left || IsInteractiveButton(e)) return;
        if (_viewModel.SelectedApp != null)
            _viewModel.GotoAppDetailCommand.Execute(_viewModel.SelectedApp);
    }

    private void OnSiteListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _sitePressedInList = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsInteractiveButton(e);
    }

    private void OnSiteListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _sitePressedInList;
        _sitePressedInList = false;
        if (!pressed || e.InitialPressMouseButton != MouseButton.Left || IsInteractiveButton(e)) return;
        if (_viewModel.SelectedSite != null)
            _viewModel.GotoSiteDetailCommand.Execute(_viewModel.SelectedSite);
    }

    private async void OnRemoveAppButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        if (_viewModel.IsSystemCategory || sender is not IconButton { Tag: AppModel app } btn) return;

        var row = btn.FindAncestorOfType<ListBoxItem>();
        await ListRowMotion.CollapseRowAsync(row);
        _viewModel.RemoveAppCommand.Execute(app);
    }

    private async void OnRemoveSiteButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        if (_viewModel.IsSystemCategory || sender is not IconButton { Tag: WebSiteModel site } btn) return;

        var row = btn.FindAncestorOfType<ListBoxItem>();
        await ListRowMotion.CollapseRowAsync(row);
        _viewModel.RemoveSiteCommand.Execute(site);
    }
}
