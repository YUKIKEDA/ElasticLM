// Times and files of the daily screens (design §15.2 "M5d1 の形").

const local = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(:\d{2})?$/;

/**
 * Returns the API's time form (yyyy-MM-ddTHH:mm:ssZ) of a datetime-local value read as UTC, or null when the value has
 * another form. A value without seconds gets ":00".
 */
export function utcOf(value: string): string | null {
  const found = local.exec(value);
  return found ? `${found[1]}${found[2] ?? ':00'}Z` : null;
}

/** Returns a time in the API's form, without milliseconds. */
export function apiTime(ms: number): string {
  return new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/** Returns the number of Unicode scalar values of a text, as the service counts a license name. */
export function scalars(text: string): number {
  return [...text].length;
}

/** Returns the name of the answer to an offline request file: acme.elmreq gives acme.elmresp; any other name gets .elmresp added. */
export function answerName(requestName: string): string {
  return requestName.endsWith('.elmreq') ? `${requestName.slice(0, -'.elmreq'.length)}.elmresp` : `${requestName}.elmresp`;
}

/** The largest offline request file the service takes, in bytes (design §12.8.4). */
export const offlineLimit = 65_536;

/** Makes the browser save bytes under a file name. */
export function save(bytes: Blob, name: string): void {
  const url = URL.createObjectURL(bytes);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  // design §15.2 (M5d1): the URL is released after the click has been handled, on the next task.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
