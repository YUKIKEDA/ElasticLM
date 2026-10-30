// License keys (design §9.5): ELM- and four groups of five Crockford base32 characters. The screen never sends a key: it
// sends the SHA-256 of the key's normal form, which is what the service stores (design §14.5, ?keyHash=).
const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/** Returns the normal form of a typed key (upper case, without "-"), or null when the text is not a key. */
export function normalizeKey(typed: string): string | null {
  const compact = typed.trim().replaceAll('-', '').toUpperCase();
  return compact.length === 23 && compact.startsWith('ELM') && [...compact.slice(3)].every((c) => alphabet.includes(c)) ? compact : null;
}

/** Returns the base64url (no padding) SHA-256 of a key's normal form, or null when the text is not a key. */
export async function keyHash(typed: string): Promise<string | null> {
  const normal = normalizeKey(typed);
  if (normal === null) {
    return null;
  }

  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(normal)));
  let binary = '';
  digest.forEach((b) => (binary += String.fromCharCode(b)));
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}
