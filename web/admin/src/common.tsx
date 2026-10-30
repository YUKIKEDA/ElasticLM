import type { ComponentChildren } from 'preact';
import { Problem, SignInNeeded } from './api';
import { m } from './messages';

// design §15.2 (0.100): every value from the API is drawn as text only; nothing builds markup, href, or src from it.

export function ErrorBanner({ error }: { error: Error | null }) {
  if (!error || error instanceof SignInNeeded) {
    return null;
  }

  const text = error instanceof Problem && error.status === 403 ? m.forbidden : error.message;
  return (
    <p class="error" role="alert">
      {text}
    </p>
  );
}

/** A question with a button that does it and one that does not. */
export function Confirm({ text, yes, onYes, onNo }: { text: string; yes: string; onYes: () => void; onNo: () => void }) {
  return (
    <p class="confirm">
      {text}{' '}
      <button type="button" onClick={onYes}>
        {yes}
      </button>{' '}
      <button type="button" onClick={onNo}>
        {m.cancel}
      </button>
    </p>
  );
}

/** A datetime-local field read as UTC (design §15.2, M5d1). */
export function UtcField({ name, label, children }: { name: string; label: string; children?: ComponentChildren }) {
  return (
    <label>
      {label} ({m.utc}) <input type="datetime-local" name={name} />
      {children}
    </label>
  );
}
