<script lang="ts">
  import { page } from '$app/state';
  import { authorize, ApiError, type AuthorizeClient } from '$lib/api';
  import { session } from '$lib/auth/session.svelte';
  import Icon from '$components/Icon.svelte';
  import ThemeToggle from '$components/ThemeToggle.svelte';

  /**
   * Consent for a third-party client — an AI assistant connecting over MCP — to act as the signed-in
   * user.
   *
   * The API's /connect/authorize has no screens of its own; it sends the browser here with the
   * original request in `return`. The decision goes back to the API with this app's bearer token,
   * which leaves a short-lived cookie, and the browser then returns to /connect/authorize to collect
   * the code (or the refusal) for the client.
   *
   * The root layout has already made sure someone is signed in before this renders.
   */

  // Anything but our own authorize endpoint is refused rather than followed: this page must never be
  // usable to bounce a signed-in user to an arbitrary address.
  const returnUrl = (() => {
    const value = page.url.searchParams.get('return') ?? '';
    return value.startsWith('/connect/authorize?') ? value : null;
  })();

  const request = returnUrl ? new URL(returnUrl, location.origin).searchParams : null;
  const clientId = request?.get('client_id') ?? null;

  // Shown so the user can recognise where the code is going. OpenIddict has already checked it against
  // the client's registered redirect URIs, so it is information, not a claim the client made up.
  const destination = (() => {
    try {
      return new URL(request?.get('redirect_uri') ?? '').host;
    } catch {
      return null;
    }
  })();

  let client = $state<AuthorizeClient | null>(null);
  let error = $state<string | null>(returnUrl && clientId ? null : 'This sign-in link is incomplete. Start again from the app that sent you here.');
  let busy = $state(false);

  $effect(() => {
    if (!clientId || error) return;

    const controller = new AbortController();

    authorize.client(clientId, { signal: controller.signal }).then(
      (found) => (client = found),
      (failure) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') return;
        error = failure instanceof ApiError && failure.status === 404
          ? 'This app is not registered with your workspace.'
          : 'The request could not be checked. Try again from the app that sent you here.';
      }
    );

    return () => controller.abort();
  });

  async function decide(allow: boolean) {
    if (busy || !clientId || !returnUrl) return;

    busy = true;
    error = null;

    try {
      await authorize.decide(clientId, allow);
      // A full navigation, not goto(): /connect/authorize belongs to the API, and its answer is a
      // redirect to the client, off this application altogether.
      location.assign(returnUrl);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Your answer could not be saved. Try again.';
      busy = false;
    }
  }
</script>

<svelte:head><title>Allow access · Planner</title></svelte:head>

<div class="screen">
  <div class="corner">
    <ThemeToggle />
  </div>

  <section class="card" aria-labelledby="authorize-title">
    <div class="brand">
      <span class="mark" aria-hidden="true">
        <Icon name="layout-grid" size={18} />
      </span>
      <div>
        <h1 id="authorize-title">
          {client ? `Allow ${client.displayName} to use Planner?` : 'Allow access to Planner?'}
        </h1>
        {#if session.user}
          <p class="muted">Signed in as {session.user.displayName} · {session.user.email}</p>
        {/if}
      </div>
    </div>

    {#if error}
      <div class="alert alert-error" role="alert">
        <Icon name="circle-alert" size={15} />
        <span>{error}</span>
      </div>
    {/if}

    {#if client}
      {#if !client.verified}
        <div class="alert alert-warning" role="note">
          <Icon name="triangle-alert" size={15} />
          <span>
            This app registered itself, so “{client.displayName}” is only what it calls itself. Allow it
            only if you just started connecting an assistant to Planner{destination ? ` and expect to go back to ${destination}` : ''}.
          </span>
        </div>
      {/if}
      <p>It will be able to act as you:</p>
      <ul>
        <li>read the teams, projects, issues and documents you can see</li>
        <li>make any change you are allowed to make, in your name</li>
      </ul>
      <p class="muted">
        It can do no more than you can, and loses access if your account is deactivated. Access
        otherwise lasts as long as the app keeps using it.
      </p>
      {#if destination}
        <p class="muted">You will be sent back to <strong>{destination}</strong>.</p>
      {/if}

      <div class="actions">
        <button type="button" class="btn btn-lg" disabled={busy} onclick={() => void decide(false)}>
          Deny
        </button>
        <button type="button" class="btn btn-primary btn-lg" disabled={busy} onclick={() => void decide(true)}>
          {#if busy}
            <Icon name="loader-circle" size={15} class="spin" />
          {/if}
          Allow
        </button>
      </div>
    {:else if !error}
      <p class="muted"><Icon name="loader-circle" size={15} class="spin" /> Checking the request…</p>
    {/if}

    <button type="button" class="btn btn-sm switch" disabled={busy} onclick={() => session.signOut()}>
      Use a different account
    </button>
  </section>
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
    width: min(460px, 100%);
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
  }

  .mark {
    display: grid;
    flex: none;
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

  ul {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
    margin: calc(-1 * var(--s-3)) 0 0;
    padding-left: var(--s-6);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: var(--s-3);
  }

  .switch {
    align-self: flex-start;
  }
</style>
