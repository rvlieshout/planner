using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planner.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces the numeric orderings — <c>sort_order</c> on issues, projects and milestones, and
    /// <c>position</c> on workflow states — with lexicographic <c>rank</c> keys (see
    /// <c>Planner.Domain.Common.Rank</c>), collated <c>"C"</c> so Postgres orders them byte-wise.
    ///
    /// <para>Existing rows keep the order they had. Each group is numbered by its old ordering, and row
    /// <c>n</c> gets <c>d</c> followed by <c>n</c> in four base-62 digits: <c>d0001</c>, <c>d0002</c>, …
    /// Those are ordinary integer keys, so the API can place rows before, between and after them straight
    /// away, and four digits cover 14.7 million rows per group.</para>
    /// </summary>
    public partial class LexicographicRanks : Migration
    {
        private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>(table, old column, the group a rank orders within, the old order with its tie-breaks).
        /// The tie-breaks are the ones each list was read with, so ties resolve the way users saw them.</summary>
        private static readonly (string Table, string Column, string Partition, string Order)[] Sequences =
        [
            ("issues", "sort_order", "team_id, state_id", "sort_order, number"),
            ("projects", "sort_order", "team_id", "sort_order, name"),
            ("milestones", "sort_order", "project_id", "sort_order, name"),
            ("workflow_states", "position", "team_id", "position, name")
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_states_team_id_position",
                table: "workflow_states");

            migrationBuilder.DropIndex(
                name: "ix_milestones_project_id_sort_order",
                table: "milestones");

            migrationBuilder.DropIndex(
                name: "ix_issues_team_id_state_id_sort_order",
                table: "issues");

            foreach (var (table, column, partition, order) in Sequences)
            {
                migrationBuilder.AddColumn<string>(
                    name: "rank",
                    table: table,
                    type: "text",
                    nullable: true,
                    collation: "C");

                migrationBuilder.Sql($"""
                    UPDATE {table} AS t
                    SET rank = {RankOf("n.n")}
                    FROM (
                        SELECT id, row_number() OVER (PARTITION BY {partition} ORDER BY {order}, id) AS n
                        FROM {table}
                    ) AS n
                    WHERE t.id = n.id;
                    """);

                migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN rank SET NOT NULL;");

                migrationBuilder.DropColumn(
                    name: column,
                    table: table);
            }

            migrationBuilder.CreateIndex(
                name: "ix_workflow_states_team_id_rank",
                table: "workflow_states",
                columns: new[] { "team_id", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_team_id_rank",
                table: "projects",
                columns: new[] { "team_id", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_milestones_project_id_rank",
                table: "milestones",
                columns: new[] { "project_id", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_team_id_state_id_rank",
                table: "issues",
                columns: new[] { "team_id", "state_id", "rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_states_team_id_rank",
                table: "workflow_states");

            migrationBuilder.DropIndex(
                name: "ix_projects_team_id_rank",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_milestones_project_id_rank",
                table: "milestones");

            migrationBuilder.DropIndex(
                name: "ix_issues_team_id_state_id_rank",
                table: "issues");

            // Back to numbers in the same order: positions count from 0, sort orders step by 1000 as the
            // API used to hand them out.
            foreach (var (table, column, partition, _) in Sequences)
            {
                var isPosition = column == "position";

                if (isPosition)
                {
                    migrationBuilder.AddColumn<int>(name: column, table: table, type: "integer", nullable: true);
                }
                else
                {
                    migrationBuilder.AddColumn<double>(name: column, table: table, type: "double precision", nullable: true);
                }

                migrationBuilder.Sql($"""
                    UPDATE {table} AS t
                    SET {column} = {(isPosition ? "n.n - 1" : "n.n * 1000")}
                    FROM (
                        SELECT id, row_number() OVER (PARTITION BY {partition} ORDER BY rank, id) AS n
                        FROM {table}
                    ) AS n
                    WHERE t.id = n.id;
                    """);

                migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN {column} SET NOT NULL;");

                migrationBuilder.DropColumn(
                    name: "rank",
                    table: table);
            }

            migrationBuilder.CreateIndex(
                name: "ix_workflow_states_team_id_position",
                table: "workflow_states",
                columns: new[] { "team_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_milestones_project_id_sort_order",
                table: "milestones",
                columns: new[] { "project_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_team_id_state_id_sort_order",
                table: "issues",
                columns: new[] { "team_id", "state_id", "sort_order" });
        }

        /// <summary>SQL for the key <c>d</c> + four base-62 digits of <paramref name="n"/>.</summary>
        private static string RankOf(string n) =>
            "'d'" + string.Concat(new[] { 238328, 3844, 62, 1 }.Select(place =>
                $" || substr('{Digits}', (({n} / {place}) % 62)::int + 1, 1)"));
    }
}
