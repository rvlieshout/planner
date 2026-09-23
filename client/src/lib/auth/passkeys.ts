import { request } from '$lib/api/http';

export interface SavedPasskey { id: string; name: string | null; createdAt: string }

export function passkeysAvailable(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext &&
    typeof PublicKeyCredential !== 'undefined' && !!navigator.credentials;
}

// Only prompt automatically when this device can enroll a passkey. External security keys
// remain available through Preferences even when there is no platform authenticator.
export async function passkeySetupAvailable(): Promise<boolean> {
  if (!passkeysAvailable() || typeof navigator.credentials.create !== 'function' ||
      typeof PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable !== 'function') return false;
  try {
    return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
  } catch {
    // Capability detection must never prevent password sign-in.
    return false;
  }
}

export function passkeyError(error: unknown): string {
  if (error instanceof DOMException) {
    if (error.name === 'NotAllowedError' || error.name === 'AbortError')
      return 'Passkey sign-in was cancelled or timed out. Try again, or use setup and recovery.';
    if (error.name === 'InvalidStateError') return 'This passkey is already registered. Use it to sign in or choose another device.';
    if (error.name === 'SecurityError') return 'Passkeys require HTTPS (or localhost) and the same workspace address used during setup.';
  }
  return error instanceof Error ? error.message : 'The passkey operation failed. Please try again.';
}

function requireSupport() {
  if (!passkeysAvailable()) throw new Error('Passkeys need a supported browser over HTTPS (or localhost). Use setup and recovery to sign in.');
}

// Decode explicitly so browsers with WebAuthn but without the newer JSON helpers work too.
function decode(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '=')), c => c.charCodeAt(0)).buffer;
}
function encode(value: ArrayBuffer): string {
  return btoa(String.fromCharCode(...new Uint8Array(value))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
interface DescriptorJson { id: string; type: 'public-key'; transports?: AuthenticatorTransport[] }
function descriptor(value: DescriptorJson): PublicKeyCredentialDescriptor {
  return { ...value, id: decode(value.id) };
}
type CreationJson = Omit<PublicKeyCredentialCreationOptions, 'challenge' | 'user' | 'excludeCredentials'> & {
  challenge: string; user: { id: string; name: string; displayName: string }; excludeCredentials?: DescriptorJson[];
};
type RequestJson = Omit<PublicKeyCredentialRequestOptions, 'challenge' | 'allowCredentials'> & {
  challenge: string; allowCredentials?: DescriptorJson[];
};
function serialize(credential: PublicKeyCredential) {
  const response = credential.response;
  const common = {
    id: credential.id, rawId: encode(credential.rawId), type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment,
    clientExtensionResults: credential.getClientExtensionResults()
  };
  if (response instanceof AuthenticatorAttestationResponse) {
    return { ...common, response: {
      clientDataJSON: encode(response.clientDataJSON), attestationObject: encode(response.attestationObject),
      transports: response.getTransports?.() ?? []
    } };
  }
  const assertion = response as AuthenticatorAssertionResponse;
  return { ...common, response: {
    clientDataJSON: encode(assertion.clientDataJSON), authenticatorData: encode(assertion.authenticatorData),
    signature: encode(assertion.signature), userHandle: assertion.userHandle ? encode(assertion.userHandle) : null
  } };
}

export async function assertPasskey(): Promise<string> {
  requireSupport();
  const options = await request<RequestJson>('/connect/passkey/options', { method: 'POST', anonymous: true, body: {} });
  const credential = await navigator.credentials.get({ publicKey: {
    ...options, challenge: decode(options.challenge), allowCredentials: options.allowCredentials?.map(descriptor)
  } });
  if (!(credential instanceof PublicKeyCredential)) throw new Error('No passkey was selected. Try again.');
  return JSON.stringify(serialize(credential));
}

export const passkeys = {
  list: () => request<SavedPasskey[]>('/api/v1/me/passkeys'),
  async add(name: string) {
    requireSupport();
    const options = await request<CreationJson>('/api/v1/me/passkeys/options', { method: 'POST', body: {} });
    const credential = await navigator.credentials.create({ publicKey: {
      ...options, challenge: decode(options.challenge), user: { ...options.user, id: decode(options.user.id) },
      excludeCredentials: options.excludeCredentials?.map(descriptor)
    } });
    if (!(credential instanceof PublicKeyCredential)) throw new Error('No passkey was created. Try again.');
    await request<void>('/api/v1/me/passkeys', { method: 'POST', body: { name, credential: serialize(credential) } });
  },
  remove: (id: string) => request<void>(`/api/v1/me/passkeys/${encodeURIComponent(id)}`, { method: 'DELETE' })
};
