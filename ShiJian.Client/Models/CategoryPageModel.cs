using System.Collections.Generic;
using System.Collections.ObjectModel;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models.Category;
using ShiJian.Client.PageState;
using ShiJian.Client.Shared.Models.Web;

namespace ShiJian.Client.Models;

[GeneratePageState]
public partial class CategoryPageModel : ModelBase
{
    /// <summary>
    /// 视图形态与排序口径需要默认值：<c>[PageState]</c> 只负责「返回导航时恢复」
    /// （<c>TryRestoreState</c> 第一句就是 <c>if (!navigation.IsNavigatingBack) return false;</c>），
    /// 从侧边栏进页面时不会写入任何值，开关就会停在一个「什么都没选」的空状态。
    /// </summary>
    public CategoryPageModel()
    {
        BrowseMode = BrowseModeOptions[0];
        ViewMode = ViewModeOptions[0];
        SortMode = SortOptions[0];
        EntityTypeMode = EntityTypeOptions[0];
        EntityFilterMode = EntityFilterOptions[0];
        EntitySortMode = EntitySortOptions[0];
    }

    private ObservableCollection<CategoryModel> _data = new();
    private ObservableCollection<CategoryModel> _activeAppCategories = new();
    private ObservableCollection<CategoryModel> _emptyAppCategories = new();
    private bool _isAppEmptyExpanded;
    private SelectItemModel _browseMode;
    private string _entitySearchInput = string.Empty;
    private SelectItemModel _entityTypeMode;
    private SelectItemModel _entityFilterMode;
    private SelectItemModel _entitySortMode;
    private ObservableCollection<CategoryEntityRow> _filteredEntities = new();
    private CategoryEntityRow? _selectedEntityItem;
    private int _uncategorizedEntityCount;
    private int _totalEntityCount;
    private ObservableCollection<CategoryTagOption> _tagPickerOptions = new();
    private string _editColor = string.Empty;
    private string _editErrorText = string.Empty;
    private string _editIconFile = string.Empty;
    private bool _editIsDirectoryMatch;
    private bool _editIsUrlMatch;
    private string _editName = string.Empty;
    private string _editSelectedDirectory = string.Empty;
    private string _editSelectedUrlPattern = string.Empty;
    private bool _editVisibility;
    private bool _isCreate;
    private bool _isEditError;
    private bool _isSysCategory;
    private CategoryModel? _selectedAppCategoryItem;

