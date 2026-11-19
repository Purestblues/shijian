using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Controls.Window;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Librarys.Api;
using ShiJian.Client.Logging;
using ShiJian.Client.Models;
using ShiJian.Client.Platform.Abstractions.Primitives;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Servicers.Updater;
using ShiJian.Client.Shared.Event;
using ShiJian.Client.Shared.Helpers;
using ShiJian.Client.Shared.Models.Config;
using ShiJian.Client.Shared.Models.Data;
using ShiJian.Client.Shared.Servicers.Interfaces;

namespace ShiJian.Client.ViewModels;

public class SettingPageViewModel : SettingPageModel
{
    private readonly IDialogService _dialogService;
    private readonly UpdateCheckerService _updateCheckerService;
    private readonly IWebData _webDataService;
    private readonly IAppData _appDataService;
    private readonly IWebSiteData _webSiteDataService;
    private readonly IAppConfig _appConfig;
    private readonly IData _dataService;
    private readonly IProcessService _processService;
    private readonly IAppMergeService _appMergeService;
    private readonly IEntityData _entityData;
    private readonly INavigationService _navigationService;
    private readonly IStateService _stateService;
    private readonly IShiJianApiClient _apiClient;

    /// <summary>与 <c>BrowserOptions</c> 一一对应的浏览器标识（第一项是空串 = 自动推荐）</summary>
    private readonly List<string> _browserReferences = [];
    private bool _isLoadingBrowsers;
    private bool _isLoadingWebDav;
    private CancellationTokenSource? _webDavSaveDebounceCts;
    private readonly IToastService _toastService;
    private readonly ConfigModel _config;
    private List<MergeCandidateRow> _allMergeCandidates = [];
    private int _editingGroupId;

    public SettingPageViewModel(
        IAppConfig appConfig,
        IToastService toastService,
        IData data,
        IWebData webData,
        IAppData appData,
        IWebSiteData webSiteData,
        IDialogService dialogService,
        IProcessService processService,
        UpdateCheckerService updateCheckerService,
        IAppMergeService appMergeService,
        IEntityData entityData,
        INavigationService navigationService,
        IStateService stateService,
        IShiJianApiClient apiClient)
    {
        _appConfig = appConfig;
        _toastService = toastService;
        _dataService = data;
        _webDataService = webData;
        _appDataService = appData;
        _webSiteDataService = webSiteData;
        _dialogService = dialogService;
        _processService = processService;
        _updateCheckerService = updateCheckerService;
        _appMergeService = appMergeService;
        _entityData = entityData;
        _navigationService = navigationService;
        _stateService = stateService;
        _apiClient = apiClient;
        _config = appConfig.GetConfig();

        OpenURL = AsyncRelayCommand.Create<object>(OnOpenURL).DisposeWith(Disposables);
        DelDataCommand = AsyncRelayCommand.CreateFromTask<object>(OnDelDataAsync).DisposeWith(Disposables);
        ExportDataCommand = AsyncRelayCommand.CreateFromTask<object>(OnExportDataAsync).DisposeWith(Disposables);
        ExportCsvCommand = AsyncRelayCommand.CreateFromTask<object>(OnExportCsvAsync).DisposeWith(Disposables);
        ImportBackupCommand = AsyncRelayCommand.CreateFromTask<object>(OnImportBackupAsync).DisposeWith(Disposables);
        TestWebDavCommand = AsyncRelayCommand.CreateFromTask(OnTestWebDavAsync).DisposeWith(Disposables);
        SyncWebDavCommand = AsyncRelayCommand.CreateFromTask(OnSyncWebDavAsync).DisposeWith(Disposables);
        LoadWebDavBackupsCommand = AsyncRelayCommand.CreateFromTask(OnLoadWebDavBackupsAsync).DisposeWith(Disposables);
        RestoreWebDavMergeCommand = AsyncRelayCommand.CreateFromTask<object>(item => OnRestoreWebDavBackupAsync(item, "merge")).DisposeWith(Disposables);
        RestoreWebDavReplaceCommand = AsyncRelayCommand.CreateFromTask<object>(item => OnRestoreWebDavBackupAsync(item, "replace")).DisposeWith(Disposables);
        CloseWebDavRemoteModalCommand = AsyncRelayCommand.Create<object>(_ => { WebDavRemoteModalVisible = false; }).DisposeWith(Disposables);
        CheckUpdate = AsyncRelayCommand.CreateFromTask(OnCheckUpdateAsync).DisposeWith(Disposables);
        DissolveMergeGroupCommand = AsyncRelayCommand.CreateFromTask<object>(OnDissolveMergeGroupAsync).DisposeWith(Disposables);
        ResetMergeGroupsCommand = AsyncRelayCommand.CreateFromTask<object>(OnResetMergeGroupsAsync).DisposeWith(Disposables);
        OpenCreateMergeGroupCommand = AsyncRelayCommand.CreateFromTask(OnOpenCreateMergeGroupAsync).DisposeWith(Disposables);
        OpenEditMergeGroupCommand = AsyncRelayCommand.CreateFromTask<object>(OnOpenEditMergeGroupAsync).DisposeWith(Disposables);
        ToggleMergeCandidateCommand = AsyncRelayCommand.Create<object>(OnToggleMergeCandidate).DisposeWith(Disposables);
        SetMergePrimaryCommand = AsyncRelayCommand.Create<object>(OnSetMergePrimary).DisposeWith(Disposables);
        SaveMergeGroupCommand = AsyncRelayCommand.CreateFromTask(OnSaveMergeGroupAsync).DisposeWith(Disposables);
        CloseMergeEditorCommand = AsyncRelayCommand.Create<object>(_ => { MergeEditorVisible = false; }).DisposeWith(Disposables);

        appConfig.ConfigChanged += OnConfigChanged;

        Initialize();

        // 默认浏览器下拉框换项即保存（没有「保存」按钮，和其他设置项一致）
        WhenPropertyChanged(this, x => x.SelectedBrowser, _ => OnSelectedBrowserChangedAsync());
        WhenPropertyChanged(this, x => x.MergeEditorSearch, _ =>
        {
            FilterMergeCandidates();
            return Task.CompletedTask;
        });

        WhenPropertyChanged(this, x => x.WebDavEnabled, _ => OnWebDavImmediateSettingChangedAsync());
        WhenPropertyChanged(this, x => x.WebDavMergeOnSync, _ => OnWebDavImmediateSettingChangedAsync());
        WhenPropertyChanged(this, x => x.SelectedWebDavSyncMode, _ => OnWebDavImmediateSettingChangedAsync());
        WhenPropertyChanged(this, x => x.WebDavUrl, _ => OnWebDavDebouncedTextChangedAsync());
        WhenPropertyChanged(this, x => x.WebDavRemoteFolder, _ => OnWebDavDebouncedTextChangedAsync());
        WhenPropertyChanged(this, x => x.WebDavUsername, _ => OnWebDavDebouncedTextChangedAsync());
        WhenPropertyChanged(this, x => x.WebDavPassword, _ => OnWebDavDebouncedTextChangedAsync());
    }

