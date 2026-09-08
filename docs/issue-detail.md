# Issue details

Opening a board card or My Issues row opens a workspace detail page. The page and the creation dialog share IssueFormView, including the borderless writing surfaces, property pills, and label chips.

Members and leads can edit the issue and its relationships. Guests with team access can comment and attach files; ordinary team viewers have read access. The API remains responsible for authorization. Navigation warns before discarding issue edits, an unposted comment, or an unfinished attachment link.

Create sub-issue opens the existing dialog with the current issue as parent and its project preselected. Click the parent, a child, or a related issue to open its detail page. Related, Blocks, and Duplicates relationships can be added and removed; incoming relationships have inverted captions.

Comments load all pages. The open page subscribes to the issue's SignalR group, resubscribes after reconnection, and refreshes comments, attachments, and relationships without replacing the issue editor or contribution drafts.

## File storage

Upload file sends up to 20 MiB to POST /api/v1/issues/{id}/files?fileName=….
The API checks comment permission and enforces the size while reading the body.
File names are metadata; generated attachment IDs identify the bytes on disk.

GET /api/v1/attachments/{id}/content checks the issue's read permission and serves an attachment download. File bytes are outside the public web root. The desktop prompts for a save location. HTTP and HTTPS shared-file links are also supported.

Configure Attachments:Path (environment variable Attachments__Path) as an absolute persistent directory for deployed instances. The default is App_Data/attachments beneath the API content root. Back up this directory together with the database. Multiple API instances must share this directory. No database migration is needed.

Deleting metadata or an issue does not currently purge stored file bytes. Keep retention/cleanup in mind when operating the storage directory.

## Verification

Planner.Client.Checks includes fake-HTTP checks for comment pagination and failure recovery, draft preservation during collaboration refresh, successive minimal patches, member/guest/viewer capabilities, relation direction and removal, sub-issue parent persistence, and attachment link/upload/download requests.
