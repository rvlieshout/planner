<script lang="ts">
  import { onMount } from 'svelte';
  import { passkeys, passkeyError, passkeysAvailable, type SavedPasskey } from '$lib/auth/passkeys';
  import { confirm } from '$components/confirm.svelte';

  let keys = $state<SavedPasskey[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let name = $state('');
  let error = $state<string | null>(null);
  let message = $state<string | null>(null);
  const supported = passkeysAvailable();

  onMount(() => { void load(); });
  async function load() {
    try { keys = await passkeys.list(); }
    catch (failure) { error = passkeyError(failure); }
    finally { loading = false; }
  }
  async function add(event: SubmitEvent) {
    event.preventDefault();
    if (busy || !name.trim()) return;
    busy = true; error = null; message = null;
    try {
      await passkeys.add(name.trim());
      name = '';
      message = 'Passkey added. You can now sign in without your Planner password.';
      await load();
    } catch (failure) { error = passkeyError(failure); }
    finally { busy = false; }
  }
  async function remove(key: SavedPasskey) {
    if (busy || !await confirm.ask({
      title: 'Remove passkey?',
      message: `“${key.name ?? 'Passkey'}” will no longer work for new sign-ins. ${keys.length === 1 ? 'You will need your password or administrator assistance until you add another passkey.' : 'Your other passkeys will still work.'} Existing sessions stay signed in.`,
      confirmLabel: 'Remove passkey', cancelLabel: 'Cancel', danger: true
    })) return;
    busy = true; error = null; message = null;
    try { await passkeys.remove(key.id); await load(); message = 'Passkey removed.'; }
    catch (failure) { error = passkeyError(failure); }
    finally { busy = false; }
  }
</script>

<section class="panel" aria-label="Passkeys">
  <div class="panel-title"><span>Passkeys</span></div>
  <p>Sign in with your fingerprint, face, device PIN, or security key. You’ll stay signed in on this browser.</p>
  <p class="muted">Add a second passkey on another device or security key as a backup. If you lose all your passkeys, use your existing password or ask your administrator for a password reset. No email is needed.</p>
  {#if error}<div class="alert alert-error" role="alert">{error}</div>{/if}
  {#if message}<p role="status">{message}</p>{/if}
  {#if loading}
    <p class="muted" role="status">Loading passkeys…</p>
  {:else if keys.length === 0}
    <p>No passkeys yet. Add one below to finish setting up passwordless sign-in.</p>
  {:else}
    <ul>
      {#each keys as key (key.id)}
        <li>
          <span>{key.name ?? 'Passkey'} <small class="muted">Added {new Date(key.createdAt).toLocaleDateString()}</small></span>
          <button type="button" class="btn btn-sm" disabled={busy} onclick={() => void remove(key)} aria-label={`Remove ${key.name ?? 'passkey'}`}>Remove</button>
        </li>
      {/each}
    </ul>
  {/if}
  {#if supported}
    <form onsubmit={add}>
      <div class="field">
        <label for="passkey-name">Passkey name</label>
        <input id="passkey-name" class="input" bind:value={name} maxlength="100" placeholder="For example, work laptop or backup key" required disabled={busy || loading} />
      </div>
      <button class="btn btn-primary btn-sm" type="submit" disabled={busy || loading || !name.trim() || keys.length >= 10}>
        {busy ? 'Please wait…' : 'Add a passkey'}
      </button>
    </form>
  {:else}
    <p class="muted">Open Planner in a browser that supports passkeys using HTTPS (or localhost) to add one.</p>
  {/if}
</section>

<style>
  section, form { display: flex; flex-direction: column; gap: var(--s-4); }
  form { align-items: flex-start; }
  .field { width: 100%; }
  ul { list-style: none; padding: 0; margin: 0; }
  li { display: flex; justify-content: space-between; align-items: center; gap: var(--s-4); padding: var(--s-3) 0; border-bottom: 1px solid var(--border); }
  small { display: block; }
</style>