    public ICommand DissolveMergeGroupCommand { get; }
    public ICommand ResetMergeGroupsCommand { get; }
    public ICommand OpenCreateMergeGroupCommand { get; }
    public ICommand OpenEditMergeGroupCommand { get; }
    public ICommand ToggleMergeCandidateCommand { get; }
    public ICommand SetMergePrimaryCommand { get; }
    public ICommand SaveMergeGroupCommand { get; }
    public ICommand CloseMergeEditorCommand { get; }

    /// <summary>进设置页时恢复分页状态并刷新一次（应用表可能在别的页面里改过）</summary>
    public override async Task OnNavigatedToAsync()
    {
        TryRestoreState(_navigationService, _stateService);
        await LoadBrowsersAsync();
        await LoadMergeGroupsAsync();
        await base.OnNavigatedToAsync();
    }

    public override void OnNavigatedFrom()
    {
        SaveState(_stateService);
        base.OnNavigatedFrom();
    }

    /// <summary>
    /// 默认浏览器：扩展早期版本上报的站点记录没有浏览器标识（`BrowserRef` 为 NULL），
    /// 这些历史记录统一归给这里选的浏览器。第一项「自动推荐」= 不指定，由服务端取出现最多的标识。
    /// </summary>
    private async Task LoadBrowsersAsync()
    {
        _isLoadingBrowsers = true;
        try
        {
            var browsers = await _entityData.GetBrowsersAsync();

            _browserReferences.Clear();
            var options = new List<SelectItemModel>
            {
                new() { Id = 0, Name = ResourceStrings.BrowserAuto }
            };
            _browserReferences.Add(string.Empty);

            foreach (var browser in browsers)
            {
                var label = string.IsNullOrWhiteSpace(browser.AppName)
                    ? browser.Reference
                    : $"{browser.AppName} ({browser.Reference})";
                options.Add(new SelectItemModel { Id = options.Count, Name = label });
                _browserReferences.Add(browser.Reference);
            }

            BrowserOptions = options;

            var current = _config.General.DefaultBrowser ?? string.Empty;
            var index = _browserReferences.FindIndex(reference =>
                string.Equals(reference, current, StringComparison.OrdinalIgnoreCase));
            SelectedBrowser = options[index < 0 ? 0 : index];
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载默认浏览器候选失败：{ex.Message}");
        }
        finally
        {
            _isLoadingBrowsers = false;
        }
    }

