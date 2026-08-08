namespace AgentCommander.Core.Services.Llm;

public static class SseParser
{
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        string? data = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:"))
            {
                var value = line[5..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }
                data = data is null ? value : $"{data}\n{value}";
            }
            else if (string.IsNullOrEmpty(line) && data is not null)
            {
                yield return data;
                data = null;
            }
        }

        if (data is not null)
        {
            yield return data;
        }
    }
}