import { send } from './api'

// The browser's WebAuthn calls want raw bytes where the server sends and takes text (base64url). These helpers do the converting both ways.

function toBytes(text: string): ArrayBuffer {
  const padded = text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes.buffer
}

function toText(buffer: ArrayBuffer): string {
  let binary = ''
  for (const b of new Uint8Array(buffer)) binary += String.fromCharCode(b)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

interface Descriptor {
  id: string
  type: PublicKeyCredentialType
  transports?: AuthenticatorTransport[]
}

interface CreateOptionsJson {
  challenge: string
  rp: PublicKeyCredentialRpEntity
  user: { id: string; name: string; displayName: string }
  pubKeyCredParams: PublicKeyCredentialParameters[]
  timeout?: number
  excludeCredentials?: Descriptor[]
  authenticatorSelection?: AuthenticatorSelectionCriteria
  attestation?: AttestationConveyancePreference
}

interface GetOptionsJson {
  challenge: string
  rpId?: string
  timeout?: number
  allowCredentials?: Descriptor[]
  userVerification?: UserVerificationRequirement
}

const descriptors = (list?: Descriptor[]): PublicKeyCredentialDescriptor[] | undefined =>
  list?.map((d) => ({ ...d, id: toBytes(d.id) }))

export const passkeysAvailable = () => typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials

/** A person's cancel, or a device that has no passkey for this site, is not an error to shout about. */
function explain(e: unknown): Error {
  if (e instanceof DOMException) {
    if (e.name === 'NotAllowedError') return new Error('The passkey was cancelled or timed out. Try again.')
    if (e.name === 'InvalidStateError') return new Error('This device already has a passkey for Tideline.')
    if (e.name === 'SecurityError') return new Error('Passkeys only work on the address Tideline is set up for. Open it the way you normally do.')
  }
  return e instanceof Error ? e : new Error(String(e))
}

/** Ask the server for a challenge, let the device make a passkey, and send the result back. Returns what the server said. */
export async function addPasskey<T>(label: string): Promise<T> {
  const options = await send<CreateOptionsJson>('POST', '/auth/enroll/passkey/options')
  let credential: PublicKeyCredential | null
  try {
    credential = (await navigator.credentials.create({
      publicKey: {
        ...options,
        challenge: toBytes(options.challenge),
        user: { ...options.user, id: toBytes(options.user.id) },
        excludeCredentials: descriptors(options.excludeCredentials),
      },
    })) as PublicKeyCredential | null
  } catch (e) {
    throw explain(e)
  }
  if (!credential) throw new Error('No passkey was made.')

  const response = credential.response as AuthenticatorAttestationResponse
  return send<T>('POST', '/auth/enroll/passkey/verify', {
    label,
    credential: {
      id: credential.id,
      rawId: toText(credential.rawId),
      type: credential.type,
      response: { attestationObject: toText(response.attestationObject), clientDataJSON: toText(response.clientDataJSON) },
      clientExtensionResults: credential.getClientExtensionResults(),
    },
  })
}

/** The second step with a passkey: the device signs the server's challenge. */
export async function signInWithPasskey<T>(): Promise<T> {
  const options = await send<GetOptionsJson>('POST', '/auth/mfa/passkey/options')
  let credential: PublicKeyCredential | null
  try {
    credential = (await navigator.credentials.get({
      publicKey: { ...options, challenge: toBytes(options.challenge), allowCredentials: descriptors(options.allowCredentials) },
    })) as PublicKeyCredential | null
  } catch (e) {
    throw explain(e)
  }
  if (!credential) throw new Error('No passkey was chosen.')

  const response = credential.response as AuthenticatorAssertionResponse
  return send<T>('POST', '/auth/mfa/passkey/verify', {
    credential: {
      id: credential.id,
      rawId: toText(credential.rawId),
      type: credential.type,
      response: {
        authenticatorData: toText(response.authenticatorData),
        clientDataJSON: toText(response.clientDataJSON),
        signature: toText(response.signature),
        userHandle: response.userHandle ? toText(response.userHandle) : null,
      },
      clientExtensionResults: credential.getClientExtensionResults(),
    },
  })
}
