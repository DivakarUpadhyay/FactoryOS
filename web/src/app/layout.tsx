import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import { AuthProvider } from "@/lib/auth";
import "./globals.css";

const geistSans = Geist({ variable: "--font-geist-sans", subsets: ["latin"] });
const geistMono = Geist_Mono({ variable: "--font-geist-mono", subsets: ["latin"] });

export const metadata: Metadata = {
  title: "Divakar FactoryOS",
  description: "Smart manufacturing platform: plant, energy, power, production, robotics, maintenance and quality in one place.",
  icons: { icon: "/factory.png", shortcut: "/factory.png", apple: "/factory.png" },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" suppressHydrationWarning data-scroll-behavior="smooth">
      <head>
        {/* apply the saved theme before the first paint, so a refresh never flashes the wrong colours */}
        <script dangerouslySetInnerHTML={{ __html: "try{var t=localStorage.getItem('factoryos.theme');if(t)document.documentElement.dataset.theme=t}catch(e){}" }} />
      </head>
      <body className={`${geistSans.variable} ${geistMono.variable} antialiased`}>
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  );
}
