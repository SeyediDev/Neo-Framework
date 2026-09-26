using Neo.AgentOrchestration.Application.Agents;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class AgentProfileTests
{
    private static Workspace Workspace() => Domain.Projects.Workspace.Create(
        Organization.Create("org", "Organization"), "work", "Workspace");

    [Fact]
    public void Profiles_are_distinct_executors_for_a_role()
    {
        var workspace = Workspace();
        var role = RoleProfile.Create(workspace, "quality", "Quality", "Review test evidence");
        var agent = AgentProfile.Create(workspace.Scope, role, "reviewer", "Reviewer", "http", "model-a",
            "Review the change.", ".agents/skills/review/SKILL.md");
        Assert.Equal(role.Id, agent.RoleProfileId);
        Assert.Equal(role.WorkspaceId, agent.WorkspaceId);
        Assert.Equal("http", agent.Provider);
        Assert.Same(agent, AgentSelector.Select(workspace.Scope, role, [agent]));
    }

    [Fact]
    public void Disabled_profiles_and_roles_cannot_be_selected()
    {
        var workspace = Workspace();
        var role = RoleProfile.Create(workspace, "quality", "Quality");
        var agent = AgentProfile.Create(workspace.Scope, role, "reviewer", "Reviewer", "http");
        agent.SetEnabled(workspace.Scope, false);
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, role, [agent]));
        agent.SetEnabled(workspace.Scope, true);
        role.SetEnabled(workspace.Scope, false);
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, role, [agent]));
        Assert.Throws<InvalidOperationException>(() => AgentProfile.Create(workspace.Scope, role, "new", "New", "http"));
        role.SetEnabled(workspace.Scope, true);
        Assert.Same(agent, AgentSelector.Select(workspace.Scope, role, [agent]));
    }

    [Fact]
    public void Ambiguous_selection_requires_an_explicit_profile()
    {
        var workspace = Workspace();
        var role = RoleProfile.Create(workspace, "quality", "Quality");
        var first = AgentProfile.Create(workspace.Scope, role, "first", "First", "http");
        var second = AgentProfile.Create(workspace.Scope, role, "second", "Second", "http");
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, role, [first, second]));
        Assert.Same(second, AgentSelector.Select(workspace.Scope, role, [first, second], second.Id));
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, role, [first], second.Id));
    }

    [Fact]
    public void Foreign_workspaces_and_roles_cannot_supply_or_update_a_profile()
    {
        var workspace = Workspace();
        var foreign = Workspace();
        var role = RoleProfile.Create(workspace, "quality", "Quality");
        var otherRole = RoleProfile.Create(foreign, "quality", "Foreign quality");
        var agent = AgentProfile.Create(foreign.Scope, otherRole, "other", "Other", "http");
        Assert.Throws<InvalidOperationException>(() => AgentProfile.Create(foreign.Scope, role, "bad", "Bad", "http"));
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, role, [agent]));
        Assert.Throws<InvalidOperationException>(() => role.SetEnabled(foreign.Scope, false));
        Assert.Throws<InvalidOperationException>(() => agent.Update(workspace.Scope, "Bad", "http", null, null, null));
        Assert.True(role.IsEnabled);
        Assert.Equal("Other", agent.Name);
    }

    [Theory]
    [InlineData("../other/SKILL.md")]
    [InlineData("/absolute/SKILL.md")]
    [InlineData("C:\\secret\\SKILL.md")]
    [InlineData("skills//review")]
    [InlineData("skills/./review")]
    public void Invalid_skill_path_cannot_partially_change_a_profile(string path)
    {
        var workspace = Workspace();
        var role = RoleProfile.Create(workspace, "quality", "Quality");
        var agent = AgentProfile.Create(workspace.Scope, role, "review", "Original", "http", "original");
        Assert.Throws<ArgumentException>(() => agent.Update(workspace.Scope, "Changed", "fake", "other", null, path));
        Assert.Equal("Original", agent.Name);
        Assert.Equal("http", agent.Provider);
        Assert.Equal("original", agent.Model);
    }

    [Fact]
    public void Same_workspace_other_role_is_not_a_candidate()
    {
        var workspace = Workspace();
        var review = RoleProfile.Create(workspace, "review", "Review");
        var implement = RoleProfile.Create(workspace, "implement", "Implement");
        var agent = AgentProfile.Create(workspace.Scope, implement, "coder", "Coder", "http");
        Assert.Throws<InvalidOperationException>(() => AgentSelector.Select(workspace.Scope, review, [agent]));
    }
}
