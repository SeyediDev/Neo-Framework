using Neo.AgentOrchestration.Domain.Projects;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void Projects_inherit_the_owning_workspace_and_organization()
    {
        var organization = Organization.Create("sample", "سازمان نمونه");
        var workspace = Workspace.Create(organization, "development", "توسعه");
        var project = Project.Create(workspace, " hyper ", "هایپر");
        Assert.Equal(organization.Id, project.OrganizationId);
        Assert.Equal(workspace.Id, project.WorkspaceId);
        Assert.Equal("HYPER", project.Key);
        project.Rename(workspace.Scope, "هایپریک");
        Assert.Equal("هایپریک", project.Name);
    }

    [Fact]
    public void Foreign_workspace_cannot_change_project_even_in_same_organization()
    {
        var org = Organization.Create("acme", "Acme");
        var first = Workspace.Create(org, "first", "First");
        var second = Workspace.Create(org, "second", "Second");
        var project = Project.Create(first, "app", "Original");
        Assert.Throws<InvalidOperationException>(() => project.Rename(second.Scope, "Changed"));
        Assert.Throws<InvalidOperationException>(() => project.Disable(second.Scope));
        Assert.Equal("Original", project.Name);
        Assert.True(project.IsEnabled);
    }

    [Fact]
    public void Matching_workspace_id_does_not_allow_a_foreign_organization()
    {
        var workspace = Workspace.Create(Organization.Create("org", "Organization"), "work", "Work");
        var project = Project.Create(workspace, "app", "App");
        Assert.Throws<InvalidOperationException>(() =>
            project.RequireScope(new WorkspaceScope(Guid.NewGuid(), workspace.Id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../other")]
    [InlineData("A B")]
    public void Invalid_keys_are_rejected(string key)
        => Assert.ThrowsAny<ArgumentException>(() => Organization.Create(key, "Organization"));

    [Fact]
    public void Empty_scopes_and_disabled_parents_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceScope(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new WorkspaceScope(Guid.NewGuid(), Guid.Empty));
        var org = Organization.Create("org", "Organization");
        var workspace = Workspace.Create(org, "work", "Work");
        workspace.Disable();
        Assert.Throws<InvalidOperationException>(() => Project.Create(workspace, "app", "App"));
        org.Disable();
        Assert.Throws<InvalidOperationException>(() => Workspace.Create(org, "other", "Other"));
    }

    [Fact]
    public void Invalid_rename_keeps_existing_name()
    {
        var workspace = Workspace.Create(Organization.Create("org", "Organization"), "work", "Work");
        var project = Project.Create(workspace, "app", "Original");
        Assert.Throws<ArgumentException>(() => project.Rename(workspace.Scope, new string('x', 201)));
        Assert.Equal("Original", project.Name);
    }
}
