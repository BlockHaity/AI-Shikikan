using System;
using System.Threading.Tasks;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Core.Services.Worker;
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

    private bool _repoRefreshQueued;

    // ── Worker 执行位置(降级可见性的兜底) ─────────────────────────────
    // 「悄悄退回同进程内联执行」是引入 Worker 后最危险的一类回归: 工具照常能跑、界面毫无异常,
    // 只是子代理又回到主进程、重扫描又卡 UI —— 用户唯一能察觉的只有「最近变慢了」, 却无从归因。
    // 所以状态栏必须把这一档显式显示出来(doctor 只在用户主动敲命令时才看得到, 顶不上这个位置)。

    /// <summary>Worker 模式轮询间隔; 与聊天页排队计数轮询同节奏。</summary>
    private const int WorkerPollIntervalMs = 500;

    private readonly DispatcherTimer _workerPollTimer;
    private string _workerModeText = string.Empty;
    private bool _isWorkerDegraded;

    /// <summary>Worker 模式的一行文案(独立进程 / ⚠ 进程内)。</summary>
    public string WorkerModeText => _workerModeText;

    /// <summary>当前是否有会话的工具退回主进程内执行(界面据此高亮)。</summary>
    public bool IsWorkerDegraded => _isWorkerDegraded;

    /// <summary>Worker 模式的解释文案(说明这一档为什么值得注意)。</summary>
    public string WorkerModeTip => IsWorkerDegraded
        ? Strings.Status_WorkerInlineTip
        : Strings.Status_WorkerPipeTip;

    public StatusPanelViewModel()
    {
        _workDir = _runtime.WorkspaceRoot;

        // Worker 模式轮询: 降级没有任何事件可订阅 —— 池是在别的线程上把某个槽位钉死成内联的,
        // 状态变化既不经过 EngineEventHub 也不经过 AppShell.DataChanged, 只能轮询。
        // 500ms / DispatcherPriority.Background 与聊天页的排队计数轮询完全一致, 不与渲染抢优先级。
        // 代价可忽略: GetHealth() 内部对 Worker 可执行文件定位有 5s 节流, 两次探测之间只读内存里的组表。
        _workerPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(WorkerPollIntervalMs)
        };
        _workerPollTimer.Tick += (_, _) => RefreshWorkerMode();

        Refresh();
        RefreshWorkerMode(); // 首帧先出结论, 避免刚进面板要等 500ms 才显示模式
        _workerPollTimer.Start();
        _runtime.Engine.OnEvent += OnEngineEvent;
        AppShell.Instance.DataChanged += OnShellDataChanged;
    }

    /// <summary>
    /// 读一次 Worker 池健康快照, 更新状态栏的模式标记(UI 线程调用)。
    /// </summary>
    /// <remarks>
    /// <para>轮询处理器里<b>不允许抛异常</b>: 一个未处理异常会沿 DispatcherTimer 冒到 UI 循环,
    /// 表现是整个界面卡死。故整体兜底 try/catch, 失败时保留上一次结论(而不是谎报"正常")。</para>
    /// <para><b>为什么不用 <c>WorkerHealth.ModeText</c></b>: 它是 Core 里的硬编码中文,
    /// 英文界面下会直接露出中文。这里按枚举自行映射到 resx 键, 保证双语一致。</para>
    /// <para><b>为什么 <c>NotFound</c> 与 <c>Inline</c> 合并成一档</b>: 前者是「压根没定位到 Worker 可执行文件」,
    /// 后者是「拉起失败/已钉死降级」—— 成因不同, 但对用户的结论完全一样(工具跑在主进程里, 失去隔离),
    /// 分成两行只会让状态栏变啰嗦。区分靠 doctor 的逐条输出。</para>
    /// </remarks>
    private void RefreshWorkerMode()
    {
        try
        {
            var pool = _runtime.Workers;
            if (pool is null) return;

            // NotConnected(已定位但此刻无存活实例)不算降级 —— 会话还没开过第一回合本就是这个状态(懒启动)
            var degraded = pool.GetHealth().Mode is WorkerMode.Inline or WorkerMode.NotFound;
            var text = degraded ? Strings.Status_WorkerInline : Strings.Status_WorkerPipe;

            // 比对文案而不只是标志位: 顺带让语言切换(Strings.Culture 变了, 文案随之变)也能刷新出来。
            if (degraded == _isWorkerDegraded && text == _workerModeText) return;

            _isWorkerDegraded = degraded;
            _workerModeText = text;
            OnPropertyChanged(nameof(WorkerModeText));
            OnPropertyChanged(nameof(IsWorkerDegraded));
            OnPropertyChanged(nameof(WorkerModeTip));
        }
        catch (Exception ex)
        {
            // 吞掉并保留上一次的结论: 谎报"正常"比显示旧值更糟
            AIShikikan.Core.Logging.Log.Debug("StatusPanel", $"读取 Worker 模式失败: {ex.Message}");
        }
    }

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        Dispatcher.UIThread.Post(RefreshUsage);
    }

    public void SetWorkspace(string workDir)
    {
        _workDir = workDir;
        RefreshRepo();
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

    /// 把仓库信息刷新合并到调度器下一轮: 内部含一次同步 git 子进程, 而 AppShell.DataChanged
    /// 一次 reload 会连着打到 AgentPanel / GitPanel / StatusPanel 三家, 合并后同一次交互只跑一次。
    private void RefreshRepo()
    {
        if (_repoRefreshQueued) return;
        _repoRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repoRefreshQueued = false;
            RefreshRepoNow();
        });
    }

    private void RefreshRepoNow()
    {
        if (string.IsNullOrWhiteSpace(_workDir))
        {
            WorkspaceRoot = string.Empty;
            IsRepoAvailable = false;
            BranchText = Strings.GitPanel_NotRepo;
            return;
        }

        var context = _runtime.GitService.ResolveContext(_workDir);
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
        // 待补 i18n 键(未改): "次" 是硬编码, 英文界面显示 "3 次"。
        // 不复用 Home_TimesFmt 是因为它是 Home_ 前缀的 "N 次调用", 与这里语义不同, 跨视图借用不合适。
        CallsText = $"{stat.Calls} 次";
        var cost = ModelProfileService.CalcCostUsd(profile, stat.InputTokens, stat.OutputTokens, stat.CachedTokens);
        CostText = ModelProfileService.FormatCost(cost);

        PriceSourceText = profile.Source switch
        {
            ProfileSource.Manual => Strings.Status_PriceSourceManual,
            ProfileSource.Api => Strings.Status_PriceSourceApi,
            _ => FormatPriceSourceUnknown(provider)
        };

        OnPropertyChanged(nameof(ContextTokensText));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextPercentText));
        OnPropertyChanged(nameof(UnknownModelText));
    }

    /// <summary>未收录价格时的文案: 若最近一次拉取失败, 展示失败原因而非笼统的"未收录"。</summary>
    private static string FormatPriceSourceUnknown(ProviderConfig? provider)
    {
        if (string.IsNullOrEmpty(provider?.Id)) return Strings.Status_PriceSourceUnknown;

        var error = ModelProfileService.LoadApiCache().Error;
        return string.IsNullOrWhiteSpace(error)
            ? Strings.Status_PriceSourceUnknown
            : string.Format(Strings.Status_PriceFetchFailed, error);
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
        catch (Exception ex)
        {
            // 拉取失败原因由 ModelProfileCache.Error 承载, 在 RefreshUsage 中展示;
            // 这里仅兜底捕获以避免命令异常冒泡, 同时记日志便于排查。
            AIShikikan.Core.Logging.Log.Warn("StatusPanel", ex, "价格拉取失败");
            Dispatcher.UIThread.Post(RefreshUsage);
        }
        finally
        {
            IsFetchingPrices = false;
        }
    }
}
