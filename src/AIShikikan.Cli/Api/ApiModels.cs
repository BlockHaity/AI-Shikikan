namespace AIShikikan.Cli.Api;

public class TaskSubmitRequest
{
    public string? AgentId { get; set; }
    public string Task { get; set; } = string.Empty;
    public string Mode { get; set; } = "async";
    public string? WorkingDirectory { get; set; }
    public string? PersonaId { get; set; }
    public string? TemplateId { get; set; }
}

public class TaskSubmitResponse
{
    public string[] AssignmentIds { get; set; } = [];
}

public class AssignmentDto
{
    public string Id { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? OutputTail { get; set; }
}

public class TaskListResponse
{
    public AssignmentDto[] Assignments { get; set; } = [];
}

public class RuntimeStatusResponse
{
    public string Version { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int AgentCount { get; set; }
    public int PersonaCount { get; set; }
    public int TemplateCount { get; set; }
    public int AssignmentCount { get; set; }
    public bool GitRepoAvailable { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}