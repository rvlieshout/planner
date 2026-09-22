using Planner.Contracts.Enums;
using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Board columns every new team starts with, mirroring Linear's defaults. Teams can rename,
/// reorder or add states afterwards; only the semantic <see cref="WorkflowStateType"/> is fixed.</summary>
public static class WorkflowStateDefaults
{
    public static IReadOnlyList<WorkflowState> CreateFor(Guid teamId)
    {
        var ranks = Rank.Sequence(6);

        return
        [
            New(teamId, "Backlog", WorkflowStateType.Backlog, "#95A2B3", ranks[0], isDefault: false),
            New(teamId, "Todo", WorkflowStateType.Unstarted, "#E2E2E2", ranks[1], isDefault: true),
            New(teamId, "In Progress", WorkflowStateType.Started, "#F2C94C", ranks[2], isDefault: false),
            New(teamId, "In Review", WorkflowStateType.Started, "#5E6AD2", ranks[3], isDefault: false),
            New(teamId, "Done", WorkflowStateType.Completed, "#4CB782", ranks[4], isDefault: false),
            New(teamId, "Canceled", WorkflowStateType.Canceled, "#95A2B3", ranks[5], isDefault: false)
        ];
    }

    private static WorkflowState New(Guid teamId, string name, WorkflowStateType type, string color, string rank, bool isDefault) =>
        new()
        {
            TeamId = teamId,
            Name = name,
            Type = type,
            Color = color,
            Rank = rank,
            IsDefault = isDefault
        };
}
