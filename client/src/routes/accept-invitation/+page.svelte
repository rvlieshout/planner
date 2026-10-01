<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { resolve } from '$app/paths';
  import { ApiError, invitations, type InvitationDetails } from '$lib/api';
  import { session } from '$lib/auth/session.svelte';
  import { settings } from '$lib/settings.svelte';
  import ThemeToggle from '$components/ThemeToggle.svelte';

  // Invitation credentials stay in memory, never in storage or a query string.
  let userId = $state('');
  let token = $state('');
  let details = $state<InvitationDetails | null>(null);
  let loading = $state(true);
  let busy = $state(false);
  let accepted = $state(false);
  let password = $state('');
  let confirmation = $state('');
  let error = $state<string | null>(null);

  onMount(() => {
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    userId = fragment.get('userId') ?? '';
    token = fragment.get('token') ?? '';
    // Remove the secret from the current history entry before any interaction.
    window.history.replaceState(window.history.state, '', window.location.pathname);
    void inspect();
  });

  async function inspect() {
    loading = true;
    error = null;
    try {
      if (!userId || !token) throw new Error('Open the original invitation link again. If it is incomplete or no longer works, ask your administrator for a new link.');
      details = await invitations.inspect({ userId, token });
    } catch (failure) {
      error = failure instanceof Error ? failure.message : 'Could not check this invitation.';
    } finally {
      loading = false;
    }
  }

  async function accept(event: SubmitEvent) {
    event.preventDefault();
    if (busy || !details || session.isSignedIn) return;
    error = null;
    if (password.length < 12) {
      error = 'Choose a password with at least 12 characters.';
      return;
    }
    if (password !== confirmation) {
      error = 'The two passwords do not match.';
      return;
    }
    busy = true;
    try {
      await invitations.accept({ userId, token, password });
      accepted = true;
      token = '';
      settings.setLastEmail(details.email);
      try {
        await session.signIn(details.email, password);
        await goto(resolve('/'), { replaceState: true });
      } catch {
        error = 'Your account is ready, but automatic sign-in failed. Sign in with the password you just chose.';
      }
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not accept the invitation. Please try again.';
    } finally {
      password = '';
      confirmation = '';
      busy = false;
    }
  }
</script>

<svelte:head>
  <title>Accept invitation · Planner</title>
  <meta name="referrer" content="no-referrer" />
</svelte:head>

<div class="screen">
  <div class="corner"><ThemeToggle /></div>
  <section class="card" aria-labelledby="invitation-title">
    <h1 id="invitation-title">{accepted ? 'Your account is ready' : 'Welcome to Planner'}</h1>
    {#if error}<div class="alert alert-error" role="alert">{error}</div>{/if}
    {#if loading}
      <p role="status">Checking your invitation…</p>
    {:else if accepted}
      {#if busy}
        <p role="status">Signing you in…</p>
      {:else}
        <a class="btn btn-primary" href={resolve('/login')}>Continue to sign in</a>
      {/if}
    {:else if details}
      <p>You’ve been invited as <strong>{details.displayName}</strong> ({details.email}).</p>
      {#if session.isSignedIn}
        <p>You’re currently signed in as {session.user?.email}. Sign out before accepting this invitation.</p>
        <button type="button" class="btn" onclick={() => session.signOut()}>Sign out to continue</button>
        <a href={resolve('/')}>Back to your workspace</a>
      {:else}
        <p class="muted">Choose your own password to activate your account. After signing in, you can add a passkey on a supported device.</p>
        <form onsubmit={accept}>
          <div class="field">
            <label for="invitation-email">Email</label>
            <input id="invitation-email" class="input" type="email" autocomplete="username" value={details.email} readonly />
          </div>
          <div class="field">
            <label for="invitation-password">Choose a password</label>
            <input id="invitation-password" class="input" type="password" autocomplete="new-password"
              minlength="12" required disabled={busy} bind:value={password} aria-describedby="password-help" />
            <p id="password-help" class="muted">Use at least 12 characters. A long, unique passphrase works well.</p>
          </div>
          <div class="field">
            <label for="invitation-confirm">Confirm password</label>
            <input id="invitation-confirm" class="input" type="password" autocomplete="new-password"
              minlength="12" required disabled={busy} bind:value={confirmation} />
          </div>
          <button class="btn btn-primary btn-lg" type="submit" disabled={busy || !password || !confirmation}>
            {busy ? 'Setting up your account…' : 'Accept invitation'}
          </button>
        </form>
      {/if}
    {:else}
      <p class="muted">Invitations expire and can only be used once. If this link no longer works, ask your administrator for a new invitation. No email is sent automatically.</p>
      {#if userId && token}
        <button type="button" class="btn" onclick={() => void inspect()}>Try again</button>
      {/if}
      <a href={resolve('/login')}>Back to sign in</a>
    {/if}
  </section>
</div>

<style>
  .screen { display: grid; place-items: center; min-height: 100%; padding: var(--s-6); background: var(--bg-app); }
  .corner { position: fixed; top: var(--s-5); right: var(--s-5); }
  .card { display: flex; flex-direction: column; gap: var(--s-5); width: min(460px, 100%); padding: var(--s-8); border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--bg-surface); box-shadow: var(--shadow-md); }
  form { display: flex; flex-direction: column; gap: var(--s-5); }
  h1 { font-size: var(--text-lg); }
</style>
