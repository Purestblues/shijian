using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ShiJian.Client.Controls;
using ShiJian.Client.Controls.Button;
using ShiJian.Client.Librarys;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class CategoryWebSiteListPage : TPage
{
    private readonly CategoryWebSiteListPageViewModel _viewModel;

    public CategoryWebSiteListPage()
    {
        InitializeComponent();
        _viewModel = ServiceLocator.GetRequiredService<CategoryWebSiteListPageViewModel>();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.MainSearchInput))
            _ = ListRowMotion.PlayStaggerInAsync(WebSiteListBox);
    }

    private void OnPageUnloaded(object? sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryWebSiteListPageViewModel.CategoryWebSiteList) &&
            string.IsNullOrEmpty(_viewModel.MainSearchInput))
        {
            _ = ListRowMotion.PlayStaggerInAsync(WebSiteListBox);
        }
    }

    private void OnWebSiteListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        if (e.Source is Visual source &&
            (source.FindAncestorOfType<IconButton>() != null ||
             source.FindAncestorOfType<Avalonia.Controls.Button>() != null))
        {
            return;
        }

        if (_viewModel.SelectedItem != null)
            _viewModel.GotoDetailCommand.Execute(_viewModel.SelectedItem);
    }

    private async void OnRemoveButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        e.Handled = true;
        if (_viewModel.IsSystemCategory || sender is not IconButton { Tag: WebSiteModel site } btn)
            return;

        var row = btn.FindAncestorOfType<ListBoxItem>();
        await ListRowMotion.CollapseRowAsync(row);
        _viewModel.DelCommand.Execute(site);
    }
}
