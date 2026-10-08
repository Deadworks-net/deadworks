import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import type { NetworkMode, ServerState } from "@/lib/hosting";
import { errorMessage, stateLabel, stateTone } from "./format";
import ui from "./ui.module.css";

/** Runs one async action at a time and turns failures into a plain sentence. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async (fn: () => Promise<unknown>): Promise<boolean> => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      return true;
    } catch (e) {
      setError(errorMessage(e));
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const clearError = useCallback(() => setError(null), []);
  return { run, busy, error, clearError };
}

export function Toggle({
  checked,
  onChange,
  disabled,
  label,
}: {
  checked: boolean;
  onChange: (next: boolean) => void;
  disabled?: boolean;
  label: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      className={cn(ui.toggle, checked && ui.toggleOn)}
      onClick={() => onChange(!checked)}
    >
      <span className={ui.toggleThumb} />
    </button>
  );
}

export function SwitchRow({
  title,
  description,
  checked,
  onChange,
  disabled,
}: {
  title: string;
  description: ReactNode;
  checked: boolean;
  onChange: (next: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <div className={ui.switchRow}>
      <div>
        <div className={ui.switchTitle}>{title}</div>
        <div className={ui.switchDesc}>{description}</div>
      </div>
      <Toggle checked={checked} onChange={onChange} disabled={disabled} label={title} />
    </div>
  );
}

/** `value` is 0..1, or null for an indeterminate bar. */
export function ProgressBar({ value }: { value: number | null }) {
  return (
    <div className={ui.progressTrack}>
      <div
        className={cn(ui.progressBar, value == null && ui.progressIndeterminate)}
        style={value != null ? { width: `${Math.min(100, Math.max(0, value * 100))}%` } : undefined}
      />
    </div>
  );
}

export function Loading({ label }: { label?: string }) {
  return (
    <div className={ui.centered}>
      <span className={ui.loadingDots} aria-hidden>
        <span />
        <span />
        <span />
      </span>
      {label && <div className={ui.emptyText}>{label}</div>}
    </div>
  );
}

/** A plain sentence plus exactly one action. */
export function ErrorNote({
  message,
  actionLabel,
  onAction,
  warn,
}: {
  message: string;
  actionLabel?: string;
  onAction?: () => void;
  warn?: boolean;
}) {
  return (
    <div className={warn ? ui.warnBox : ui.errorBox} role="alert">
      <span className={ui.errorBoxText}>{message}</span>
      {actionLabel && onAction && (
        <button type="button" className={cn(ui.btn, ui.btnSmall)} onClick={onAction}>
          {actionLabel}
        </button>
      )}
    </div>
  );
}

export function StateDot({ state }: { state: ServerState }) {
  return <span className={cn(ui.dot, ui[`tone-${stateTone(state)}`])} aria-hidden />;
}

export function StateBadge({ state }: { state: ServerState }) {
  const tone = stateTone(state);
  return (
    <span className={cn(ui.pill, ui[`textTone-${tone}`])}>
      <StateDot state={state} />
      {stateLabel(state)}
    </span>
  );
}

const modalStack: object[] = [];

export function Modal({
  title,
  onClose,
  children,
  actions,
  wide,
  medium,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  actions?: ReactNode;
  /** Full-height editor dialog. */
  wide?: boolean;
  /** Form dialog: wider than a confirm, and its body scrolls when the form is tall. */
  medium?: boolean;
}) {
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const token = {};
    modalStack.push(token);
    const onKey = (e: KeyboardEvent) => {
      // Stacked dialogs (a confirm over an editor): only the top one closes.
      if (e.key === "Escape" && modalStack[modalStack.length - 1] === token) onCloseRef.current();
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      modalStack.splice(modalStack.indexOf(token), 1);
    };
  }, []);

  return (
    <div className={ui.overlay} onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className={cn(ui.dialog, wide && ui.dialogWide, medium && ui.dialogMedium)}
        role="dialog"
        aria-modal
        aria-label={title}
      >
        <h3 className={ui.dialogTitle}>{title}</h3>
        <div className={ui.dialogBody}>{children}</div>
        {actions && <div className={ui.dialogActions}>{actions}</div>}
      </div>
    </div>
  );
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel,
  danger,
  onConfirm,
  onClose,
}: {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  /** Rejections are shown in the dialog; success closes it. */
  onConfirm: () => Promise<unknown>;
  onClose: () => void;
}) {
  const { run, busy, error } = useAction();
  const confirm = async () => {
    if (await run(onConfirm)) onClose();
  };
  return (
    <Modal
      title={title}
      onClose={busy ? () => {} : onClose}
      actions={
        <>
          <button type="button" className={ui.btn} onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className={danger ? cn(ui.btn, ui.btnDanger) : ui.btnPrimary}
            onClick={confirm}
            disabled={busy}
            autoFocus
          >
            {busy ? "Working..." : confirmLabel}
          </button>
        </>
      }
    >
      {message}
      {error && <div className={ui.errorText}>{error}</div>}
    </Modal>
  );
}

const NETWORK_CHOICES: { mode: NetworkMode; title: string; desc: string }[] = [
  {
    mode: "sdr",
    title: "SDR",
    desc: "Routes traffic through Steam Datagram Relay. Requires no port forwarding. The address changes on every restart.",
  },
  {
    mode: "port_forward",
    title: "Port forwarding",
    desc: "Accepts direct connections on a fixed address. Requires a forwarded UDP port.",
  },
  {
    mode: "lan",
    title: "LAN",
    desc: "Accepts connections from the local network only.",
  },
];

export function NetworkCards({
  value,
  onChange,
  disabled,
}: {
  value: NetworkMode;
  onChange: (mode: NetworkMode) => void;
  disabled?: boolean;
}) {
  return (
    <div className={ui.choiceGrid} role="radiogroup" aria-label="Network">
      {NETWORK_CHOICES.map((c) => (
        <button
          key={c.mode}
          type="button"
          role="radio"
          aria-checked={value === c.mode}
          disabled={disabled}
          className={cn(ui.choice, value === c.mode && ui.choiceSelected)}
          onClick={() => onChange(c.mode)}
        >
          <span className={ui.choiceTitle}>{c.title}</span>
          <span className={ui.choiceDesc}>{c.desc}</span>
        </button>
      ))}
    </div>
  );
}
