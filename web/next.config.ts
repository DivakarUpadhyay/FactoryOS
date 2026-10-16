import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Production builds (deploy/publish.ps1 sets FOS_DIST) go to their own folder, so building never overwrites the
  // files of a running development server ("npm run dev" uses .next).
  distDir: process.env.FOS_DIST ?? ".next",
  // A self-contained server (node server.js) for on-premise installs and containers
  output: "standalone",
};

export default nextConfig;
