<script lang="ts">
  import { ApiError, issues as issuesApi } from '$lib/api';
  import type { Guid, IssueRelationDto, IssueRelationType, IssueSummary } from '$lib/api/types';
  import { ISSUE_RELATION_TYPES } from '$lib/api/types';
  import { relationLabel, STATE_TYPE } from '$lib/meta';
  import { navigate } from '$lib/navigation.svelte';
  import Icon from '$components/Icon.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Related, blocking and duplicate issues.
   *
   * A relation is stored once and shown from both ends, so the caption inverts on the far side:
   * create "A blocks B" and B reports it as "Blocked by A". Cross-team relations are allowed provided
   * both teams can be read, which is why the picker searches everything rather than one team.
   */
  interface Props {
    issueId: Guid;
    relations: IssueRelationDto[];
    canEdit: boolean;
    onchange: (relations: IssueRelationDto[]) => void;
  }

  let { issueId, relations, canEdit, onchange }: Props = $props();

  let adding = $state(false);
  let type = $state<IssueRelationType>('Related');
  let search = $state('');
  let results = $state<IssueSummary[]>([]);
  let searching = $state(false);
  let busy = $state(false);

  let timer: ReturnType<typeof setTimeout> | null = null;

  function onSearchInput() {
    if (timer) clearTimeout(timer);

    const term = search.trim();
    if (term.length < 2) {
      results = [];
      return;
    }

    // Debounced, because the server's search is a trigram match over every issue the caller can read
    // and a request per keystroke is a request per keystroke.
    timer = setTimeout(async () => {
      searching = true;

      try {
        const found = await issuesApi.list({ search: term, pageSize: 10 });
        results = found.items.filter((issue) => issue.id !== issueId);
      } catch {
        results = [];
      } finally {
        searching = false;
      }
    }, 250);
  }

  async function add(target: IssueSummary) {
    busy = true;

    try {
      const created = await issuesApi.addRelation(issueId, { targetIssueId: target.id, type });
      onchange([...relations, created]);
      search = '';
      results = [];
      adding = false;
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'That relation was refused.');
    } finally {
      busy = false;
    }
  }

  async function remove(relation: IssueRelationDto) {
    try {
      await issuesApi.removeRelation(issueId, relation.id);
      onchange(relations.filter((candidate) => candidate.id !== relation.id));
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Removing that relation failed.');
    }
  }

  const grouped = $derived(
    Object.entries(
      relations.reduce<Record<string, IssueRelationDto[]>>((groups, relation) => {
        const caption = relationLabel(relation.type, relation.isOutgoing);
        (groups[caption] ??= []).push(relation);
        return groups;
      }, {})
    )
  );
</script>

<section class="relations">
  <header class="section-header">
    <h2>Relations</h2>
    {#if canEdit}
      <button type="button" class="btn btn-sm btn-quiet" onclick={() => (adding = !adding)}>
        <Icon name="link" size={13} />
        Add
      </button>
    {/if}
  </header>

  {#if adding}
    <div class="adder">
      <div class="row-tight">
        <select bind:value={type} class="select type">
          {#each ISSUE_RELATION_TYPES as option (option)}
            <option value={option}>{relationLabel(option, true)}</option>
          {/each}
        </select>
        <input
          bind:value={search}
          class="input"
          placeholder="Search issues by key or title…"
          oninput={onSearchInput} />
      </div>

      {#if searching}
        <p class="muted small">Searching…</p>
      {:else if results.length > 0}
        <ul class="results">
          {#each results as result (result.id)}
            <li>
              <button type="button" class="result" disabled={busy} onclick={() => void add(result)}>
                <span class="issue-key">{result.key}</span>
                <span class="truncate">{result.title}</span>
              </button>
            </li>
          {/each}
        </ul>
      {:else if search.trim().length >= 2}
        <p class="muted small">No matches.</p>
      {/if}
    </div>
  {/if}

  {#if grouped.length === 0 && !adding}
    <p class="muted small">Nothing related yet.</p>
  {/if}

  {#each grouped as [caption, items] (caption)}
    <div class="group">
      <p class="caption">{caption}</p>
      <ul>
        {#each items as relation (relation.id)}
          <li>
            <button
              type="button"
              class="related"
              onclick={() => void navigate(`/issues/${relation.issueKey}`)}>
              <span style:color={STATE_TYPE[relation.stateType].color}>
                <Icon name={STATE_TYPE[relation.stateType].icon} size={13} />
              </span>
              <span class="issue-key">{relation.issueKey}</span>
              <span class="truncate">{relation.issueTitle}</span>
            </button>

            {#if canEdit}
              <button
                type="button"
                class="btn btn-quiet btn-icon btn-sm"
                onclick={() => void remove(relation)}
                aria-label="Remove relation to {relation.issueKey}">
                <Icon name="x" size={12} />
              </button>
            {/if}
          </li>
        {/each}
      </ul>
    </div>
  {/each}
</section>

<style>
  .relations {
    display: flex;
    flex-direction: column;
    gap: var(--s-4);
  }

  h2 {
    font-size: var(--text-md);
  }

  .group {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
  }

  li {
    display: flex;
    align-items: center;
    gap: var(--s-2);
  }

  .related,
  .result {
    display: flex;
    flex: 1;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
    height: var(--row-h-sm);
    padding: 0 var(--s-3);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg);
    font-size: var(--text-sm);
    text-align: left;
    cursor: pointer;
  }

  .related:hover,
  .result:hover {
    background: var(--bg-hover);
  }

  .adder {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
    padding: var(--s-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
  }

  .type {
    width: 140px;
  }

  .results {
    max-height: 200px;
    overflow-y: auto;
  }

  .small {
    font-size: var(--text-sm);
  }
</style>
