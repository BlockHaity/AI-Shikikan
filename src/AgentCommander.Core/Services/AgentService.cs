namespace AgentCommander.Core.Services;

public class AgentInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = "idle";
    public DateTime LastActive { get; init; } = DateTime.Now;
}

public class AgentService
{
    private readonly List<AgentInfo> _agents = [];

    public IReadOnlyList<AgentInfo> GetAll() => _agents.AsReadOnly();

    public AgentInfo? GetById(string id) => _agents.FirstOrDefault(a => a.Id == id);

    public AgentInfo Create(string name)
    {
        var agent = new AgentInfo
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Status = "idle"
        };
        _agents.Add(agent);
        return agent;
    }

    public bool Remove(string id)
    {
        var agent = GetById(id);
        return agent is not null && _agents.Remove(agent);
    }

    public int Count => _agents.Count;
}
