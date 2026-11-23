namespace BLearn.Domain;

/// <summary>A registered source of truth and what it is authoritative about.</summary>
public sealed record SourceOfTruth(string Name, string System, string Scope, Trust MinTrust);

/// <summary>Store-level settings: name, root concept, privacy, and registered sources of truth.</summary>
public sealed class StoreSettings : AggregateRoot
{
    private List<SourceOfTruth> _sources = [];

    public string Name { get; private set; } = "";
    public string Root { get; private set; } = "";
    public bool Private { get; private set; }
    public IReadOnlyList<SourceOfTruth> Sources => _sources;

    private StoreSettings() { }

    public static StoreSettings Rehydrate(string name, string root, bool isPrivate, IEnumerable<SourceOfTruth> sources, long version) =>
        new() { Name = name, Root = root, Private = isPrivate, _sources = sources.ToList(), Version = version };

    /// <summary>Creates settings for a new store. The root concept is created in the same command.</summary>
    public static StoreSettings Create(string name, string root, bool isPrivate)
    {
        name = name.Trim();
        if (name.Length == 0) throw new DomainViolation("store-name", "Store name is required.");
        Ids.Require(root, "Root concept id");
        var s = new StoreSettings { Name = name, Root = root, Private = isPrivate };
        s.Raise(new StoreCreated(name, root, isPrivate));
        return s;
    }

    public SourceOfTruth? FindSource(string name) => _sources.FirstOrDefault(s => s.Name == name);

    public void RegisterSource(string name, string system, string scope, Trust minTrust)
    {
        Ids.Require(name, "Source name");
        Ids.Require(system, "Source system");
        scope = scope.Trim();
        if (scope.Length == 0)
            throw new DomainViolation("source-scope", "Describe what this source is authoritative about.");
        if (FindSource(name) is not null)
            throw new DomainViolation("source-duplicate", $"Source '{name}' is already registered.");
        var source = new SourceOfTruth(name, system, scope, minTrust);
        _sources.Add(source);
        Raise(new SourceRegistered(name, system, scope, minTrust.ToName()));
    }

    public void SetPrivate(bool isPrivate)
    {
        if (Private == isPrivate) return;
        Private = isPrivate;
        Raise(new StorePrivacySet(isPrivate));
    }
}
