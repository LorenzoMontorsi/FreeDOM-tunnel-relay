namespace FreeDom.Server;

/// <summary>A set of ports written as "1024-65535" or "2102,8000,20000-40000".</summary>
sealed class PortSet
{
    private readonly List<(int Min, int Max)> _ranges;

    private PortSet(List<(int, int)> ranges) => _ranges = ranges;

    public static PortSet All { get; } = new([(1024, 65535)]);

    public static PortSet Parse(string text, string optionName)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out int min) ||
                !int.TryParse(bounds.Length == 2 ? bounds[1] : bounds[0], out int max) ||
                min < 1 || max > 65535 || min > max)
                throw new ArgumentException($"invalid {optionName} '{text}', expected e.g. 2102,8000,20000-40000");
            ranges.Add((min, max));
        }
        if (ranges.Count == 0) throw new ArgumentException($"{optionName} is empty");
        return new PortSet(ranges);
    }

    public bool Contains(int port) => _ranges.Any(r => port >= r.Min && port <= r.Max);

    /// <summary>A uniformly random port from the set.</summary>
    public int Random()
    {
        long total = _ranges.Sum(r => (long)(r.Max - r.Min + 1));
        long pick = System.Random.Shared.NextInt64(total);
        foreach (var (min, max) in _ranges)
        {
            long size = max - min + 1;
            if (pick < size) return (int)(min + pick);
            pick -= size;
        }
        return _ranges[0].Min;
    }

    public override string ToString() =>
        string.Join(",", _ranges.Select(r => r.Min == r.Max ? $"{r.Min}" : $"{r.Min}-{r.Max}"));
}
