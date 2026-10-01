using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Templates;
using Avalonia.Threading;

namespace AIShikikan.Gui.ViewModels;

/// <summary>GUI 全局外壳: 单一 CommanderRuntime、可刷新的展示集合与子代理进程列表。</summary>
public sealed class AppShell
{
    public static AppShell Instance { get; } = new();

    public CommanderRuntime Runtime { get; }

    public ObservableCollection<Persona> Personas { get; } = [];

    public ObservableCollection<AgentTemplate> Templates { get; } = [];

    public ObservableCollection<CliAgentDefinition> Agents { get; } = [];

    public ObservableCollection<Assignment> ProcessList { get; } = [];

    public event Action? DataChanged;

    private AppShell()
    {
        // ⚠ 已知架构问题(未修, 见报告 F3): 工作目录被硬绑到进程 CWD, 与 preferences.toml 里
        // 可能存在的默认工作目录设置无关。本构造函数在 MainWindowViewModel 构造链里首次触碰
        // AppShell.Instance 时触发, 即整台 Core Runtime 的 workspaceRoot 由此确定。
        // 彻底修需要"持久化 workspaceRoot + 启动时读取"(ThemeService/PreferenceService 层), 改动面大。
        CommanderRuntime.Boot(Directory.GetCurrentDirectory());
        Runtime = CommanderRuntime.Instance;
        ReloadPersonas();
        ReloadTemplates();
        ReloadAgents();
        SyncProcessList();
        Runtime.Assignments.AssignmentChanged += OnAssignmentChanged;
    }

    /// <summary>
    /// Agent 面板的手动分派(与工具层的 run_&lt;agent&gt; 并行的一条独立路径):
    /// 复用同一套提示词构建(ResolvePersonaText + BuildFinalPrompt), 后台同步执行完成后回调。
    /// </summary>
    /// <remarks>
    /// 与 run_&lt;agent&gt; 的语义差异(⚠ 尚未拉齐, 见报告): planMode 已转发(下面 planMode 参数),
    /// 但**不向 WorkspaceExecutionCoordinator 申请工作区执行权**(因此不与其他分支的活跃会话互斥)、
    /// **不走工具审批**、**不做输出压缩**。待 AgentToolFactory 接好协调器后, 本方法也应走同一条申请路径。
    /// </remarks>
    public void Dispatch(CliAgentDefinition agent, string task,
        string? personaId = null, string? templateId = null, string? workingDirectory = null,
        Action<Assignment, CliAgentRunResult?>? onFinished = null,
        bool useCommanderPersona = false)
    {
        var assignment = Runtime.Assignments.Create(
            agent, task, templateId, personaId, workingDirectory);
        var personaText = AgentExecutor.ResolvePersonaText(
            agent, Runtime.Personas, Runtime.Templates, personaId, templateId,
            Runtime.CurrentPersonaText, useCommanderPersona,
            planMode: Runtime.IsPlanMode);
        var finalPrompt = AgentExecutor.BuildFinalPrompt(assignment.Task, personaText);

        _ = Task.Run(async () =>
        {
            try
            {
                var (_, run) = await Runtime.Assignments.RunSyncAsync(assignment, finalPrompt, null);
                onFinished?.Invoke(assignment, run);
            }
            catch (Exception ex)
            {
                var now = DateTime.Now;
                onFinished?.Invoke(assignment, new CliAgentRunResult
                {
                    ExitCode = -1,
                    Output = ex.Message,
                    TimedOut = false,
                    Elapsed = TimeSpan.Zero,
                    StartedAt = now,
                    CompletedAt = now
                });
            }
        });
    }

    private void OnAssignmentChanged(Assignment assignment)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SyncProcessList();
        }
        else
        {
            Dispatcher.UIThread.Post(SyncProcessList);
        }
    }

    private void SyncProcessList()
    {
        var all = Runtime.Assignments.All;
        for (var i = ProcessList.Count - 1; i >= 0; i--)
        {
            if (all.All(a => a.AssignmentId != ProcessList[i].AssignmentId))
            {
                ProcessList.RemoveAt(i);
            }
        }

        var existing = new HashSet<string>(ProcessList.Select(a => a.AssignmentId), StringComparer.Ordinal);
        foreach (var a in all)
        {
            if (!existing.Contains(a.AssignmentId))
            {
                ProcessList.Add(a);
            }
        }
    }

    public void ReloadPersonas()
    {
        Sync(Personas, PersonaService.LoadAll());
        DataChanged?.Invoke();
    }

    public void ReloadTemplates()
    {
        Sync(Templates, AgentTemplateService.LoadAll());
        DataChanged?.Invoke();
    }

    public void ReloadAgents()
    {
        AgentConfigService.Refresh();
        Sync(Agents, AgentConfigService.LoadAll());
        DataChanged?.Invoke();
    }

    /// <summary>通知订阅方(如聊天页)刷新展示数据。</summary>
    public void NotifyDataChanged() => DataChanged?.Invoke();

    /// <summary>
    /// 用 source 全量替换 target 的内容。
    /// </summary>
    /// <remarks>
    /// 刻意保留 Clear() + 逐个 Add() 而不换 AvaloniaList/AvaloniaList 的 Reset 优化:
    /// 这些集合被 ItemsControl 直接绑定, Clear 会让容器整体重建(与聊天页同源的
    /// "Material 主题过渡 NRE"风险), 且本类 Reload* 只在设置变更时触发, N 次通知的代价可接受。
    /// 换成 AvaloniaList 可用 ResetBehavior 一次通知, 但要改所有绑定点的集合类型, 收益不足。
    /// </remarks>
    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
