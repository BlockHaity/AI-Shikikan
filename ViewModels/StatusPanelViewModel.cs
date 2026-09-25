using System;
using System.Threading.Tasks;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Gui.Resources;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

/// <summary>侧栏"状态": 当前指挥官上下文占用、会话成本、项目位置与 Git 分支。</summary>
public partial class StatusPanelViewModel : ViewModelBase
{
    private readonly CommanderRuntime _runtime = AppShell.Instance.Runtime;
    private string _sessionId = string.Empty;
    private string _workDir = string.Empty;

    // 上下文
    [ObservableProperty]
    private string _modelText = string.Empty;

    [ObservableProperty]
    private long _contextUsed;

    [ObservableProperty]
    private long _contextTotal;

    [ObservableProperty]
    private bool _hasContextInfo;

    public string ContextTokensText => HasContextInfo
        ? $"{ContextUsed:N0} / {ContextTotal:N0} tokens"
        : string.Empty;

    public double ContextPercent => ContextTotal > 0
        ? Math.Min(100, ContextUsed * 100.0 / ContextTotal)
        : 0;

    public string ContextPercentText => HasContextInfo ? $"{ContextPercent:0.0}%" : string.Empty;

    public string UnknownModelText => HasContextInfo ? string.Empty : Strings.Status_UnknownModel;

    // 费用
    [ObservableProperty]
    private string _costText = "-";

    [ObservableProperty]
    private string _callsText = string.Empty;

    [ObservableProperty]
    private string _priceSourceText = string.Empty;

    [ObservableProperty]
    private bool _isFetchingPrices;

    // 项目
    [ObservableProperty]
    private string _workspaceRoot = string.Empty;

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private bool _isRepoAvailable;

    public StatusPanelViewModel()
    {
        _workDir = _runtime.WorkspaceRoot;
        Refresh();
        _runtime.Engine.OnEvent += OnEngineEvent;
        AppShell.Instance.DataChanged += OnShellDataChanged;
    }

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        Dispatcher.UIThread.Post(RefreshUsage);
    }

    public void SetWorkspace(string workDir)
    {
        _workDir = workDir;
        Dispatcher.UIThread.Post(RefreshRepo);
    }

    private void OnEngineEvent(AgentEngineEvent e)
    {
        if (e is not EngineUsageRecorded) return;
        Dispatcher.UIThread.Post(RefreshUsage);
    }

    private void OnShellDataChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        RefreshRepo();
        RefreshUsage();
    }

    private void RefreshRepo()
    {
        if (string.IsNullOrWhiteSpace(_workDir))
        {
            WorkspaceRoot = string.Empty;
            IsRepoAvailable = false;
            BranchText = Strings.GitPanel_NotRepo;
            return;
        }

        var context = _runtime.Git.ResolveContext(_workDir);
        WorkspaceRoot = context.RepositoryRoot;
        IsRepoAvailable = context.IsValidRepo;
        BranchText = !IsRepoAvailable
            ? Strings.GitPanel_NotRepo
            : context.IsDetachedHead
                ? Strings.Chat_DetachedHead
                : string.IsNullOrWhiteSpace(context.BranchName) ? Strings.Chat_BranchUnknown : context.BranchName;
    }

    private void RefreshUsage()
    {
        var model = _runtime.Llm.ResolveModel();
        var provider = _runtime.Llm.GetProvider();
        ModelText = string.IsNullOrWhiteSpace(model) ? "-" : model;

        var profile = ModelProfileService.Resolve(model, provider?.Id);
        // 模型设置里手动配置的上下文窗口优先于档案值
        ContextTotal = provider?.GetContextTokens(model) ?? profile.ContextTokens;
        HasContextInfo = ContextTotal > 0;
        ContextUsed = UsageStatsService.GetLastContextTokens(_sessionId);

        var stat = UsageStatsService.GetSessionStat(_sessionId);
        CallsText = $"{stat.Calls} 次";
        var cost = ModelProfileService.CalcCostUsd(profile, stat.InputTokens, stat.OutputTokens, stat.CachedTokens);
        CostText = ModelProfileService.FormatCost(cost);

        PriceSourceText = profile.Source switch
        {
            ProfileSource.Manual => Strings.Status_PriceSourceManual,
            ProfileSource.Api => Strings.Status_PriceSourceApi,
            _ => Strings.Status_PriceSourceUnknown
        };

        OnPropertyChanged(nameof(ContextTokensText));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextPercentText));
        OnPropertyChanged(nameof(UnknownModelText));
    }

    /// <summary>从 Provider 的 /v1/models 拉取模型价格与上下文窗口(OpenRouter 兼容端点)。</summary>
    [RelayCommand]
    private async Task RefreshPrices()
    {
        if (IsFetchingPrices) return;

        var provider = _runtime.Llm.GetProvider();
        if (provider is null) return;

        IsFetchingPrices = true;
        try
        {
            var key = string.IsNullOrEmpty(provider.ApiKey)
                ? Environment.GetEnvironmentVariable(provider.EnvKey) ?? string.Empty
                : provider.ApiKey;
            var effective = new ProviderConfig
            {
                Id = provider.Id,
                Name = provider.Name,
                Kind = provider.Kind,
                BaseUrl = provider.BaseUrl,
                ApiKey = key,
                DefaultModel = provider.DefaultModel
            };

            await ModelProfileService.FetchFromApiAsync(effective);
            Dispatcher.UIThread.Post(RefreshUsage);
        }
        catch
        {
            Dispatcher.UIThread.Post(RefreshUsage);
        }
        finally
        {
            IsFetchingPrices = false;
        }
    }
}
