namespace Planner.Domain.Identity;

/// <summary>Organisation-wide roles. Exactly one is expected per user; team-level authority is
/// layered on top through <see cref="Planner.Domain.Enums.TeamRole"/>.</summary>
public static class PlannerRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Guest = "guest";

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [Owner] = "Full control of the installation, including instance settings and role assignment.",
        [Admin] = "Manages users, teams and org-wide labels. Cannot change the owner.",
        [Member] = "Standard user. Creates and works on content in the teams they belong to.",
        [Guest] = "Restricted user. Read/comment only, and only in teams they were explicitly added to."
    };
}
