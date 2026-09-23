<script lang="ts">
  import { assertPasskey, passkeyError, passkeysAvailable } from '$lib/auth/passkeys';
  import { ApiError } from '$lib/api';
  import { session } from '$lib/auth/session.svelte';
  import { settings } from '$lib/settings.svelte';
  import Icon from '$components/Icon.svelte';
  import ThemeToggle from '$components/ThemeToggle.svelte';

  /**
   * Sign-in.
   *
   * There is no server field. The desktop client needs one because it is a binary that could be
   * pointed anywhere; this application was served by the installation it talks to, so the address is
   * already settled and asking for it again would only be a way to get it wrong.
   */
  let recovery = $state(false);
  const supportsPasskeys = passkeysAvailable();
  let email = $state(settings.lastEmail ?? '');
  let password = $state('');
  let busy = $state(false);
  let error = $state<string | null>(session.restoreError);

  let passwordField = $state<HTMLInputElement | null>(null);

  /*
   * Someone returning to a remembered address starts in the password box.
   *
   * Once, on mount — not whenever the email field changes. As an effect over `email` this fires again
   * the moment the first character is typed into an empty field, which moves the cursor to the
   * password box mid-address.
   */
  const resuming = Boolean(settings.lastEmail);

  $effect(() => {
    if (resuming && recovery) passwordField?.focus();
  });

  async function signInWithPasskey() {
    if (busy) return;
    busy = true;
    error = null;
    try {
      await session.signInWithPasskey(await assertPasskey());
    } catch (failure) {
      error = passkeyError(failure);
    } finally {
      busy = false;
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();

    if (busy) return;

    busy = true;
    error = null;

    try {
      await session.signIn(email.trim(), password);
      settings.setLastEmail(email.trim());
      password = '';
    } catch (failure) {
      error =
        failure instanceof ApiError
          ? failure.message
          : 'Sign-in failed. Check the server and try again.';
      password = '';
      passwordField?.focus();
    } finally {
      busy = false;
    }
  }
</script>

<div class="screen">
  <div class="corner">
    <ThemeToggle />
  </div>

  <form class="card" onsubmit={submit}>
    <div class="brand">
      <span class="mark" aria-hidden="true">
        <Icon name="layout-grid" size={18} />
      </span>
      <div>
        <h1>Planner</h1>
        <p class="muted">Sign in to your workspace.</p>
      </div>
    </div>

    {#if error}
      <div class="alert alert-error" role="alert">
        <Icon name="circle-alert" size={15} />
        <span>{error}</span>
      </div>
    {/if}

    <button type="button" class="btn btn-primary btn-lg btn-block"
      disabled={busy || !supportsPasskeys} onclick={() => void signInWithPasskey()}>
      {busy && !recovery ? 'Signing in…' : 'Sign in with a passkey'}
    </button>
    <p class="muted">Use your fingerprint, face, device PIN, or security key.</p>
    {#if !supportsPasskeys}
      <p class="muted">Passkeys need a supported browser over HTTPS (or localhost). You can still use setup and recovery below.</p>
    {/if}
    <p class="muted">You’ll stay signed in on this browser. Sign out when using a shared device.</p>
    <button type="button" class="btn btn-sm" disabled={busy} aria-expanded={recovery}
      onclick={() => { recovery = !recovery; error = null; }}>
      {recovery ? 'Hide setup and recovery' : 'First-time setup or recovery'}
    </button>
    {#if recovery}
    <p class="muted">Sign in with your existing password. On supported devices, we’ll guide you through setting up a passkey next. If you’ve lost access, ask your administrator to reset your password and share it with you directly. No email is sent.</p>
    <div class="field">
      <label for="email">Email</label>
      <input
        id="email"
        class="input input-lg"
        type="email"
        autocomplete="username"
        required
        disabled={busy}
        bind:value={email} />
    </div>

    <div class="field">
      <label for="password">Password</label>
      <input
        id="password"
        bind:this={passwordField}
        class="input input-lg"
        type="password"
        autocomplete="current-password"
        required
        disabled={busy}
        bind:value={password} />
    </div>

    <button type="submit" class="btn btn-primary btn-lg btn-block" disabled={busy || !email || !password}>
      {#if busy}
        <Icon name="loader-circle" size={15} class="spin" />
        Signing in…
      {:else}
        Continue with password
      {/if}
    </button>
    {/if}
  </form>
</div>

<style>
  .screen {
    display: grid;
    place-items: center;
    min-height: 100%;
    padding: var(--s-6);
    background:
      radial-gradient(circle at 50% -10%, var(--accent-subtle), transparent 55%),
      var(--bg-app);
  }

  .corner {
    position: fixed;
    top: var(--s-5);
    right: var(--s-5);
  }

  .card {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
    width: min(420px, 100%);
    padding: var(--s-8);
    border: 1px solid var(--border);
    border-radius: var(--radius-lg);
    background: var(--bg-surface);
    box-shadow: var(--shadow-md);
  }

  .brand {
    display: flex;
    align-items: center;
    gap: var(--s-4);
    margin-bottom: var(--s-2);
  }

  .mark {
    display: grid;
    place-items: center;
    width: 34px;
    height: 34px;
    border-radius: var(--radius-md);
    background: var(--accent);
    color: var(--fg-on-accent);
  }

  h1 {
    font-size: var(--text-lg);
    letter-spacing: -0.01em;
  }

  .brand p {
    font-size: var(--text-sm);
  }
</style>
