using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Projects;

public sealed class Organization : BaseEntity<Guid>
{
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsEnabled { get; private set; }

    // Neo repositories require new(); application commands use the validated factory.
    public Organization() { }

    public static Organization Create(string key, string name) => new()
    {
        Id = Guid.NewGuid(), Key = ProjectRules.Key(key), Name = ProjectRules.Name(name), IsEnabled = true
    };

    public void Rename(string name) => Name = ProjectRules.Name(name);
    public void Disable() => IsEnabled = false;
}
