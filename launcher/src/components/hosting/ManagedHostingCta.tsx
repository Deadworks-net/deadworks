import { useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { cn } from "@/lib/utils";
import ui from "./ui.module.css";
import styles from "./ManagedHostingCta.module.css";

// Deadworks Hosting, the rented always-on servers sold on deadworks.net. The
// figures mirror deadworks-web `app/hosting/catalog.js` (cheapest preset and
// the location list); update them together.
const RENT_URL = "https://deadworks.net/app/servers/new";
const PLANS_URL = "https://deadworks.net/#hosting";
const FROM_PRICE = "$16.40/mo";
const REGIONS = 12;
const DISMISS_KEY = "hosting.managedCta.dismissed";

function open(url: string) {
  openUrl(url).catch(() => undefined);
}

/** Full card for the welcome screen, next to "host it yourself". */
export function ManagedHostingCard() {
  return (
    <div className={styles.card}>
      <div className={styles.badge}>Deadworks Hosting</div>
      <h3 className={styles.title}>Want it online 24/7?</h3>
      <p className={styles.text}>
        Rent a server that stays up when your PC is off. Same plugins, managed updates, a fixed address.
      </p>
      <ul className={styles.points}>
        <li>From {FROM_PRICE}</li>
        <li>{REGIONS} regions</li>
        <li>Up to 31 players</li>
      </ul>
      <div className={styles.actions}>
        <button className={ui.btn} onClick={() => open(RENT_URL)}>
          Rent a server
        </button>
        <button className={ui.linkBtn} onClick={() => open(PLANS_URL)}>
          See plans
        </button>
      </div>
    </div>
  );
}

/** Compact, dismissible reminder under the server list. */
export function ManagedHostingRailNote() {
  const [hidden, setHidden] = useState(() => {
    try {
      return localStorage.getItem(DISMISS_KEY) === "1";
    } catch {
      return false;
    }
  });
  if (hidden) return null;
  const dismiss = () => {
    setHidden(true);
    try {
      localStorage.setItem(DISMISS_KEY, "1");
    } catch {
      // Remembering the dismissal is a nicety.
    }
  };
  return (
    <div className={styles.note}>
      <button className={styles.close} onClick={dismiss} aria-label="Hide">
        ×
      </button>
      <div className={styles.noteTitle}>Keep it online 24/7</div>
      <div className={styles.noteText}>
        Managed hosting from {FROM_PRICE}.
      </div>
      <button className={cn(ui.linkBtn, styles.noteLink)} onClick={() => open(RENT_URL)}>
        Rent a server →
      </button>
    </div>
  );
}
