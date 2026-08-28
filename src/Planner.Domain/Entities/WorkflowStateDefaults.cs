using Planner.Domain.Enums;

namespace Planner.Domain.Entities;

/// <summary>Board columns every new team starts with, mirroring Linear's defaults. Teams can rename,
/// reorder or add states afterwards; only the semantic <see cref="WorkflowStateType"/> is fixed.</summary>
public static class WorkflowStateDefaults
{
    public static IReadOnlyList<WorkflowState> CreateFor(Guid teamId) =>
    [
        New(teamId, "Backlog", WorkflowStateType.Backlog, "#95A2B3", 0, isDefault: false),
        New(teamId, "Todo", WorkflowStateType.Unstarted, "#E2E2E2", 1, isDefault: true),
        New(teamId, "In Progress", WorkflowStateType.Started, "#F2C94C", 2, isDefault: false),
        New(teamId, "In Review", WorkflowStateType.Started, "#5E6AD2", 3, isDefault: false),
        New(teamId, "Done", WorkflowStateType.Completed, "#4CB782", 4, isDefault: false),
        New(teamId, "Canceled", WorkflowStateType.Canceled, "#95A2B3", 5, isDefault: false)
    ];

    private static WorkflowState New(Guid teamId, string name, WorkflowStateType type, string color, int position, bool isDefault) =>
        new()
        {
            TeamId = teamId,
            Name = name,
            Type = type,
            Color = color,
            Position = position,
            IsDefault = isDefault
        };
}