    /// <summary>N5：「分类 / 应用与网站」主视图切换</summary>
    [PageState(LookupFrom = nameof(BrowseModeOptions))]
    public SelectItemModel BrowseMode
    {
        get => _browseMode;
        set
        {
            _browseMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEntityBrowseMode));
            OnPropertyChanged(nameof(IsCategoryBrowseMode));
        }
    }

    public List<SelectItemModel> BrowseModeOptions { get; } =
    [
        new() { Id = 0, Name = ResourceStrings.BrowseByCategory },
        new() { Id = 1, Name = ResourceStrings.BrowseByEntity }
    ];

    public bool IsEntityBrowseMode => BrowseMode?.Id == 1;
    public bool IsCategoryBrowseMode => !IsEntityBrowseMode;

    [PageState]
    public bool IsAppEmptyExpanded
    {
        get => _isAppEmptyExpanded;
        set
        {
            if (_isAppEmptyExpanded == value) return;
            _isAppEmptyExpanded = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<CategoryModel> ActiveAppCategories => _activeAppCategories;
    public ObservableCollection<CategoryModel> EmptyAppCategories => _emptyAppCategories;

    public bool HasAnyAppCategories => Data.Count > 0;
    public bool HasActiveAppCategories => ActiveAppCategories.Count > 0;
    public bool HasEmptyAppCategories => EmptyAppCategories.Count > 0;
    public string AppEmptyHeaderText => string.Format(ResourceStrings.EmptyCategoriesHeader, EmptyAppCategories.Count);

    public string EntitySearchInput
    {
        get => _entitySearchInput;
        set
        {
            if (_entitySearchInput == value) return;
            _entitySearchInput = value;
            OnPropertyChanged();
        }
    }

    [PageState(LookupFrom = nameof(EntityTypeOptions))]
    public SelectItemModel EntityTypeMode
    {
        get => _entityTypeMode;
        set
        {
            _entityTypeMode = value;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> EntityTypeOptions { get; } =
    [
        new() { Id = 0, Name = ResourceStrings.FilterAll },
        new() { Id = 1, Name = ResourceStrings.MergeCandidateApp },
        new() { Id = 2, Name = ResourceStrings.MergeCandidateSite }
    ];

    [PageState(LookupFrom = nameof(EntityFilterOptions))]
    public SelectItemModel EntityFilterMode
    {
        get => _entityFilterMode;
        set
        {
            _entityFilterMode = value;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> EntityFilterOptions { get; } =
    [
        new() { Id = 0, Name = ResourceStrings.FilterAll },
        new() { Id = 1, Name = ResourceStrings.FilterUncategorized }
    ];

    [PageState(LookupFrom = nameof(EntitySortOptions))]
    public SelectItemModel EntitySortMode
    {
        get => _entitySortMode;
        set
        {
            _entitySortMode = value;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> EntitySortOptions { get; } =
    [
        new() { Id = 0, Name = ResourceStrings.SortByTodayDuration },
        new() { Id = 1, Name = ResourceStrings.SortByTotalDuration },
        new() { Id = 2, Name = ResourceStrings.SortByName },
        new() { Id = 3, Name = ResourceStrings.SortUncategorizedFirst }
    ];

    public ObservableCollection<CategoryEntityRow> FilteredEntities => _filteredEntities;

    public CategoryEntityRow? SelectedEntityItem
    {
        get => _selectedEntityItem;
        set
        {
            _selectedEntityItem = value;
            OnPropertyChanged();
        }
    }

    public int UncategorizedEntityCount
    {
        get => _uncategorizedEntityCount;
        set
        {
            if (_uncategorizedEntityCount == value) return;
            _uncategorizedEntityCount = value;
            OnPropertyChanged();
        }
    }

    public int TotalEntityCount
    {
        get => _totalEntityCount;
        set
        {
            if (_totalEntityCount == value) return;
            _totalEntityCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAnyEntities));
        }
    }

    public bool HasAnyEntities => _totalEntityCount > 0;
    public bool HasFilteredEntities => FilteredEntities.Count > 0;

    public ObservableCollection<CategoryTagOption> TagPickerOptions => _tagPickerOptions;

    public void NotifyCategoryBucketStates()
    {
        OnPropertyChanged(nameof(HasAnyAppCategories));
        OnPropertyChanged(nameof(HasActiveAppCategories));
        OnPropertyChanged(nameof(HasEmptyAppCategories));
        OnPropertyChanged(nameof(AppEmptyHeaderText));
    }

    public void NotifyEntityListState()
    {
        OnPropertyChanged(nameof(HasFilteredEntities));
    }

    private SelectItemModel _viewMode;

    /// <summary>
    /// 紧凑 / 卡片视图（N7-b）。纯展示层开关：不重拉数据，也不重建列表项，
    /// 所以切换时滚动位置与选中态都不会丢。
    /// </summary>
    [PageState(LookupFrom = nameof(ViewModeOptions))]
    public SelectItemModel ViewMode
    {
        get => _viewMode;
        set
        {
            _viewMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCardView));
        }
    }

    public List<SelectItemModel> ViewModeOptions { get; } =
    [
        new()
        {
            Id = 0,
            Name = ResourceStrings.ViewCompact
        },
        new()
        {
            Id = 1,
            Name = ResourceStrings.ViewCard
        }
    ];

    public bool IsCardView => ViewMode?.Id == 1;

    private SelectItemModel _sortMode;

    /// <summary>
    /// 排序口径（N7-c）：时长 / 名称 / 数量。只重排现有卡片，不重拉数据
    /// （卡片上的时长与数量已经是全量，排序纯属展示层）。
    /// </summary>
    [PageState(LookupFrom = nameof(SortOptions))]
    public SelectItemModel SortMode
    {
        get => _sortMode;
        set
        {
            _sortMode = value;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> SortOptions { get; } =
    [
        new()
        {
            Id = 0,
            Name = ResourceStrings.SortByDuration
        },
        new()
        {
            Id = 1,
            Name = ResourceStrings.SortByName
        },
        new()
        {
            Id = 2,
            Name = ResourceStrings.SortByCount
        }
    ];

    [PageState(DataCache = true)]
    public ObservableCollection<CategoryModel> Data
    {
        get => _data;
        set
        {
            _data = value;
            OnPropertyChanged();
        }
    }

    public CategoryModel? SelectedAppCategoryItem
    {
        get => _selectedAppCategoryItem;
        set
        {
            _selectedAppCategoryItem = value;
            OnPropertyChanged();
        }
    }

    public bool EditVisibility
    {
        get => _editVisibility;
        set
        {
            _editVisibility = value;
            OnPropertyChanged();
        }
    }

    public bool IsCreate
    {
        get => _isCreate;
        set
        {
            _isCreate = value;
            OnPropertyChanged();
        }
    }

    public string EditName
    {
        get => _editName;
        set
        {
            _editName = value;
            OnPropertyChanged();
        }
    }

    public string EditIconFile
    {
        get => _editIconFile;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                _editIconFile = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EditIsDirectoryMatch
    {
        get => _editIsDirectoryMatch;
        set
        {
            _editIsDirectoryMatch = value;
            OnPropertyChanged();
        }
    }

    public bool EditIsUrlMatch
    {
        get => _editIsUrlMatch;
        set
        {
            _editIsUrlMatch = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> EditDirectories { get; set; } = new();

    public ObservableCollection<string> EditUrlPatterns { get; set; } = new();

    public string EditSelectedDirectory
    {
        get => _editSelectedDirectory;
        set
        {
            _editSelectedDirectory = value;
            OnPropertyChanged();
        }
    }

    public string EditSelectedUrlPattern
    {
        get => _editSelectedUrlPattern;
        set
        {
            _editSelectedUrlPattern = value;
            OnPropertyChanged();
        }
    }

    private string _editNewUrlPattern = string.Empty;
    public string EditNewUrlPattern
    {
        get => _editNewUrlPattern;
        set
        {
            _editNewUrlPattern = value;
            OnPropertyChanged();
        }
    }

    public string EditErrorText
    {
        get => _editErrorText;
        set
        {
            _editErrorText = value;
            OnPropertyChanged();
        }
    }

    public bool IsEditError
    {
        get => _isEditError;
        set
        {
            _isEditError = value;
            OnPropertyChanged();
        }
    }

    public string EditColor
    {
        get => _editColor;
        set
        {
            _editColor = value;
            OnPropertyChanged();
        }
    }

    public bool IsSysCategory
    {
        get => _isSysCategory;
        set
        {
            _isSysCategory = value;
            OnPropertyChanged();
        }
    }
}
