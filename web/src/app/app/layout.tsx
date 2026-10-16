import { Shell, appNav } from "@/components/shell";

export default function FactoryLayout({ children }: { children: React.ReactNode }) {
  return (
    <Shell nav={appNav} area="app">
      {children}
    </Shell>
  );
}
