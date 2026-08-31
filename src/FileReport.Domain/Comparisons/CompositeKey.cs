namespace FileReport.Domain.Comparisons;

public sealed class CompositeKey : IEquatable<CompositeKey>, IComparable<CompositeKey>
{
    private readonly string[] _components;

    public CompositeKey(IEnumerable<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components = components.ToArray();
        if (_components.Length == 0 || _components.Any(string.IsNullOrEmpty))
        {
            throw new DomainException("EmptyKey", "Every key component must be nonempty.");
        }

        Components = Array.AsReadOnly(_components);
    }

    public IReadOnlyList<string> Components { get; }

    public int CompareTo(CompositeKey? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var index = 0; index < Math.Min(_components.Length, other._components.Length); index++)
        {
            var comparison = StringComparer.Ordinal.Compare(_components[index], other._components[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _components.Length.CompareTo(other._components.Length);
    }

    public bool Equals(CompositeKey? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is CompositeKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in _components)
        {
            hash.Add(component, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
