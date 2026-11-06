// Brand mark: a sawtooth factory roof with a chimney sending a live signal.
// Keep in sync with public/icon.svg.
export function LogoMark({ className = "h-9 w-9" }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} role="img" aria-label="Divakar FactoryOS">
      <rect width="32" height="32" rx="8" fill="#0f766e" />
      <path d="M6 25V15l5 3v-3l5 3v-3l5 3V9h4v16z" fill="#fff" />
      <path d="M20.8 7.2A2.6 2.6 0 0 1 25.2 7.2M19.1 6A4.5 4.5 0 0 1 26.9 6" stroke="#fbbf24" strokeWidth="1.4" fill="none" strokeLinecap="round" />
      <rect x="8.5" y="20" width="2.5" height="2.5" rx=".5" fill="#0f766e" />
      <rect x="13.5" y="20" width="2.5" height="2.5" rx=".5" fill="#0f766e" />
    </svg>
  );
}
