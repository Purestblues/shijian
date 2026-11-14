using System.Collections.Generic;
using System.Collections.ObjectModel;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Web;

namespace ShiJian.Client.Models;

public class CategoryWebSiteListPageModel : ModelBase
{
    private CategoryModel? _category;
    private ObservableCollection<WebSiteModel> _categoryWebSiteList = new();
    private bool _chooseVisibility;
    private string _mainSearchInput = string.Empty;
    private string _searchInput = string.Empty;
    private WebSiteModel? _selectedItem;
    private ObservableCollection<OptionModel> _webSiteOptionList = new();

    public bool ChooseVisibility
    {
        get => _chooseVisibility;
        set
        {
            _chooseVisibility = value;
            OnPropertyChanged();
        }
    }

    public WebSiteModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public string MainSearchInput
    {
        get => _mainSearchInput;
        set
        {
            if (_mainSearchInput == value) return;
            _mainSearchInput = value;
            OnPropertyChanged();
        }
    }

    public string SearchInput
    {
        get => _searchInput;
        set
        {
            _searchInput = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<WebSiteModel> CategoryWebSiteList
    {
        get => _categoryWebSiteList;
        set
        {
            _categoryWebSiteList = value;
            OnPropertyChanged();
        }
    }

    public CategoryModel? Category
    {
        get => _category;
        set
        {
            _category = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<OptionModel> WebSiteOptionList
    {
        get => _webSiteOptionList;
        set
        {
            _webSiteOptionList = value;
            OnPropertyChanged();
        }
    }

    public class OptionModel
    {
        public bool IsChecked { get; set; }
        public WebSiteModel? WebSite { get; set; }
        public SelectItemModel OptionValue { get; set; } = new();
    }
}
