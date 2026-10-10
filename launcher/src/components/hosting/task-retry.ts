import type { TaskProgress } from "@/lib/hosting";
import type { HostingActions } from "./use-hosting";
import type { InstallDraft } from "./InstallScreen";

/**
 * What "Try again" does for a failed task. A SteamCMD install needs the
 * password again, which we never keep, so it goes back to the form.
 */
export function retryTask(
  task: TaskProgress,
  actions: HostingActions,
  lastInstall: InstallDraft | null,
  showInstallForm: () => void
) {
  switch (task.kind) {
    case "install":
      if (lastInstall && lastInstall.source === "client") actions.install(lastInstall);
      else {
        actions.dismissTask();
        showInstallForm();
      }
      break;
    case "update":
      actions.applyUpdates();
      break;
    case "verify":
      actions.verify();
      break;
    case "uninstall":
      actions.uninstall();
      break;
  }
}
