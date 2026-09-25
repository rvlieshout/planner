using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Planner.Api.Mcp;

/// <summary>Ready-made requests a user can pick in their assistant, so the common questions get the same
/// well-shaped answer every time.</summary>
[McpServerPromptType]
public sealed class PlannerPrompts
{
    [McpServerPrompt(Name = "team_summary", Title = "What happened in a team")]
    [Description("Summarise what a team completed, started and created over a period, and how its projects are progressing.")]
    public static string TeamSummary(
        [Description("The team: its key or name.")] string team,
        [Description("The period, e.g. 'the last 7 days', 'since Monday' or '2026-09-01 to 2026-09-15'.")] string period = "the last 7 days")
        =>
        $"""
        Summarise what happened in the {team} team over {period}.

        Call team_digest once for that team and period. Open individual issues with get_issue only when
        the digest leaves something important unclear.

        Write the summary for a team lead catching up, in this order:

        1. **Highlights**: two to four sentences on what mattered most: finished milestones, big
           issues done, projects that moved.
        2. **Done**: completed issues grouped by project, each as "KEY Title (assignee)". Collapse
           long lists into a count plus the notable ones.
        3. **New**: what came in, and anything urgent or high priority among it.
        4. **In progress**: what is being worked on and by whom. Call out work that went quiet this
           period and anything overdue.
        5. **Projects**: one line each: progress now, how much moved this period, health, target date.
           Mention milestones completed or due in the next two weeks.

        Keep it scannable. Use the people's names and issue keys from the data. Don't invent anything the
        data doesn't show, and say so plainly when a period was quiet.
        """;
}
