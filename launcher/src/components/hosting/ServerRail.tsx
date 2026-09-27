import type { ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import { stateLabel } from "./format";
import { StateDot } from "./ui";
import { ManagedHostingRailNote } from "./ManagedHostingCta";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

interface ServerRailProps {
  servers: ServerSummary[];
  selectedId: string | null;
  creating: boolean;
  onSelect: (id: string) => void;
  onNew: () => void;
}

export default function ServerRail({ servers, selectedId, creating, onSelect, onNew }: ServerRailProps) {
  return (
    <nav className={styles.rail} aria-label="Your servers">
      <div className={styles.railList}>
        {servers.map(({ config, runtime }) => (
          <button
            key={config.id}
            className={cn(styles.railItem, !creating && selectedId === config.id && styles.railItemActive)}
            aria-current={!creating && selectedId === config.id ? "page" : undefined}
            title={`${config.name} · ${stateLabel(runtime.state)}`}
            onClick={() => onSelect(config.id)}
          >
            <StateDot state={runtime.state} />
            <span className={styles.railName}>{config.name}</span>
            {runtime.state === "running" && (
              <span className={styles.railPlayers}>
                {runtime.players.length}/{config.maxPlayers}
              </span>
            )}
          </button>
        ))}
      </div>
      <ManagedHostingRailNote />
      <div className={styles.railFooter}>
        <button className={cn(ui.btn, creating && ui.choiceSelected)} onClick={onNew}>
          + New server
        </button>
      </div>
    </nav>
  );
}
