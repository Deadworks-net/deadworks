import { cn } from "@/lib/utils";
import { ManagedHostingCard } from "./ManagedHostingCta";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

export default function WelcomeScreen({ onStart }: { onStart: () => void }) {
  return (
    <div className={styles.card}>
      <div className={ui.centered}>
        <svg width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" strokeWidth="1.5" aria-hidden>
          <rect x="3" y="4" width="18" height="7" rx="1" />
          <rect x="3" y="13" width="18" height="7" rx="1" />
          <circle cx="7" cy="7.5" r="0.8" fill="var(--accent)" />
          <circle cx="7" cy="16.5" r="0.8" fill="var(--accent)" />
        </svg>
        <h2 className={styles.welcomeTitle}>Host your own server</h2>
        <p className={styles.welcomeText}>
          Run a Deadworks server on this PC with your own plugins and settings. Setup takes a few
          minutes and only happens once.
        </p>
        <button className={cn(ui.btnPrimary, ui.btnLarge)} onClick={onStart} autoFocus>
          Get started
        </button>
        <ManagedHostingCard />
      </div>
    </div>
  );
}
