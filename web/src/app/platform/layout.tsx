import { Shell, platformNav } from "@/components/shell";

export default function PlatformLayout({ children }: { children: React.ReactNode }) {
  return (
    <Shell nav={platformNav} area="platform">
      {children}
    </Shell>
  );
}
