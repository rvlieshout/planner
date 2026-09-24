# Issue details

> Describes the **frozen** desktop client's issue page. The web client's equivalent — same shape,
> same rules — is in [web-client.md](web-client.md); the "File storage" section below applies to
> both, because it describes the API.

Opening a board card or My Issues row opens a workspace detail page. A separate breadcrumb and save toolbar stays above two independently scrolling columns. The main column contains the title, description, sub-issues, related issues, and comments. The right column contains the editable properties, labels, and attachments. Both the page and creation dialog use the same issue editor view model.

Sub-issues use the dense My Issues row layout, with priority, issue key, status, title, labels, and updated time. Click to select; double-click or press Enter to open the selected issue.

Members and leads can edit the issue and its relationships. Guests with team access can comment and attach files; ordinary team viewers have read access. The API remains responsible for authorization. Navigation warns before discarding issue edits, an unposted comment, or an unfinished attachment link.

Create sub-issue opens the existing dialog with the current issue as parent and its project preselected. Click the parent, a child, or a related issue to open its detail page. Related, Blocks, and Duplicates relationships can be added and removed; incoming relationships have inverted captions.

Comments load all pages. The open page subscribes to the issue's SignalR group, resubscribes after reconnection, and refreshes comments, attachments, and relationships without replacing the issue editor or contribution drafts.

## File storage

Upload file sends up to 20 MiB to POST /api/v1/issues/{id}/files?fileName=….
The API checks comment permission and enforces the size while reading the body.
File names are metadata; generated attachment IDs identify the bytes on disk.

GET /api/v1/attachments/{id}/content checks the issue's read permission and serves an attachment download. File bytes are outside the public web root. The desktop prompts for a save location. HTTP and HTTPS shared-file links are also supported.

Attachments:Path (environment variable Attachments__Path) must be an absolute, writable directory in any container deployment: the default, App_Data/attachments beneath the API content root, sits inside the application folder, which the image's non-root user cannot create — uploads fail with a 500. Both docker-compose.yml and docker-compose.coolify.yml now set it to /var/lib/planner/attachments and mount a volume there. Back up that directory together with the database; the rows point at files that exist only there. Multiple API instances must share it. No database migration is needed.

Removing an attachment deletes its bytes first and keeps the row if that fails, so removal can be retried. Deleting an issue deletes the rows first and then the bytes of its uploaded files; a file that cannot be removed is logged and left behind rather than blocking the delete, so the storage directory may still hold the occasional orphan.

## Verification

The desktop client's `Planner.Client.Checks` project, removed with the client itself, covered comment pagination and failure recovery, draft preservation during collaboration refresh, successive minimal patches, member/guest/viewer capabilities, relation direction and removal, sub-issue parent persistence, and attachment link/upload/download requests.
