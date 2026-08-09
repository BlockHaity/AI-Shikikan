using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Runtime;

namespace AIShikikan.Cli.Api;

/// <summary>基于 HttpListener 的最小 REST API 服务器: 任务提交(单 Agent 或全体 Roster)/状态/取消。</summary>
public sealed class ApiServer
{
    private readonly CommanderRuntime _runtime;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    public ApiServer(CommanderRuntime runtime, int port = 8090, string host = "localhost")
    {
        _runtime = runtime;
        _listener.Prefixes.Add($"http://{host}:{port}/");
    }

    public string BaseUrl => _listener.Prefixes.First();

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public async Task StopAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch
            {
            }
        }

        _stopped.TrySetResult();
    }

    /// <summary>等待服务停止(Ctrl+C 或外部调用 StopAsync 时返回)。</summary>
    public async Task WaitForShutdownAsync()
    {
        await _stopped.Task;
    }

    private async Task LoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
            }
            catch (Exception)
            {
                break;
            }

            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
            if (path.Length == 0)
            {
                path = "/";
            }

            var method = ctx.Request.HttpMethod;

            if (method == "GET" && path is "/" or "/api/status")
            {
                await WriteStatusAsync(ctx);
            }
            else if (method == "GET" && path == "/api/tasks")
            {
                await WriteTasksAsync(ctx, ListAssignments());
            }
            else if (method == "POST" && path == "/api/tasks")
            {
                await SubmitTaskAsync(ctx);
            }
            else if (method == "GET" && path.StartsWith("/api/tasks/"))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                var a = _runtime.Assignments.Get(id);
                if (a is null)
                {
                    ctx.Response.StatusCode = 404;
                    await WriteErrorAsync(ctx, $"unknown assignment: {id}");
                }
                else
                {
                    await WriteTasksAsync(ctx, new[] { Format(a) });
                }
            }
            else if (method == "POST" && path.StartsWith("/api/tasks/") && path.EndsWith("/cancel"))
            {
                var id = path.Split('/')[^2];
                if (_runtime.Assignments.Get(id) is null)
                {
                    ctx.Response.StatusCode = 404;
                    await WriteErrorAsync(ctx, $"unknown assignment: {id}");
                }
                else
                {
                    _runtime.Assignments.Cancel(id);
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.StatusCode = 204;
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await WriteErrorAsync(ctx, "not found");
            }
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await WriteErrorAsync(ctx, ex.Message);
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task SubmitTaskAsync(HttpListenerContext ctx)
    {
        var request = await JsonSerializer.DeserializeAsync(
            ctx.Request.InputStream, ApiJsonContext.Default.TaskSubmitRequest);
        if (request is null || string.IsNullOrWhiteSpace(request.Task))
        {
            ctx.Response.StatusCode = 400;
            await WriteErrorAsync(ctx, "field 'task' is required");
            return;
        }

        var mode = request.Mode is "sync" or "async" ? request.Mode : "async";

        CliAgentDefinition? single = null;
        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            single = AgentConfigService.Find(request.AgentId);
            if (single is null)
            {
                ctx.Response.StatusCode = 400;
                await WriteErrorAsync(ctx, $"unknown agent: {request.AgentId}");
                return;
            }
        }

        var targets = single is null ? _runtime.Agents : new[] { single };

        var ids = new List<string>();
        foreach (var agent in targets)
        {
            var assignment = Dispatch(agent, request.Task.Trim(), mode, request);
            ids.Add(assignment.AssignmentId);
        }

        ctx.Response.StatusCode = 202;
        await WriteJsonAsync(ctx, new TaskSubmitResponse { AssignmentIds = ids.ToArray() });
    }

    private Assignment Dispatch(CliAgentDefinition agent, string task, string mode, TaskSubmitRequest request)
    {
        var assignment = _runtime.Assignments.Create(
            agent, task,
            templateId: request.TemplateId,
            personaId: request.PersonaId,
            mode: mode,
            workingDirectory: request.WorkingDirectory);

        var personaText = AgentExecutor.ResolvePersonaText(
            agent, _runtime.Personas, _runtime.Templates, request.PersonaId, request.TemplateId);
        var finalPrompt = AgentExecutor.BuildFinalPrompt(assignment.Task, personaText);

        if (mode == "async")
        {
            _runtime.Assignments.StartAsync(assignment, finalPrompt, null);
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runtime.Assignments.RunSyncAsync(assignment, finalPrompt, null);
                }
                catch (Exception ex)
                {
                    assignment.Error = ex.Message;
                }
            });
        }

        return assignment;
    }

    private IEnumerable<AssignmentDto> ListAssignments() =>
        _runtime.Assignments.All.Select(Format);

    private async Task WriteTasksAsync(HttpListenerContext ctx, IEnumerable<AssignmentDto> tasks)
    {
        await WriteJsonAsync(ctx, new TaskListResponse { Assignments = tasks.ToArray() });
    }

    private async Task WriteStatusAsync(HttpListenerContext ctx)
    {
        var status = new RuntimeStatusResponse
        {
            Version = AppInfo.Version,
            Model = _runtime.Llm.ResolveModel(),
            Provider = _runtime.Llm.GetProvider()?.Id ?? string.Empty,
            AgentCount = _runtime.Agents.Count,
            PersonaCount = _runtime.Personas.Count,
            TemplateCount = _runtime.Templates.Count,
            AssignmentCount = _runtime.Assignments.All.Count,
            GitRepoAvailable = _runtime.Git.IsRepoAvailable
        };
        await WriteJsonAsync(ctx, status);
    }

    private static AssignmentDto Format(Assignment a) => new()
    {
        Id = a.AssignmentId,
        AgentName = a.AgentName,
        Task = a.Task,
        Mode = a.Mode,
        Status = a.Status.ToString(),
        StepId = a.StepId,
        WorkingDirectory = a.WorkingDirectory,
        ExitCode = a.ExitCode,
        Error = a.Error,
        CreatedAt = a.CreatedAt,
        FinishedAt = a.FinishedAt,
        OutputTail = a.OutputTail
    };

    private static readonly Dictionary<Type, JsonTypeInfo> JsonInfos = new()
    {
        [typeof(TaskSubmitResponse)] = ApiJsonContext.Default.TaskSubmitResponse,
        [typeof(TaskListResponse)] = ApiJsonContext.Default.TaskListResponse,
        [typeof(RuntimeStatusResponse)] = ApiJsonContext.Default.RuntimeStatusResponse,
        [typeof(ErrorResponse)] = ApiJsonContext.Default.ErrorResponse
    };

    private static async Task WriteJsonAsync<T>(HttpListenerContext ctx, T value)
    {
        var info = JsonInfos[typeof(T)];

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteErrorAsync(HttpListenerContext ctx, string error)
    {
        await WriteJsonAsync(ctx, new ErrorResponse { Error = error });
    }
}