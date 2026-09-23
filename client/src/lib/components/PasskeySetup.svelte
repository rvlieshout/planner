<script lang="ts">
  import { passkeys, passkeyError } from '$lib/auth/passkeys';
  import Icon from '$components/Icon.svelte';
  import ThemeToggle from '$components/ThemeToggle.svelte';

  let { onComplete }: { onComplete: () => void } = $props();
  let name = $state('');
  let busy = $state(false);
  let complete = $state(false);
  let error = $state<string | null>(null);

  async function create(event: SubmitEvent) {
    event.preventDefault();
    if (busy) return;
    busy = true;
    error = null;
    try {
      await passkeys.add(name.trim() || 'My passkey');
      complete = true;
    } catch (failure) {
      error = failure instanceof DOMException &&
        (failure.name === 'NotAllowedError' || failure.name === 'AbortError')
        ? 'Setup was cancelled or timed out. Select Create passkey to try again, or skip this step for now.'
        : passkeyError(failure);
    } finally {
      busy = false;
    }
  }
</script>

<svelte:head><title>Set up your passkey · Planner</title></svelte:head>

<main class="screen">
  <div class="corner"><ThemeToggle /></div>
  <section class="card" aria-labelledby="setup-title">
    <div class="brand"><Icon name="layout-grid" size={18} /> Planner</div>
    <p class="step">{complete ? 'Step 2 of 2 · Ready to go' : 'Step 1 of 2 · Secure your sign-in'}</p>
    {#if complete}
      <div class="mark"><Icon name="check" size={28} /></div>
      <h1 id="setup-title">Your passkey is ready</h1>
      <p role="status">Next time, choose <strong>Sign in with a passkey</strong> to open Planner without your password.</p>
      <p class="muted">You can manage your passkeys or add a backup in Preferences.</p>
      <button type="button" class="btn btn-primary btn-lg btn-block" onclick={onComplete}>Continue to Planner</button>
    {:else}
      <h1 id="setup-title">Set up a passkey</h1>
      <p>You’re signed in. Make your next sign-in easier and more secure with a passkey.</p>
      <ol>
        <li>Select <strong>Create passkey</strong> below.</li>
        <li>Follow your browser’s prompt to save it and confirm with your fingerprint, face, or device PIN.</li>
      </ol>
      <form onsubmit={create}>
        <div class="field">
          <label for="setup-passkey-name">Passkey name <span class="muted">(optional)</span></label>
          <input id="setup-passkey-name" class="input input-lg" bind:value={name} maxlength="100"
            placeholder="For example, work laptop" disabled={busy} />
        </div>
        {#if error}<div class="alert alert-error" role="alert">{error}</div>{/if}
        <button type="submit" class="btn btn-primary btn-lg btn-block" disabled={busy}>
          {#if busy}<Icon name="loader-circle" size={16} class="spin" />{/if}
          {busy ? 'Waiting for your device…' : 'Create passkey'}
        </button>
        {#if busy}<p class="muted" role="status">Follow the prompt from your browser to finish setup.</p>{/if}
      </form>
      <footer>
        <button type="button" class="btn btn-quiet" disabled={busy} onclick={onComplete}>Skip this step</button>
        <p class="muted">You can set this up later in Preferences.</p>
      </footer>
    {/if}
  </section>
</main>

<style>
  .screen { display: grid; place-items: center; height: 100%; overflow: auto; padding: var(--s-8) var(--s-5); background: radial-gradient(circle at 50% -10%, var(--accent-subtle), transparent 55%), var(--bg-app); }
  .corner { position: absolute; top: var(--s-4); right: var(--s-4); }
  .card { display: flex; flex-direction: column; gap: var(--s-5); width: min(480px, 100%); padding: var(--s-8); border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--bg-surface); box-shadow: var(--shadow-md); margin: auto; }
  .brand { display: flex; align-items: center; gap: var(--s-2); font-weight: 600; }
  .step { color: var(--accent); font-size: var(--text-sm); }
  h1 { font-size: var(--text-xl); letter-spacing: -0.02em; }
  ol { padding-left: var(--s-5); display: grid; gap: var(--s-3); }
  form { display: flex; flex-direction: column; gap: var(--s-5); }
  .mark { color: var(--success); }
  footer { text-align: center; border-top: 1px solid var(--border); padding-top: var(--s-4); }
  footer p { margin-top: var(--s-2); font-size: var(--text-sm); }
  @media (width <= 480px) { .card { padding: var(--s-5); } }
</style>
