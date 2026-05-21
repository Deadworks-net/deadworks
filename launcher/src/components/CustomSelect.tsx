import { useState, useRef, useEffect } from "react";
import { cn } from "@/lib/utils";
import styles from "./CustomSelect.module.css";

interface Option {
  value: string;
  label: string;
}

interface CustomSelectProps {
  value: string;
  options: Option[];
  onChange: (value: string) => void;
}

export default function CustomSelect({ value, options, onChange }: CustomSelectProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [dropDirection, setDropDirection] = useState<"down" | "up">("down");
  const containerRef = useRef<HTMLDivElement>(null);

  const selected = options.find((o) => o.value === value) || options[0];

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    }
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  const handleToggle = () => {
    if (!isOpen && containerRef.current) {
      const rect = containerRef.current.getBoundingClientRect();
      const spaceBelow = window.innerHeight - rect.bottom;
      const spaceAbove = rect.top;
      
      const expectedHeight = options.length * 36 + 10;

      if (spaceBelow < expectedHeight && spaceAbove > spaceBelow) {
        setDropDirection("up");
      } else {
        setDropDirection("down");
      }
    }
    setIsOpen(!isOpen);
  };

  return (
    <div className={styles.container} ref={containerRef}>
      <button
        type="button"
        className={cn(styles.trigger, isOpen && styles.open)}
        onClick={handleToggle}
      >
        <span>{selected.label}</span>
        <svg
          className={styles.icon}
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <polyline points="6 9 12 15 18 9" />
        </svg>
      </button>
      
      {isOpen && (
        <div className={cn(styles.dropdown, dropDirection === "up" && styles.dropUp)}>
          {options.map((opt) => (
            <button
              key={opt.value}
              type="button"
              className={cn(styles.option, opt.value === value && styles.selected)}
              onClick={() => {
                onChange(opt.value);
                setIsOpen(false);
              }}
            >
              {opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
