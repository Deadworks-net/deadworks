import { cn } from "@/lib/utils";
import styles from "./MainNav.module.css";

export type MainTab = "servers" | "host";

const TABS: { id: MainTab; label: string }[] = [
  { id: "servers", label: "SERVERS" },
  { id: "host", label: "HOST" },
];

interface MainNavProps {
  tab: MainTab;
  onChange: (tab: MainTab) => void;
}

/** Brand + the top-level tab row. Each page renders it at the left of its toolbar. */
export default function MainNav({ tab, onChange }: MainNavProps) {
  return (
    <div className={styles.nav}>
      <span className={styles.brandText}>Deadworks</span>
      <div className={styles.tabs} role="tablist">
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            aria-selected={tab === t.id}
            className={cn(styles.tab, tab === t.id && styles.tabActive)}
            onClick={() => onChange(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>
    </div>
  );
}