    /// <summary>下拉框换项即落配置（没有「保存」按钮，与其他设置项一致）</summary>
    public async Task OnSelectedBrowserChangedAsync()
    {
        if (_isLoadingBrowsers) return;

        var option = SelectedBrowser;
        if (option == null) return;

        var reference = option.Id >= 0 && option.Id < _browserReferences.Count
            ? _browserReferences[option.Id]
            : string.Empty;

        if (string.Equals(_config.General.DefaultBrowser, reference, StringComparison.Ordinal)) return;

        _config.General.DefaultBrowser = reference;
        try
        {
            await _appConfig.SaveAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"保存默认浏览器失败：{ex.Message}", ex);
        }
    }

    private async Task LoadMergeGroupsAsync()
    {
        try
        {
            var groups = await _appMergeService.GetGroupsAsync(force: true);
            MergeGroups.Clear();
            foreach (var group in groups)
            {
                var kindLabel = group.Kind == "mixed"
                    ? ResourceStrings.MergeKindMixed
                    : group.Kind == "site"
                        ? ResourceStrings.MergeCandidateSite
                        : group.IsAuto
                            ? string.Empty
                            : ResourceStrings.MergeKindManual;

                MergeGroups.Add(new AppMergeGroupRow
                {
                    Key = group.Key,
                    Name = group.Name,
                    Count = group.MemberNames.Count,
                    MembersText = string.Join(" · ", group.MemberNames),
                    IsRenamed = group.Name != group.MemberNames.FirstOrDefault(),
                    Kind = group.Kind,
                    IsAuto = group.IsAuto,
                    KindLabel = kindLabel
                });
            }

            HasMergeGroups = MergeGroups.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"加载子母程序合并组失败：{ex.Message}", ex);
        }
    }

    private async Task OnOpenCreateMergeGroupAsync()
    {
        await OpenMergeEditorAsync(null);
    }

    private async Task OnOpenEditMergeGroupAsync(object obj)
    {
        if (obj is not AppMergeGroupRow row) return;
        var group = _appMergeService.Groups.FirstOrDefault(g => g.Key == row.Key);
        if (group == null) return;
        await OpenMergeEditorAsync(group);
    }

    private async Task OpenMergeEditorAsync(AppMergeGroup? existing)
    {
        MergeEditorError = string.Empty;
        MergeEditorSearch = string.Empty;
        _editingGroupId = existing != null && int.TryParse(existing.Key, out var gid) ? gid : 0;
        IsMergeEditorCreate = existing == null;
        MergeEditorName = existing?.Name ?? string.Empty;

        try
        {
            var appsTask = _appDataService.GetAllAppsAsync();
            var sitesTask = _webSiteDataService.GetAllWebSitesAsync();
            await Task.WhenAll(appsTask, sitesTask);

            var selectedSet = existing != null
                ? new HashSet<string>(existing.MemberRefs, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryRef = existing?.PrimaryRef ?? string.Empty;

            var candidates = new List<MergeCandidateRow>();
            foreach (var app in appsTask.Result.Where(a => !string.IsNullOrWhiteSpace(a.Name)).OrderByDescending(a => a.TotalTime))
            {
                var r = AppMergePlanner.CanonicalMemberRef(false, app.Name!);
                candidates.Add(new MergeCandidateRow
                {
                    Ref = r,
                    Name = AppMergePlanner.AppDisplayName(app),
                    Subtitle = app.Name!,
                    Icon = app.IconFile,
                    IsSite = false,
                    IsSelected = selectedSet.Contains(r),
                    IsPrimary = string.Equals(r, primaryRef, StringComparison.OrdinalIgnoreCase)
                });
            }

            foreach (var site in sitesTask.Result.Where(s => !string.IsNullOrWhiteSpace(s.Domain)).OrderByDescending(s => s.Duration))
            {
                var r = AppMergePlanner.CanonicalMemberRef(true, site.Domain!);
                candidates.Add(new MergeCandidateRow
                {
                    Ref = r,
                    Name = AppMergePlanner.SiteDisplayName(site),
                    Subtitle = site.Domain!,
                    Icon = site.IconFile,
                    IsSite = true,
                    IsSelected = selectedSet.Contains(r),
                    IsPrimary = string.Equals(r, primaryRef, StringComparison.OrdinalIgnoreCase)
                });
            }

            _allMergeCandidates = candidates;
            SyncMergeSelectionState();
            FilterMergeCandidates();
            MergeEditorVisible = true;
        }
        catch (Exception ex)
        {
            Logger.Error($"打开合并编辑器失败：{ex.Message}", ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private void OnToggleMergeCandidate(object obj)
    {
        if (obj is not MergeCandidateRow row) return;
        row.IsSelected = !row.IsSelected;
        if (!row.IsSelected)
        {
            row.IsPrimary = false;
        }
        SyncMergeSelectionState();
    }

    private void OnSetMergePrimary(object obj)
    {
        if (obj is not MergeCandidateRow target) return;
        target.IsSelected = true;
        foreach (var item in _allMergeCandidates)
        {
            item.IsPrimary = ReferenceEquals(item, target);
        }
        SyncMergeSelectionState();
    }

    private void SyncMergeSelectionState()
    {
        var selected = _allMergeCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count > 0 && !selected.Any(c => c.IsPrimary))
        {
            selected[0].IsPrimary = true;
        }

        if (selected.Count >= 2 && !string.IsNullOrEmpty(MergeEditorError))
        {
            MergeEditorError = string.Empty;
        }

        MergeEditorSelectedMembers.Clear();
        foreach (var item in selected)
        {
            MergeEditorSelectedMembers.Add(item);
        }
    }

    private void FilterMergeCandidates()
    {
        var keyword = (MergeEditorSearch ?? string.Empty).Trim();
        var filtered = string.IsNullOrEmpty(keyword)
            ? _allMergeCandidates
            : _allMergeCandidates.Where(c =>
                c.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || c.Subtitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        MergeEditorCandidates.Clear();
        foreach (var item in filtered.Take(120))
        {
            MergeEditorCandidates.Add(item);
        }
    }

    private async Task OnSaveMergeGroupAsync()
    {
        MergeEditorError = string.Empty;
        var selected = _allMergeCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count < 2)
        {
            var err = ResourceStrings.MergeGroupNeedTwo;
            MergeEditorError = string.IsNullOrWhiteSpace(err) ? "请至少选择 2 个成员" : err;
            return;
        }

        var primary = selected.FirstOrDefault(c => c.IsPrimary) ?? selected[0];
        var refs = selected.Select(c => c.Ref).ToList();

        try
        {
            if (_editingGroupId > 0)
            {
                await _appMergeService.UpdateGroupAsync(_editingGroupId, MergeEditorName, primary.Ref, refs);
            }
            else
            {
                await _appMergeService.CreateGroupAsync(MergeEditorName, primary.Ref, refs);
            }

            MergeEditorVisible = false;
            _toastService.Success(ResourceStrings.MergeGroupSaved);
            await LoadMergeGroupsAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存合并组失败：{ex.Message}");
            MergeEditorError = ex.Message;
        }
    }

    private async Task OnDissolveMergeGroupAsync(object obj)
    {
        if (obj is not AppMergeGroupRow row) return;

        try
        {
            await _appMergeService.DissolveAsync(row.Key);
            _toastService.Success(ResourceStrings.MergeGroupDissolved);
            await LoadMergeGroupsAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnResetMergeGroupsAsync(object obj)
    {
        try
        {
            await _appMergeService.ResetAsync();
            _toastService.Success(ResourceStrings.MergeGroupRestored);
            await LoadMergeGroupsAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    public object GeneralSettings
    {
        get => _config.General;
        set
        {
            if (value is GeneralModel general && _config.General != general)
            {
                _config.General = general;
                _ = _appConfig.SaveAsync();
                OnPropertyChanged();
            }
        }
    }

    public object BehaviorSettings
    {
        get => _config.Behavior;
        set
        {
            if (value is BehaviorModel behavior && _config.Behavior != behavior)
            {
                _config.Behavior = behavior;
                _ = _appConfig.SaveAsync();
                OnPropertyChanged();
            }
        }
    }

    public string RefreshShortcut
    {
        get => _config.Shortcut?.Refresh ?? string.Empty;
        set
        {
            _config.Shortcut ??= new ShortcutModel();
            if (_config.Shortcut.Refresh == value) return;
            _config.Shortcut.Refresh = value ?? string.Empty;
            _ = _appConfig.SaveAsync();
            OnPropertyChanged();
        }
    }

    public string SearchShortcut
    {
        get => _config.Shortcut?.Search ?? string.Empty;
        set
        {
            _config.Shortcut ??= new ShortcutModel();
            if (_config.Shortcut.Search == value) return;
            _config.Shortcut.Search = value ?? string.Empty;
            _ = _appConfig.SaveAsync();
            OnPropertyChanged();
        }
    }

    // 鼠标后退键始终可用，与此配置无关
    public string NavigateBackShortcut
    {
        get => _config.Shortcut?.NavigateBack ?? string.Empty;
        set
        {
            _config.Shortcut ??= new ShortcutModel();
            if (_config.Shortcut.NavigateBack == value) return;
            _config.Shortcut.NavigateBack = value ?? string.Empty;
            _ = _appConfig.SaveAsync();
            OnPropertyChanged();
        }
    }

    public ICommand OpenURL { get; }
    public ICommand CheckUpdate { get; }
    public ICommand DelDataCommand { get; }
    public ICommand ExportDataCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ImportBackupCommand { get; }
    public ICommand TestWebDavCommand { get; }
    public ICommand SyncWebDavCommand { get; }
    public ICommand LoadWebDavBackupsCommand { get; }
    public ICommand RestoreWebDavMergeCommand { get; }
    public ICommand RestoreWebDavReplaceCommand { get; }
    public ICommand CloseWebDavRemoteModalCommand { get; }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.HasChange("General.Language"))
        {
            if (TabbarData.Count > 0) TabbarData[0] = ResourceStrings.SettingTabGeneral;
            if (TabbarData.Count > 1) TabbarData[1] = ResourceStrings.SettingTabAppearance;
            if (TabbarData.Count > 2) TabbarData[2] = ResourceStrings.SettingTabTracking;
            if (TabbarData.Count > 3) TabbarData[3] = ResourceStrings.SettingTabData;
            if (TabbarData.Count > 4) TabbarData[4] = ResourceStrings.SettingTabAbout;
            OnPropertyChanged(nameof(TabbarData));
            RefreshWebDavSyncModeOptions();
        }

        // 别处编辑了 Shortcut 也同步刷新本地输入框
        if (e.HasAnyChange("Shortcut.Refresh", "Shortcut.Search", "Shortcut.NavigateBack"))
        {
            OnPropertyChanged(nameof(RefreshShortcut));
            OnPropertyChanged(nameof(SearchShortcut));
            OnPropertyChanged(nameof(NavigateBackShortcut));
        }

        if (e.HasAnyChange("WebDav.Enabled", "WebDav.Url", "WebDav.SyncMode", "WebDav.LastSyncAtUtc"))
        {
            LoadWebDavFromConfig();
        }
    }

    private void Initialize()
    {
        Data = _config.General;
        TabbarData =
        [
            ResourceStrings.SettingTabGeneral,
            ResourceStrings.SettingTabAppearance,
            ResourceStrings.SettingTabTracking,
            ResourceStrings.SettingTabData,
            ResourceStrings.SettingTabAbout
        ];
        TabbarIcons =
        [
            IconTypes.Settings,
            IconTypes.SetlockScreen,
            IconTypes.BulletedList,
            IconTypes.Folder,
            IconTypes.Info
        ];
        Version = AppVersionHelper.GetDisplayVersion();
        DelDataStartMonthDate = DateTime.Now;
        DelDataEndMonthDate = DateTime.Now;
        ExportDataStartMonthDate = DateTime.Now;
        ExportDataEndMonthDate = DateTime.Now;
        RefreshWebDavSyncModeOptions();
        LoadWebDavFromConfig();
    }

    private static readonly string[] WebDavSyncModes = ["manual", "startup", "hourly", "daily"];

    private void RefreshWebDavSyncModeOptions()
    {
        var currentKey = ResolveWebDavSyncModeKey();
        _isLoadingWebDav = true;
        try
        {
            var options = new List<SelectItemModel>
            {
                new() { Id = 0, Name = ResourceStrings.WebDavSyncModeManual },
                new() { Id = 1, Name = ResourceStrings.WebDavSyncModeStartup },
                new() { Id = 2, Name = ResourceStrings.WebDavSyncModeHourly },
                new() { Id = 3, Name = ResourceStrings.WebDavSyncModeDaily }
            };
            WebDavSyncModeOptions = options;
            var idx = Array.FindIndex(WebDavSyncModes, m => string.Equals(m, currentKey, StringComparison.OrdinalIgnoreCase));
            SelectedWebDavSyncMode = options[idx < 0 ? 0 : idx];
            UpdateWebDavStatusText();
        }
        finally
        {
            _isLoadingWebDav = false;
        }
    }

    private string ResolveWebDavSyncModeKey()
    {
        if (SelectedWebDavSyncMode != null && SelectedWebDavSyncMode.Id >= 0 && SelectedWebDavSyncMode.Id < WebDavSyncModes.Length)
            return WebDavSyncModes[SelectedWebDavSyncMode.Id];
        return _config.WebDav?.SyncMode ?? "manual";
    }

    private void LoadWebDavFromConfig()
    {
        _isLoadingWebDav = true;
        try
        {
            _config.WebDav ??= new WebDavModel();
            var dav = _config.WebDav;
            WebDavEnabled = dav.Enabled;
            WebDavUrl = dav.Url ?? string.Empty;
            WebDavRemoteFolder = string.IsNullOrWhiteSpace(dav.RemoteFolder) ? "ShiJian" : dav.RemoteFolder;
            WebDavUsername = dav.Username ?? string.Empty;
            WebDavPassword = dav.Password ?? string.Empty;
            WebDavMergeOnSync = dav.MergeOnSync;
            var idx = Array.FindIndex(WebDavSyncModes, m => string.Equals(m, dav.SyncMode ?? "manual", StringComparison.OrdinalIgnoreCase));
            if (WebDavSyncModeOptions.Count > 0)
                SelectedWebDavSyncMode = WebDavSyncModeOptions[idx < 0 ? 0 : idx];
            UpdateWebDavStatusText();
        }
        finally
        {
            _isLoadingWebDav = false;
        }
    }

    private void ApplyWebDavFieldsToConfig()
    {
        _config.WebDav ??= new WebDavModel();
        _config.WebDav.Enabled = WebDavEnabled;
        _config.WebDav.Url = WebDavUrl?.Trim() ?? string.Empty;
        _config.WebDav.RemoteFolder = string.IsNullOrWhiteSpace(WebDavRemoteFolder) ? "ShiJian" : WebDavRemoteFolder.Trim();
        _config.WebDav.Username = WebDavUsername?.Trim() ?? string.Empty;
        _config.WebDav.Password = WebDavPassword ?? string.Empty;
        _config.WebDav.SyncMode = ResolveWebDavSyncModeKey();
        _config.WebDav.MergeOnSync = WebDavMergeOnSync;
    }

    private async Task OnWebDavImmediateSettingChangedAsync()
    {
        if (_isLoadingWebDav) return;
        ApplyWebDavFieldsToConfig();
        await _appConfig.SaveAsync();
    }

    private async Task OnWebDavDebouncedTextChangedAsync()
    {
        if (_isLoadingWebDav) return;
        ApplyWebDavFieldsToConfig();

        _webDavSaveDebounceCts?.Cancel();
        _webDavSaveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _webDavSaveDebounceCts = cts;
        try
        {
            await Task.Delay(450, cts.Token);
            ApplyWebDavFieldsToConfig();
            await _appConfig.SaveAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushWebDavConfigAsync()
    {
        _webDavSaveDebounceCts?.Cancel();
        _webDavSaveDebounceCts?.Dispose();
        _webDavSaveDebounceCts = null;
        ApplyWebDavFieldsToConfig();
        await _appConfig.SaveAsync();
    }

    private void UpdateWebDavStatusText()
    {
        var dav = _config.WebDav;
        if (dav == null || string.IsNullOrWhiteSpace(dav.LastSyncAtUtc))
        {
            WebDavStatusText = ResourceStrings.WebDavNeverSynced;
            return;
        }

        var displayTime = DateTimeOffset.TryParse(dav.LastSyncAtUtc, out var dto)
            ? dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : dav.LastSyncAtUtc;
        var status = string.IsNullOrWhiteSpace(dav.LastSyncStatus) ? "OK" : dav.LastSyncStatus;
        WebDavStatusText = string.Format(ResourceStrings.WebDavLastSyncFormat, displayTime, status);
    }

    private async Task OnDelDataAsync(object obj)
    {
        if (DelDataStartMonthDate > DelDataEndMonthDate)
        {
            _toastService.Toast(ResourceStrings.TimeRangeSelectionError, ToastType.Error, IconTypes.IncidentTriangle);
            return;
        }

        var isConfirm = await _dialogService.ShowConfirmDialogAsync(
            ResourceStrings.DeleteConfirmation,
            ResourceStrings.WantPerformAction);
        if (isConfirm)
        {
            await _dataService.ClearRangeAsync(DelDataStartMonthDate, DelDataEndMonthDate);
            await _webDataService.ClearAsync(DelDataStartMonthDate, DelDataEndMonthDate);
            _toastService.Success(ResourceStrings.OperationCompleted);
        }
    }

    /// <summary>主导出动作：生成 `.shijian-backup`（SQLite VACUUM INTO + SHA-256 + manifest + 图标包）</summary>
    private async Task OnExportDataAsync(object obj)
    {
        try
        {
            var suggestedName = $"shijian-backup-{DateTime.Now:yyyyMMdd-HHmm}.shijian-backup";
            var filePath = await _dialogService.ShowSaveFilePickerAsync(
                suggestedName,
                ".shijian-backup",
                ResourceStrings.BackupFileFilter,
                ["*.shijian-backup"],
                ResourceStrings.ExportData);
            if (string.IsNullOrEmpty(filePath)) return;

            var (data, _) = await _apiClient.ExportBackupAsync(
                includeIcons: true,
                appVersion: AppVersionHelper.GetDisplayVersion());
            await File.WriteAllBytesAsync(filePath, data);
            _toastService.Success(string.Format(ResourceStrings.BackupExportSuccess, Path.GetFileName(filePath)));
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.BackupExportFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
    }

    /// <summary>可选表格导出：按选定月份范围导出应用与网页明细为 CSV</summary>
    private async Task OnExportCsvAsync(object obj)
    {
        try
        {
            if (ExportDataStartMonthDate > ExportDataEndMonthDate)
            {
                _toastService.Toast(ResourceStrings.TimeRangeSelectionError, ToastType.Error, IconTypes.IncidentTriangle);
                return;
            }

            var folder = await _dialogService.ShowFolderPickerAsync();
            if (string.IsNullOrEmpty(folder)) return;

            var exportStart = new DateTime(ExportDataStartMonthDate.Year, ExportDataStartMonthDate.Month, 1);
            var exportEnd = new DateTime(ExportDataEndMonthDate.Year, ExportDataEndMonthDate.Month, DateTime.DaysInMonth(ExportDataEndMonthDate.Year, ExportDataEndMonthDate.Month));
            await _dataService.ExportToExcelAsync(folder, exportStart, exportEnd);
            await _webDataService.ExportAsync(folder, exportStart, exportEnd);
            _toastService.Success(ResourceStrings.DataExportCompleted);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(ResourceStrings.DataExportFailed, ToastType.Error, IconTypes.IncidentTriangle);
        }
    }

    /// <summary>从 `.shijian-backup` 校验完整性并执行 merge / replace 导入恢复（导入前服务端自动生成本地回滚点）</summary>
    private async Task OnImportBackupAsync(object obj)
    {
        try
        {
            var filePath = await _dialogService.ShowOpenFilePickerAsync(
                ResourceStrings.BackupFileFilter,
                ["*.shijian-backup"],
                ResourceStrings.ChooseBackupFile);
            if (string.IsNullOrEmpty(filePath)) return;

            var bytes = await File.ReadAllBytesAsync(filePath);
            var inspect = await _apiClient.InspectBackupAsync(bytes);
            if (!inspect.Valid || !inspect.Compatible)
            {
                var reason = !string.IsNullOrWhiteSpace(inspect.CompatibilityNote)
                    ? inspect.CompatibilityNote
                    : (!inspect.Sha256Verified ? "SHA-256 mismatch" : "Integrity check failed");
                _toastService.Toast(string.Format(ResourceStrings.BackupInvalidFile, reason), ToastType.Error, IconTypes.IncidentTriangle);
                return;
            }

            var createdAt = DateTimeOffset.TryParse(inspect.Manifest.CreatedAtUtc, out var dto)
                ? dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : inspect.Manifest.CreatedAtUtc;
            var counts = inspect.Manifest.Counts;
            var summary = string.Format(
                ResourceStrings.BackupInspectSummary,
                createdAt,
                string.IsNullOrWhiteSpace(inspect.Manifest.DeviceName) ? "-" : inspect.Manifest.DeviceName,
                inspect.Manifest.AppVersion,
                inspect.Manifest.SchemaVersion,
                counts.Apps,
                counts.DailyLogs,
                counts.HoursLogs,
                counts.WebSites,
                counts.WebBrowseLogs,
                counts.Categories,
                FormatByteSize(bytes.LongLength));

            var choice = await _dialogService.ShowActionDialogAsync(
                ResourceStrings.BackupInspectTitle,
                summary,
                [ResourceStrings.BackupModeMerge, ResourceStrings.BackupModeReplace, ResourceStrings.Cancel]);
            if (choice is not (0 or 1)) return;

            var mode = choice == 0 ? "merge" : "replace";
            var result = await _apiClient.ImportBackupAsync(bytes, mode);
            await RefreshAfterImportAsync();

            var modeLabel = mode == "merge" ? ResourceStrings.BackupModeMerge : ResourceStrings.BackupModeReplace;
            _toastService.Success(string.Format(
                ResourceStrings.BackupImportSuccess,
                modeLabel,
                result.Counts.DailyLogs,
                result.Counts.HoursLogs,
                result.Counts.WebBrowseLogs));
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.BackupImportFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
    }

    private async Task OnTestWebDavAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            _toastService.Toast(ResourceStrings.WebDavUrlRequired, ToastType.Error, IconTypes.IncidentTriangle);
            return;
        }

        WebDavBusy = true;
        try
        {
            await FlushWebDavConfigAsync();
            var res = await _apiClient.TestWebDavAsync(new WebDavTestRequest
            {
                Url = WebDavUrl.Trim(),
                Username = WebDavUsername?.Trim() ?? string.Empty,
                Password = WebDavPassword ?? string.Empty,
                RemoteFolder = string.IsNullOrWhiteSpace(WebDavRemoteFolder) ? "ShiJian" : WebDavRemoteFolder.Trim()
            });
            if (res.Ok)
            {
                _toastService.Success(string.Format(ResourceStrings.WebDavTestSuccess, res.Message));
            }
            else
            {
                _toastService.Toast(string.Format(ResourceStrings.WebDavTestFailed, res.Message), ToastType.Error, IconTypes.IncidentTriangle);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.WebDavTestFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
        finally
        {
            WebDavBusy = false;
        }
    }

    private async Task OnSyncWebDavAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            _toastService.Toast(ResourceStrings.WebDavUrlRequired, ToastType.Error, IconTypes.IncidentTriangle);
            return;
        }

        WebDavBusy = true;
        try
        {
            await FlushWebDavConfigAsync();
            var res = await _apiClient.SyncWebDavAsync(WebDavMergeOnSync, AppVersionHelper.GetDisplayVersion());
            _config.WebDav.LastSyncAtUtc = res.SyncedAtUtc;
            _config.WebDav.LastSyncStatus = res.Message;
            UpdateWebDavStatusText();
            if (res.MergedFiles.Count > 0)
                await RefreshAfterImportAsync();

            _toastService.Success(string.Format(ResourceStrings.WebDavSyncSuccess, res.MergedFiles.Count, res.UploadedFile));
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.WebDavSyncFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
        finally
        {
            WebDavBusy = false;
        }
    }

    private async Task OnLoadWebDavBackupsAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            _toastService.Toast(ResourceStrings.WebDavUrlRequired, ToastType.Error, IconTypes.IncidentTriangle);
            return;
        }

        WebDavBusy = true;
        try
        {
            await FlushWebDavConfigAsync();
            var list = await _apiClient.GetWebDavBackupsAsync();
            WebDavRemoteBackups = new System.Collections.ObjectModel.ObservableCollection<RemoteBackupItem>(list);
            OnPropertyChanged(nameof(HasWebDavRemoteBackups));
            WebDavRemoteModalVisible = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.WebDavTestFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
        finally
        {
            WebDavBusy = false;
        }
    }

    private async Task OnRestoreWebDavBackupAsync(object? parameter, string mode)
    {
        if (parameter is not RemoteBackupItem item || string.IsNullOrWhiteSpace(item.FileName)) return;

        WebDavBusy = true;
        try
        {
            var result = await _apiClient.RestoreWebDavBackupAsync(item.FileName, mode);
            WebDavRemoteModalVisible = false;
            await RefreshAfterImportAsync();

            var modeLabel = mode == "merge" ? ResourceStrings.BackupModeMerge : ResourceStrings.BackupModeReplace;
            _toastService.Success(string.Format(
                ResourceStrings.BackupImportSuccess,
                modeLabel,
                result.Counts.DailyLogs,
                result.Counts.HoursLogs,
                result.Counts.WebBrowseLogs));
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(string.Format(ResourceStrings.BackupImportFailed, ex.Message), ToastType.Error, IconTypes.IncidentTriangle);
        }
        finally
        {
            WebDavBusy = false;
        }
    }

    private async Task RefreshAfterImportAsync()
    {
        await LoadBrowsersAsync();
        await LoadMergeGroupsAsync();
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private Task OnCheckUpdateAsync() => _updateCheckerService.ManualCheckForUpdatesAsync();

    private void OnOpenURL(object obj)
    {
        var url = obj?.ToString();
        if (!string.IsNullOrEmpty(url))
            _processService.OpenUrl(url);
    }

    public override void Dispose()
    {
        _webDavSaveDebounceCts?.Cancel();
        _webDavSaveDebounceCts?.Dispose();
        _appConfig.ConfigChanged -= OnConfigChanged;
        base.Dispose();
    }
}
