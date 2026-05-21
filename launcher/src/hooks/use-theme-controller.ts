import { useEffect } from "react";
import { useSettings } from "@/hooks/use-settings";

export function useThemeController() {
  const { theme } = useSettings();

  useEffect(() => {
    const root = document.documentElement;

    const applyTheme = (t: string) => {
      if (t === "system") {
        const systemDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
        root.setAttribute("data-theme", systemDark ? "dark" : "light");
      } else {
        root.setAttribute("data-theme", t);
      }
    };

    applyTheme(theme);

    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
    const handleChange = () => {
      if (theme === "system") applyTheme("system");
    };

    mediaQuery.addEventListener("change", handleChange);
    return () => mediaQuery.removeEventListener("change", handleChange);
  }, [theme]);
}
