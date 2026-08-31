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
        CommanderRuntime.Boot(Directory.GetCurrentDirectory());
        Runtime = CommanderRuntime.Instance;
        ReloadPersonas();
        ReloadTemplates();
        ReloadAgents();
        SyncProcessList();
        Runtime.Assignments.AssignmentChanged += OnAssignmentChanged;
    }

    /// <summary>子代理分派(与 REST API 同一提示词构建路径): 后台同步执行完成后回调。</summary>
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

    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
