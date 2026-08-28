using Microsoft.AspNetCore.Identity;

namespace Planner.Domain.Identity;

public class AppRole : IdentityRole<Guid>
{
    public AppRole() { }

    public AppRole(string name, string description) : base(name) => Description = description;

    public string? Description { get; set; }
}
