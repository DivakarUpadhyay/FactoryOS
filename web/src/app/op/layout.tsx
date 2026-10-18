import type { Metadata, Viewport } from "next";

export const metadata: Metadata = {
  title: "FactoryOS shop floor",
  description: "Good and scrap counts, downtime reasons and inspections from the shop floor.",
  manifest: "/manifest.webmanifest",
  icons: { icon: "/icon.svg", apple: "/icon.svg" },
  appleWebApp: { capable: true, title: "FactoryOS", statusBarStyle: "default" },
};

export const viewport: Viewport = {
  themeColor: "#0f766e",
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
};

export default function OperatorLayout({ children }: { children: React.ReactNode }) {
  return children;
}
