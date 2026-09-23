<script lang="ts">
  import '$lib/styles/app.css';

  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { resolve } from '$app/paths';
  import { setUnauthorizedHandler } from '$lib/api';
  import { session } from '$lib/auth/session.svelte';
  import { settings } from '$lib/settings.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import ConfirmHost from '$components/ConfirmHost.svelte';
  import LightboxHost from '$components/LightboxHost.svelte';
  import ToastHost from '$components/ToastHost.svelte';
  import Icon from '$components/Icon.svelte';
  import PasskeySetup from '$components/PasskeySetup.svelte';

  /**
   * The outermost shell: the theme, the session, and the two hosts that any screen can call into.
   *
   * It routes on session state and presents passkey onboarding before mounting the workspace.
   * Everything about the signed-in workspace belongs to the (app) layout underneath.
   */
  let { children } = $props();

  const route = $derived(page.url.pathname);
  const onLogin = $derived(route === resolve('/login') || route === `${resolve('/login')}/`);

  settings.applyTheme();

  // A 401 that survived a refresh means the session is over. The HTTP layer knows when; only this
  // layer can decide what to put on the screen about it.
  setUnauthorizedHandler(() => {
    if (!session.isSignedIn) return;

    session.expire();
    workspace.reset();
    void realtime.disconnect();
  });

  // Resume a stored session before deciding anything, so a reload does not flash the sign-in screen
  // at someone who is already signed in.
  $effect(() => {
    if (session.status === 'unknown') void session.restore();
  });

  $effect(() => {
    if (session.status === 'signed-out' && !onLogin) {
      void goto(resolve('/login'), { replaceState: true });
    }

    if (session.status === 'signed-in' && onLogin) {
      void goto(resolve('/'), { replaceState: true });
    }
  });

  // One socket for the application's lifetime, opened once signed in and closed on the way out.
  $effect(() => {
    if (session.isSignedIn) {
      void realtime.connect();
    } else {
      void realtime.disconnect();
    }
  });
</script>

<svelte:head>
  <title>{page.data.title ?? 'Planner'}</title>
</svelte:head>

{#if session.status === 'unknown' || session.status === 'restoring'}
  <div class="booting">
    <Icon name="loader-circle" size={22} class="spin" />
    <p>Resuming your session…</p>
  </div>
{:else if session.isSignedIn && session.suggestPasskeySetup}
  <PasskeySetup onComplete={() => { session.suggestPasskeySetup = false; }} />
{:else}
  {@render children()}
{/if}

<ConfirmHost />
<LightboxHost />
<ToastHost />

<style>
  .booting {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--fg-tertiary);
  }
</style>
