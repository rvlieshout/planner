# Realtime

A SignalR hub at `/hubs/planner` pushes every change to the clients that care about it, so a board is
never polled.

## Connecting

Authentication is the same bearer token the REST API uses.

```csharp
var connection = new HubConnectionBuilder()
    .WithUrl("http://planner.internal:8080/hubs/planner", options =>
        options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken))
    .WithAutomaticReconnect()
    .Build();
```

The .NET client sends the token as an `Authorization` header. Browsers cannot set headers on a
WebSocket handshake and pass `?access_token=` instead — which is what the web client does, through
`accessTokenFactory`; a middleware lifts it into the header before authentication runs, so both
transports validate identically.

An unauthenticated connection is refused outright — the hub carries `[Authorize]`.

## Groups

On connect the server puts you in a group per team you can read, plus a personal group and an
organisation group, and then tells you which ones you actually got:

```csharp
connection.On<IReadOnlyList<string>>("Subscribed", groups =>
    logger.LogInformation("subscribed to {Groups}", string.Join(", ", groups)));
```

| Group | Carries |
| --- | --- |
| `team:{teamId}` | Board-level traffic: the team, its states, labels, members, projects, milestones, documents and issues. Joined automatically. |
| `issue:{issueId}` | Comments, attachments and relations for one issue. Joined on demand. |
| `user:{userId}` | Messages for one person across all their connections. |
| `org` | Directory changes — users created, renamed, deactivated. |

**Every event goes to exactly one group.** A connection in several groups therefore never receives
the same change twice, and an open board does not stream every comment typed anywhere in the team.

Issue-local traffic is opt-in because it is the high-volume kind. When the user opens an issue:

```csharp
var granted = await connection.InvokeAsync<bool>("SubscribeToIssue", issueId);
// false means the server would not grant it — you cannot read that issue's team
await connection.InvokeAsync("UnsubscribeFromIssue", issueId);   // when the pane closes
```

After a membership change, `Resubscribe()` re-evaluates the team groups without reconnecting:

```csharp
var groups = await connection.InvokeAsync<IReadOnlyList<string>>("Resubscribe");
```

## The envelope

Every server-to-client method takes one `EntityChange<T>`:

```jsonc
{
  "kind": "Updated",          // Created | Updated | Deleted | Archived | Restored
  "entityType": "issue",
  "id": "0199…",
  "teamId": "0199…",
  "projectId": "0199…",       // null when not applicable
  "issueId": "0199…",
  "actorId": "0199…",         // who caused it — skip your own echo if you apply changes optimistically
  "occurredAt": "2026-08-27T15:44:45.4802Z",
  "entity": { … }             // the new state; null for Deleted
}
```

The scope ids sit on the envelope so a client can decide whether a change touches a view it currently
has open without deserialising the payload.

Enums are names here exactly as they are over REST.

## Methods

`IPlannerClient` in `Planner.Contracts` is the authoritative list — reference that project and the
names and payloads are checked at compile time.

| Method | Payload | Group |
| --- | --- | --- |
| `TeamChanged` | `TeamDto` | team |
| `TeamMemberChanged` | `TeamMemberDto` | team |
| `WorkflowStateChanged` | `WorkflowStateDto` | team |
| `LabelChanged` | `LabelDto` | team |
| `ProjectChanged` | `ProjectDto` | team |
| `MilestoneChanged` | `MilestoneDto` | team |
| `DocumentChanged` | `DocumentSummary` (no body — fetch it if the document is open) | team |
| `IssueChanged` | `IssueSummary` | team |
| `CommentChanged` | `CommentDto` | issue |
| `AttachmentChanged` | `AttachmentDto` | issue |
| `IssueRelationChanged` | `IssueRelationDto` | issue |
| `UserChanged` | `UserSummary` | org |
| `Subscribed` | `IReadOnlyList<string>` | caller only |

Client-callable: `SubscribeToIssue(Guid) → bool`, `UnsubscribeFromIssue(Guid)`,
`Resubscribe() → IReadOnlyList<string>`.

## A client sketch

```csharp
connection.On<EntityChange<IssueSummary>>(nameof(IPlannerClient.IssueChanged), change =>
{
    if (change.ActorId == currentUserId)
    {
        return;                                   // our own write, already applied locally
    }

    switch (change.Kind)
    {
        case ChangeKind.Deleted:
        case ChangeKind.Archived:
            board.Remove(change.Id);
            break;
        default:
            board.Upsert(change.Entity!);         // Created and Updated are the same operation to a cache
            break;
    }
});
```

Treating `Created` and `Updated` as one upsert is worth doing: it makes the client idempotent, so a
duplicate delivery or a replay after reconnect cannot corrupt the view.

## Reconnecting

Events are **not** buffered. A client that was disconnected has missed whatever happened meanwhile,
so reconcile over REST on reconnect rather than trusting the socket to have been complete:

```csharp
connection.Reconnected += async _ =>
{
    await connection.InvokeAsync("Resubscribe");
    var since = lastSuccessfulSync.ToString("O");
    var changed = await api.GetAsync($"/api/v1/issues?teamId={teamId}&updatedSince={since}&includeArchived=true");
    // …merge, then move lastSuccessfulSync forward
};
```

`?updatedSince=` is indexed for exactly this. Pass `includeArchived=true` so an issue archived while
you were away arrives and can be removed from the board, rather than silently persisting.

`/api/v1/activity?since=` gives the same window as a narrative, if the client wants to show what
changed rather than just apply it.

## Scaling out

The hub is in-process. Two API replicas would each publish only to their own connections, so a client
attached to replica A would miss changes written on replica B. Running more than one instance needs:

1. A SignalR backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`), and
2. A transactional outbox — publish after the commit *from the outbox*, so a crash between
   `SaveChanges` and the fan-out cannot lose an event.

Neither is here. A single container comfortably serves an on-prem team; add both before adding
replicas.
