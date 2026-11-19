using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client.Controls;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Models.Category;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class CategoryPage : TPage
{
    private readonly IDisposable _editDirectoriesSubscription;
    private readonly IDisposable _editIsDirectoryMatchSubscription;
    private readonly IDisposable _editIsUrlMatchSubscription;
    private readonly IDisposable _editUrlPatternsSubscription;
    private readonly IDisposable _appEmptyExpandedSubscription;
    private ObservableCollection<string>? _observedEditDirectories;
    private ObservableCollection<string>? _observedEditUrlPatterns;
    private CategoryPageViewModel _model;
    private ContextMenu? _activeTagMenu;

    public CategoryPage()
    {
        InitializeComponent();
        var model = ServiceLocator.GetRequiredService<CategoryPageViewModel>();
        _model = model;
        DataContext = model;
        _editIsDirectoryMatchSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(model, x => x.EditIsDirectoryMatch)
            .Subscribe(HandleEditIsDirectoryMatchChange);

        _editDirectoriesSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(model, x => x.EditDirectories).Subscribe(val =>
        {
            if (_observedEditDirectories != null)
                _observedEditDirectories.CollectionChanged -= OnEditDirectoriesCollectionChanged;
            _observedEditDirectories = val;
            val.CollectionChanged += OnEditDirectoriesCollectionChanged;
        });

        _editIsUrlMatchSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(model, x => x.EditIsUrlMatch)
            .Subscribe(HandleEditIsUrlMatchChange);

        _editUrlPatternsSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(model, x => x.EditUrlPatterns).Subscribe(val =>
        {
            if (_observedEditUrlPatterns != null)
                _observedEditUrlPatterns.CollectionChanged -= OnEditUrlPatternsCollectionChanged;
            _observedEditUrlPatterns = val;
            val.CollectionChanged += OnEditUrlPatternsCollectionChanged;
        });

        _appEmptyExpandedSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(model, x => x.IsAppEmptyExpanded)
            .Skip(1)
            .Subscribe(expanded => _ = ExpandMotion.SetExpandedAsync(EmptyAppCategoriesHost, expanded));

        model.EntityScrollResetRequested += OnEntityScrollResetRequested;
    }

    private void OnEntityScrollResetRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = EntityListBox?.FindDescendantOfType<ScrollViewer>();
            if (scrollViewer != null)
                SmoothScroll.AnimateTo(scrollViewer, Vector.Zero);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_model != null)
        {
            ExpandMotion.ApplyFinal(EmptyAppCategoriesHost, _model.IsAppEmptyExpanded);
        }
    }

    private void OnEditDirectoriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) viewer.ScrollToEnd();
    }

    private void OnEditUrlPatternsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) viewer.ScrollToEnd();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_model != null) _model.IsEditError = false;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (_model != null)
            _model.EntityScrollResetRequested -= OnEntityScrollResetRequested;
        _activeTagMenu?.Close();
        _activeTagMenu = null;
        _editIsDirectoryMatchSubscription.Dispose();
        _editDirectoriesSubscription.Dispose();
        _editIsUrlMatchSubscription.Dispose();
        _editUrlPatternsSubscription.Dispose();
        _appEmptyExpandedSubscription.Dispose();
        if (_observedEditDirectories != null)
            _observedEditDirectories.CollectionChanged -= OnEditDirectoriesCollectionChanged;
        if (_observedEditUrlPatterns != null)
            _observedEditUrlPatterns.CollectionChanged -= OnEditUrlPatternsCollectionChanged;
        _observedEditDirectories = null;
        _observedEditUrlPatterns = null;
        _model = null;
    }

    private void HandleEditIsDirectoryMatchChange(bool isDirectoryMatch)
    {
        if (isDirectoryMatch)
        {
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewer.ScrollToEnd();
        }
        else
        {
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void HandleEditIsUrlMatchChange(bool isUrlMatch)
    {
        if (isUrlMatch)
        {
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewer.ScrollToEnd();
        }
        else
        {
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void OnAppCategoryListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (sender is not ListBox listBox) return;

        var point = e.GetPosition(listBox);
        var hit = listBox.InputHitTest(point) as Visual;

        Visual? visual = hit;
        while (visual != null && visual is not ListBoxItem)
        {
            visual = visual.GetVisualParent();
        }

        if (visual is ListBoxItem listBoxItem && listBoxItem.DataContext is CategoryModel category)
        {
            _model.SelectedAppCategoryItem = category;
        }
    }

    private void OnEntityListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (sender is not ListBox listBox) return;

        var point = e.GetPosition(listBox);
        var hit = listBox.InputHitTest(point) as Visual;

        Visual? visual = hit;
        while (visual != null && visual is not ListBoxItem)
        {
            visual = visual.GetVisualParent();
        }

        if (visual is ListBoxItem listBoxItem && listBoxItem.DataContext is CategoryEntityRow row)
        {
            _model.SelectedEntityItem = row;
        }
    }

    private void OnTagPillPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnTagPillClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_model == null || sender is not Button btn || btn.Tag is not CategoryEntityRow row)
            return;

        _model.PrepareTagPicker(row);

        _activeTagMenu?.Close();
        var menu = new ContextMenu
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            MaxHeight = 520
        };

        CategoryTagOption? systemOption = null;
        foreach (var option in _model.TagPickerOptions)
        {
            if (option.IsSystem)
            {
                systemOption ??= option;
                continue;
            }

            var item = new MenuItem
            {
                ToggleType = MenuItemToggleType.Radio,
                Header = option.Name,
                IsChecked = option.IsSelected
            };
            var captured = option;
            item.Click += (_, _) => _ = _model.AssignEntityCategoryAsync(captured);
            menu.Items.Add(item);
        }

        if (systemOption != null && !row.IsUncategorized)
        {
            menu.Items.Add(new Separator());
            var uncategorizedItem = new MenuItem { Header = systemOption.Name };
            var captured = systemOption;
            uncategorizedItem.Click += (_, _) => _ = _model.AssignEntityCategoryAsync(captured);
            menu.Items.Add(uncategorizedItem);
        }

        menu.Items.Add(new Separator());
        var newCategoryItem = new MenuItem { Header = ResourceStrings.NewCategory };
        newCategoryItem.Click += (_, _) =>
        {
            if (_model != null && _model.EditCommand.CanExecute(null))
                _model.EditCommand.Execute(null);
        };
        menu.Items.Add(newCategoryItem);

        _activeTagMenu = menu;
        menu.Open(btn);
    }
}
